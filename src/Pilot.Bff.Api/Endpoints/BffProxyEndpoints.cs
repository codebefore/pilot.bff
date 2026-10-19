using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Pilot.Bff.Api.Auth;
using Pilot.Bff.Api.Options;
using Pilot.Bff.Api.Proxy;
using Pilot.Bff.Api.Security;

namespace Pilot.Bff.Api.Endpoints;

public static class BffProxyEndpoints
{
    private static readonly string[] ForwardedResponseHeaders =
        [HeaderNames.ContentType, HeaderNames.ContentLength, HeaderNames.ContentDisposition,
         HeaderNames.ETag, HeaderNames.LastModified, HeaderNames.AcceptRanges,
         HeaderNames.ContentRange, HeaderNames.RetryAfter];

    public static IEndpointRouteBuilder MapBffProxyEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapMethods(
                "/bff/v1/{service}/{**path}",
                [HttpMethods.Get, HttpMethods.Head],
                ProxyAsync)
            .AddEndpointFilter<SessionEnabledEndpointFilter>();
        endpoints.MapMethods(
                "/bff/v1/{service}/{**path}",
                [HttpMethods.Post, HttpMethods.Put, HttpMethods.Patch, HttpMethods.Delete],
                ProxyAsync)
            .AddEndpointFilter<SessionEnabledEndpointFilter>()
            .AddEndpointFilter<AntiforgeryEndpointFilter>();
        return endpoints;
    }

    private static async Task<IResult> ProxyAsync(
        string service,
        string? path,
        HttpContext context,
        BffAuthService auth,
        BffSessionCookie cookie,
        IBffProxyRoutePolicy routePolicy,
        IBffProxyRequestFactory requestFactory,
        IBffProxyRequestBodyPolicy bodyPolicy,
        IBffProxyQueryPolicy queryPolicy,
        IBffProxyTrafficLimiter trafficLimiter,
        IBffWebSocketProxy webSocketProxy,
        IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory,
        IOptions<BffProxyOptions> proxyOptions,
        CancellationToken cancellationToken)
    {
        if (!routePolicy.TryResolve(context.Request.Method, service, path, out var targetPath) ||
            context.Request.QueryString.Value?.Length > 4096 ||
            !queryPolicy.IsSafe(context.Request.Query))
        {
            SecurityEventLog.Rejected(
                loggerFactory.CreateLogger("Pilot.Bff.Security"),
                "proxy_route_rejected");
            return Results.NotFound();
        }

        var maxBody = bodyPolicy.GetMaxRequestBodyBytes(context.Request);
        if (context.Request.ContentLength > maxBody)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        var bodySizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodySizeFeature is { IsReadOnly: false })
        {
            bodySizeFeature.MaxRequestBodySize = maxBody;
        }

        var authorization = await auth.GetProxyAuthorizationAsync(
            cookie.Read(context.Request),
            cancellationToken);
        if (authorization.StatusCode != System.Net.HttpStatusCode.OK || authorization.Value is null)
        {
            if (authorization.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                cookie.Delete(context.Response);
            }

            return Results.StatusCode((int)authorization.StatusCode);
        }

        cookie.Append(context.Response, authorization.SessionId!, authorization.Value.IdleExpiresAtUtc);
        var mutation = routePolicy.IsMutation(context.Request.Method, targetPath);
        await using var trafficLease = await trafficLimiter.TryAcquireAsync(
            authorization.Value.UserId,
            authorization.Value.InstitutionId,
            context.Connection.RemoteIpAddress,
            mutation,
            cancellationToken);
        if (trafficLease is null)
        {
            SecurityEventLog.Rejected(
                loggerFactory.CreateLogger("Pilot.Bff.Security"),
                "proxy_rate_limited");
            context.Response.Headers.RetryAfter = "1";
            return Results.StatusCode(StatusCodes.Status429TooManyRequests);
        }

        if (context.WebSockets.IsWebSocketRequest)
        {
            await webSocketProxy.ProxyAsync(
                context,
                targetPath,
                authorization.Value,
                cancellationToken);
            return Results.Empty;
        }

        using var upstreamRequest = requestFactory.Create(context.Request, targetPath, authorization.Value);
        HttpResponseMessage upstreamResponse;
        try
        {
            upstreamResponse = await httpClientFactory.CreateClient(BffProxyInfrastructure.HttpClientName)
                .SendAsync(upstreamRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Results.StatusCode(StatusCodes.Status504GatewayTimeout);
        }
        catch (HttpRequestException)
        {
            return Results.StatusCode(StatusCodes.Status502BadGateway);
        }

        using (upstreamResponse)
        {
            if (upstreamResponse.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                await auth.InvalidateLocalSessionAsync(authorization.SessionId, CancellationToken.None);
                cookie.Delete(context.Response);
            }

            context.Response.StatusCode = (int)upstreamResponse.StatusCode;
            CopyResponseHeaders(upstreamResponse, context.Response);
            if (!HttpMethods.IsHead(context.Request.Method) && upstreamResponse.Content is not null)
            {
                using var streamTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                streamTimeout.CancelAfter(TimeSpan.FromSeconds(
                    proxyOptions.Value.ResponseStreamingTimeoutSeconds));
                try
                {
                    await upstreamResponse.Content.CopyToAsync(
                        context.Response.Body,
                        streamTimeout.Token);
                }
                catch (OperationCanceledException) when (
                    !cancellationToken.IsCancellationRequested &&
                    streamTimeout.IsCancellationRequested)
                {
                    context.Abort();
                }
            }
        }

        return Results.Empty;
    }

    private static void CopyResponseHeaders(HttpResponseMessage upstream, HttpResponse response)
    {
        foreach (var name in ForwardedResponseHeaders)
        {
            if (upstream.Headers.TryGetValues(name, out var headerValues) ||
                upstream.Content.Headers.TryGetValues(name, out headerValues))
            {
                response.Headers[name] = headerValues.ToArray();
            }
        }
    }
}
