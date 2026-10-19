using System.Net;
using Microsoft.AspNetCore.Antiforgery;
using Pilot.Bff.Api.Auth;
using Pilot.Bff.Api.Contracts;
using Pilot.Bff.Api.Security;
using StackExchange.Redis;

namespace Pilot.Bff.Api.Endpoints;

public static class BffAuthEndpoints
{
    public static IEndpointRouteBuilder MapBffAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/bff")
            .AddEndpointFilter<SessionEnabledEndpointFilter>();

        group.MapGet("/antiforgery", GetAntiforgeryToken);
        group.MapGet("/session", GetSessionAsync);
        group.MapPost("/release-grant", IssueReleaseGrantAsync)
            .AddEndpointFilter<AntiforgeryEndpointFilter>();

        group.MapPost("/auth/otp/request", RequestLoginCodeAsync)
            .AddEndpointFilter<AntiforgeryEndpointFilter>();
        group.MapPost("/auth/otp/verify", VerifyLoginCodeAsync)
            .AddEndpointFilter<AntiforgeryEndpointFilter>();
        group.MapPost("/session/institution", SelectInstitutionAsync)
            .AddEndpointFilter<AntiforgeryEndpointFilter>();
        group.MapPost("/auth/migration-access/request-code", RequestMigrationAccessCodeAsync)
            .AddEndpointFilter<AntiforgeryEndpointFilter>();
        group.MapPost("/auth/migration-access/verify-code", VerifyMigrationAccessCodeAsync)
            .AddEndpointFilter<AntiforgeryEndpointFilter>();
        group.MapPost("/logout", LogoutAsync)
            .AddEndpointFilter<AntiforgeryEndpointFilter>();

        return endpoints;
    }

    private static async Task<IResult> IssueReleaseGrantAsync(
        HttpContext context,
        ReleaseGrantService grants,
        BffSessionCookie cookie,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await grants.IssueAsync(cookie.Read(context.Request), cancellationToken);
            if (result.StatusCode != HttpStatusCode.OK)
            {
                SecurityEventLog.Rejected(
                    loggerFactory.CreateLogger("Pilot.Bff.Security"),
                    "release_grant_rejected");
            }
            if (result.StatusCode == HttpStatusCode.Unauthorized)
                cookie.Delete(context.Response);
            return result.Value is not null ? Results.Ok(result.Value) : Status(result.StatusCode);
        }
        catch (Exception exception) when (IsDependencyFailure(exception, cancellationToken))
        {
            SecurityEventLog.Rejected(
                loggerFactory.CreateLogger("Pilot.Bff.Security"),
                "release_grant_unavailable");
            return DependencyUnavailable();
        }
    }

    private static async Task<IResult> RequestMigrationAccessCodeAsync(
        HttpContext context,
        BffAuthService auth,
        BffSessionCookie cookie,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await auth.RequestMigrationAccessCodeAsync(
                cookie.Read(context.Request),
                cancellationToken);
            if (result.StatusCode == HttpStatusCode.Unauthorized)
            {
                cookie.Delete(context.Response);
            }

            return result.StatusCode == HttpStatusCode.OK && result.Value is not null
                ? Results.Ok(result.Value)
                : Status(result.StatusCode);
        }
        catch (Exception exception) when (IsDependencyFailure(exception, cancellationToken))
        {
            return DependencyUnavailable();
        }
    }

    private static async Task<IResult> VerifyMigrationAccessCodeAsync(
        VerifyMigrationAccessCodeRequest request,
        HttpContext context,
        BffAuthService auth,
        BffSessionCookie cookie,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await auth.VerifyMigrationAccessCodeAsync(
                cookie.Read(context.Request),
                request,
                cancellationToken);
            return result.StatusCode == HttpStatusCode.OK && result.Value is not null
                ? Results.Ok(result.Value)
                : Status(result.StatusCode);
        }
        catch (Exception exception) when (IsDependencyFailure(exception, cancellationToken))
        {
            return DependencyUnavailable();
        }
    }

    private static IResult GetAntiforgeryToken(HttpContext context, IAntiforgery antiforgery)
    {
        var tokens = antiforgery.GetAndStoreTokens(context);
        return Results.Ok(new AntiforgeryTokenResponse(tokens.RequestToken!));
    }

    private static async Task<IResult> RequestLoginCodeAsync(
        LoginOtpRequest request,
        BffAuthService auth,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await auth.RequestLoginCodeAsync(request, cancellationToken);
            return result.IsSuccess && result.Value is not null
                ? Results.Ok(result.Value)
                : Status(result.StatusCode);
        }
        catch (Exception exception) when (IsDependencyFailure(exception, cancellationToken))
        {
            return DependencyUnavailable();
        }
    }

    private static async Task<IResult> VerifyLoginCodeAsync(
        VerifyLoginOtpRequest request,
        HttpContext context,
        BffAuthService auth,
        BffSessionCookie cookie,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await auth.VerifyLoginCodeAsync(request, cancellationToken);
            if (result.StatusCode == HttpStatusCode.OK &&
                result.Value is not null &&
                result.SessionId is not null)
            {
                cookie.Append(context.Response, result.SessionId, result.Value.IdleExpiresAtUtc);
                return Results.Ok(result.Value);
            }

            return Status(result.StatusCode);
        }
        catch (Exception exception) when (IsDependencyFailure(exception, cancellationToken))
        {
            return DependencyUnavailable();
        }
    }

    private static async Task<IResult> GetSessionAsync(
        HttpContext context,
        BffAuthService auth,
        BffSessionCookie cookie,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await auth.GetSessionAsync(cookie.Read(context.Request), cancellationToken);
            if (result.StatusCode == HttpStatusCode.OK && result.Value is not null)
            {
                cookie.Append(context.Response, result.SessionId!, result.Value.IdleExpiresAtUtc);
                return Results.Ok(result.Value);
            }

            if (result.StatusCode == HttpStatusCode.Unauthorized)
            {
                cookie.Delete(context.Response);
            }

            return Status(result.StatusCode);
        }
        catch (Exception exception) when (IsDependencyFailure(exception, cancellationToken))
        {
            return DependencyUnavailable();
        }
    }

    private static async Task<IResult> SelectInstitutionAsync(
        SelectInstitutionRequest request,
        HttpContext context,
        BffAuthService auth,
        BffSessionCookie cookie,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await auth.SelectInstitutionAsync(
                cookie.Read(context.Request),
                request.InstitutionId,
                cancellationToken);
            if (result.StatusCode == HttpStatusCode.OK &&
                result.Value is not null &&
                result.SessionId is not null)
            {
                cookie.Append(context.Response, result.SessionId, result.Value.IdleExpiresAtUtc);
                return Results.Ok(result.Value);
            }

            if (result.StatusCode == HttpStatusCode.Unauthorized)
            {
                cookie.Delete(context.Response);
            }

            return Status(result.StatusCode);
        }
        catch (Exception exception) when (IsDependencyFailure(exception, cancellationToken))
        {
            return DependencyUnavailable();
        }
    }

    private static async Task<IResult> LogoutAsync(
        HttpContext context,
        BffAuthService auth,
        BffSessionCookie cookie,
        CancellationToken cancellationToken)
    {
        try
        {
            await auth.LogoutAsync(cookie.Read(context.Request), cancellationToken);
            return Results.NoContent();
        }
        catch (Exception exception) when (IsDependencyFailure(exception, cancellationToken))
        {
            return DependencyUnavailable();
        }
        finally
        {
            cookie.Delete(context.Response);
        }
    }

    private static IResult Status(HttpStatusCode statusCode)
    {
        return Results.StatusCode((int)statusCode);
    }

    private static IResult DependencyUnavailable()
    {
        return Results.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "An authentication dependency is unavailable.");
    }

    private static bool IsDependencyFailure(Exception exception, CancellationToken requestCancellation)
    {
        return exception is RedisException or HttpRequestException or TimeoutException ||
            exception is TaskCanceledException && !requestCancellation.IsCancellationRequested;
    }
}
