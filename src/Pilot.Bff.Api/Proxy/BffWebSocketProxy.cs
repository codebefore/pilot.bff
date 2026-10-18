using System.Net.WebSockets;
using Microsoft.Extensions.Options;
using Pilot.Bff.Api.Auth;
using Pilot.Bff.Api.Options;

namespace Pilot.Bff.Api.Proxy;

public interface IBffWebSocketProxy
{
    Task ProxyAsync(
        HttpContext context,
        string targetPath,
        BffProxyAuthorization authorization,
        CancellationToken cancellationToken);
}

public sealed class BffWebSocketProxy(IOptions<BffProxyOptions> options) : IBffWebSocketProxy
{
    public async Task ProxyAsync(
        HttpContext context,
        string targetPath,
        BffProxyAuthorization authorization,
        CancellationToken cancellationToken)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            throw new InvalidOperationException("A WebSocket upgrade request is required.");
        }

        using var upstream = new ClientWebSocket();
        upstream.Options.SetRequestHeader("Authorization", $"Bearer {authorization.AccessToken}");
        if (authorization.InstitutionId is { } institutionId)
        {
            upstream.Options.SetRequestHeader("X-Institution-Id", institutionId.ToString("D"));
        }

        foreach (var protocol in context.WebSockets.WebSocketRequestedProtocols)
        {
            upstream.Options.AddSubProtocol(protocol);
        }

        await upstream.ConnectAsync(CreateTargetUri(targetPath, context.Request.QueryString), cancellationToken);
        using var browser = await context.WebSockets.AcceptWebSocketAsync(upstream.SubProtocol);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            context.RequestAborted);
        var browserToUpstream = PumpAsync(browser, upstream, linked.Token);
        var upstreamToBrowser = PumpAsync(upstream, browser, linked.Token);

        await Task.WhenAny(browserToUpstream, upstreamToBrowser);
        linked.Cancel();
        try
        {
            await Task.WhenAll(browserToUpstream, upstreamToBrowser);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
        }
        catch (WebSocketException) when (linked.IsCancellationRequested)
        {
        }
    }

    private Uri CreateTargetUri(string targetPath, QueryString query)
    {
        var gateway = new Uri(options.Value.GatewayBaseUrl.TrimEnd('/') + '/', UriKind.Absolute);
        var builder = new UriBuilder(new Uri(gateway, targetPath.TrimStart('/') + query))
        {
            Scheme = gateway.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                ? "wss"
                : "ws"
        };
        return builder.Uri;
    }

    private static async Task PumpAsync(
        WebSocket source,
        WebSocket destination,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        while (!cancellationToken.IsCancellationRequested &&
               source.State is WebSocketState.Open or WebSocketState.CloseSent)
        {
            var result = await source.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                if (destination.State == WebSocketState.Open)
                {
                    await destination.CloseOutputAsync(
                        result.CloseStatus ?? WebSocketCloseStatus.NormalClosure,
                        result.CloseStatusDescription,
                        cancellationToken);
                }

                return;
            }

            if (destination.State != WebSocketState.Open)
            {
                return;
            }

            await destination.SendAsync(
                buffer.AsMemory(0, result.Count),
                result.MessageType,
                result.EndOfMessage,
                cancellationToken);
        }
    }
}
