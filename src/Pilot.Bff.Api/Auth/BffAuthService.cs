using System.Net;
using Microsoft.Extensions.Options;
using Pilot.Bff.Api.Contracts;
using Pilot.Bff.Api.Options;
using Pilot.Bff.Api.Session;

namespace Pilot.Bff.Api.Auth;

public sealed record BffAuthResult<T>(HttpStatusCode StatusCode, T? Value, string? SessionId = null);

public sealed record BffProxyAuthorization(
    Guid UserId,
    string AccessToken,
    Guid? InstitutionId,
    DateTime IdleExpiresAtUtc,
    string? MigrationAccessToken = null);

public sealed class BffAuthService(
    IIdentityAuthClient identity,
    IBffSessionStore sessions,
    IBffSessionLock sessionLock,
    IBffSessionIdGenerator sessionIds,
    IOptions<BffSessionOptions> options,
    TimeProvider timeProvider)
{
    public Task<IdentityAuthResult<LoginOtpChallengeResponse>> RequestLoginCodeAsync(
        LoginOtpRequest request,
        CancellationToken cancellationToken)
    {
        return identity.RequestLoginCodeAsync(request, cancellationToken);
    }

    public async Task<BffAuthResult<BffSessionResponse>> VerifyLoginCodeAsync(
        VerifyLoginOtpRequest request,
        CancellationToken cancellationToken)
    {
        var result = await identity.VerifyLoginCodeAsync(request, cancellationToken);
        if (!result.IsSuccess || result.Value is null)
        {
            return new(result.StatusCode, null);
        }

        var session = CreateSession(result.Value);
        var sessionId = sessionIds.Create();
        try
        {
            await sessions.SetAsync(sessionId, session, cancellationToken);
        }
        catch
        {
            try
            {
                await identity.LogoutAsync(session.RefreshToken, CancellationToken.None);
            }
            catch
            {
                // Preserve the original session-store failure. Identity refresh rotation/reuse
                // detection remains the final fail-closed boundary if compensation also fails.
            }

            throw;
        }
        return new(HttpStatusCode.OK, ToResponse(session), sessionId);
    }

    public async Task<BffAuthResult<BffSessionResponse>> GetSessionAsync(
        string? sessionId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return new(HttpStatusCode.Unauthorized, null);
        }

        var session = await sessions.GetAsync(sessionId, cancellationToken);
        if (session is null)
        {
            return new(HttpStatusCode.Unauthorized, null);
        }

        if (!RequiresMaintenance(session))
        {
            return new(HttpStatusCode.OK, ToResponse(session), sessionId);
        }

        await using var lease = await AcquireLockAsync(sessionId, cancellationToken);
        if (lease is null)
        {
            return new(HttpStatusCode.ServiceUnavailable, null);
        }

        var maintained = await MaintainSessionAsync(sessionId, cancellationToken);
        return maintained.Session is null
            ? new(maintained.StatusCode, null)
            : new(HttpStatusCode.OK, ToResponse(maintained.Session), sessionId);
    }

    public async Task<BffAuthResult<BffSessionResponse>> SelectInstitutionAsync(
        string? sessionId,
        Guid institutionId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || institutionId == Guid.Empty)
        {
            return new(HttpStatusCode.Unauthorized, null);
        }

        await using var lease = await AcquireLockAsync(sessionId, cancellationToken);
        if (lease is null)
        {
            return new(HttpStatusCode.ServiceUnavailable, null);
        }

        var maintained = await MaintainSessionAsync(sessionId, cancellationToken);
        if (maintained.Session is null)
        {
            return new(maintained.StatusCode, null);
        }

        var current = maintained.Session;

        var identityResult = await identity.SelectInstitutionAsync(
            institutionId,
            current.AccessToken,
            cancellationToken);
        if (!identityResult.IsSuccess || identityResult.Value is null)
        {
            return new(identityResult.StatusCode, null);
        }

        var replacement = CreateSession(identityResult.Value, current.CreatedAtUtc, current.AbsoluteExpiresAtUtc);
        var replacementId = sessionIds.Create();
        var rotated = await sessions.RotateAsync(
            sessionId,
            replacementId,
            replacement,
            cancellationToken);
        if (rotated)
        {
            return new(HttpStatusCode.OK, ToResponse(replacement), replacementId);
        }

        await BestEffortLogoutAsync(replacement.RefreshToken);
        return new(HttpStatusCode.Unauthorized, null);
    }

    public async Task<BffAuthResult<BffProxyAuthorization>> GetProxyAuthorizationAsync(
        string? sessionId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return new(HttpStatusCode.Unauthorized, null);
        }

        var current = await sessions.GetAsync(sessionId, cancellationToken);
        if (current is null)
        {
            return new(HttpStatusCode.Unauthorized, null);
        }

        if (RequiresMaintenance(current))
        {
            await using var lease = await AcquireLockAsync(sessionId, cancellationToken);
            if (lease is null)
            {
                return new(HttpStatusCode.ServiceUnavailable, null);
            }

            var maintained = await MaintainSessionAsync(sessionId, cancellationToken);
            if (maintained.Session is null)
            {
                return new(maintained.StatusCode, null);
            }

            current = maintained.Session;
        }

        return new(
            HttpStatusCode.OK,
            new BffProxyAuthorization(
                current.User.Id,
                current.AccessToken,
                current.ActiveInstitution?.Id,
                current.IdleExpiresAtUtc,
                ValidMigrationAccessToken(current)),
            sessionId);
    }

    public async Task<BffAuthResult<MigrationAccessChallengeResponse>> RequestMigrationAccessCodeAsync(
        string? sessionId,
        CancellationToken cancellationToken)
    {
        var authorization = await GetProxyAuthorizationAsync(sessionId, cancellationToken);
        if (authorization.StatusCode != HttpStatusCode.OK || authorization.Value is null)
        {
            return new(authorization.StatusCode, null);
        }

        var result = await identity.RequestMigrationAccessCodeAsync(
            authorization.Value.AccessToken,
            cancellationToken);
        return new(result.StatusCode, result.Value, authorization.SessionId);
    }

    public async Task<BffAuthResult<BffMigrationAccessResponse>> VerifyMigrationAccessCodeAsync(
        string? sessionId,
        VerifyMigrationAccessCodeRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return new(HttpStatusCode.Unauthorized, null);
        }

        await using var lease = await AcquireLockAsync(sessionId, cancellationToken);
        if (lease is null)
        {
            return new(HttpStatusCode.ServiceUnavailable, null);
        }

        var maintained = await MaintainSessionAsync(sessionId, cancellationToken);
        var current = maintained.Session;
        if (current is null || current.ActiveInstitution is null)
        {
            return new(maintained.StatusCode, null);
        }

        var result = await identity.VerifyMigrationAccessCodeAsync(
            request,
            current.AccessToken,
            cancellationToken);
        if (!result.IsSuccess || result.Value is null)
        {
            return new(result.StatusCode, null);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (string.IsNullOrWhiteSpace(result.Value.AccessToken) || result.Value.ExpiresAtUtc <= now)
        {
            throw new InvalidOperationException("Identity returned an invalid migration access response.");
        }

        var replacement = current with
        {
            MigrationAccessToken = result.Value.AccessToken,
            MigrationAccessExpiresAtUtc = result.Value.ExpiresAtUtc,
            MigrationAccessInstitutionId = current.ActiveInstitution.Id
        };
        if (!await sessions.TryUpdateAsync(sessionId, replacement, cancellationToken))
        {
            return new(HttpStatusCode.Unauthorized, null);
        }

        return new(
            HttpStatusCode.OK,
            new BffMigrationAccessResponse(result.Value.ExpiresAtUtc),
            sessionId);
    }

    public async Task LogoutAsync(string? sessionId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        await using var lease = await AcquireLockAsync(sessionId, cancellationToken);
        if (lease is null)
        {
            await sessions.DeleteAsync(sessionId, CancellationToken.None);
            return;
        }

        var session = await sessions.GetAsync(sessionId, cancellationToken);
        try
        {
            if (session is not null)
            {
                await identity.LogoutAsync(session.RefreshToken, cancellationToken);
            }
        }
        finally
        {
            await sessions.DeleteAsync(sessionId, CancellationToken.None);
        }
    }

    public Task InvalidateLocalSessionAsync(
        string? sessionId,
        CancellationToken cancellationToken)
    {
        return string.IsNullOrWhiteSpace(sessionId)
            ? Task.CompletedTask
            : sessions.DeleteAsync(sessionId, cancellationToken);
    }

    private async Task<(HttpStatusCode StatusCode, BffSessionRecord? Session)> MaintainSessionAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        var current = await sessions.GetAsync(sessionId, cancellationToken);
        if (current is null)
        {
            return (HttpStatusCode.Unauthorized, null);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (NeedsAccessTokenRefresh(current, now))
        {
            var refreshed = await identity.RefreshAsync(current.RefreshToken, cancellationToken);
            if (!refreshed.IsSuccess || refreshed.Value is null)
            {
                if (refreshed.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    await sessions.DeleteAsync(sessionId, CancellationToken.None);
                }

                return (refreshed.StatusCode, null);
            }

            var replacement = CreateSession(
                refreshed.Value,
                current.CreatedAtUtc,
                current.AbsoluteExpiresAtUtc);
            if (!await sessions.TryUpdateAsync(sessionId, replacement, cancellationToken))
            {
                await BestEffortLogoutAsync(replacement.RefreshToken);
                return (HttpStatusCode.Unauthorized, null);
            }

            return (HttpStatusCode.OK, replacement);
        }

        if (NeedsActivityRenewal(current, now))
        {
            var idleExpiry = new[]
            {
                now.AddMinutes(options.Value.IdleTimeoutMinutes),
                current.RefreshTokenExpiresAtUtc,
                current.AbsoluteExpiresAtUtc
            }.Min();
            var replacement = current with
            {
                LastActivityAtUtc = now,
                IdleExpiresAtUtc = idleExpiry
            };
            if (!await sessions.TryUpdateAsync(sessionId, replacement, cancellationToken))
            {
                return (HttpStatusCode.Unauthorized, null);
            }

            return (HttpStatusCode.OK, replacement);
        }

        return (HttpStatusCode.OK, current);
    }

    private bool RequiresMaintenance(BffSessionRecord session)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        return NeedsAccessTokenRefresh(session, now) || NeedsActivityRenewal(session, now);
    }

    private bool NeedsAccessTokenRefresh(BffSessionRecord session, DateTime now)
    {
        return session.AccessTokenExpiresAtUtc <=
            now.AddSeconds(options.Value.AccessTokenRefreshSkewSeconds);
    }

    private bool NeedsActivityRenewal(BffSessionRecord session, DateTime now)
    {
        return session.LastActivityAtUtc <=
            now.AddMinutes(-options.Value.ActivityRenewalMinutes);
    }

    private string? ValidMigrationAccessToken(BffSessionRecord session)
    {
        return session.MigrationAccessExpiresAtUtc > timeProvider.GetUtcNow().UtcDateTime &&
            session.MigrationAccessInstitutionId == session.ActiveInstitution?.Id
                ? session.MigrationAccessToken
                : null;
    }

    private Task<IAsyncDisposable?> AcquireLockAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        return sessionLock.AcquireAsync(
            sessionId,
            TimeSpan.FromMilliseconds(options.Value.RefreshLockWaitMilliseconds),
            TimeSpan.FromSeconds(options.Value.RefreshLockLeaseSeconds),
            cancellationToken);
    }

    private async Task BestEffortLogoutAsync(string refreshToken)
    {
        try
        {
            await identity.LogoutAsync(refreshToken, CancellationToken.None);
        }
        catch
        {
            // The local session remains unusable. Identity token-family replay detection
            // is the final fail-closed boundary when compensating revocation is unavailable.
        }
    }

    private BffSessionRecord CreateSession(
        IdentityLoginResponse response,
        DateTime? originalCreatedAtUtc = null,
        DateTime? existingAbsoluteExpiryUtc = null)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (string.IsNullOrWhiteSpace(response.AccessToken) ||
            string.IsNullOrWhiteSpace(response.RefreshToken) ||
            response.User.Id == Guid.Empty ||
            response.ExpiresAtUtc <= now ||
            response.RefreshTokenExpiresAtUtc <= response.ExpiresAtUtc)
        {
            throw new InvalidOperationException("Identity returned an invalid login response.");
        }

        var configuredAbsoluteExpiry = now.AddHours(options.Value.AbsoluteTimeoutHours);
        var absoluteExpiry = new[]
        {
            configuredAbsoluteExpiry,
            response.RefreshTokenExpiresAtUtc,
            existingAbsoluteExpiryUtc ?? DateTime.MaxValue
        }.Min();
        var idleExpiry = new[]
        {
            now.AddMinutes(options.Value.IdleTimeoutMinutes),
            absoluteExpiry
        }.Min();

        return new BffSessionRecord(
            response.AccessToken,
            response.ExpiresAtUtc,
            response.RefreshToken,
            response.RefreshTokenExpiresAtUtc,
            response.User,
            response.Institutions,
            response.ActiveInstitution,
            originalCreatedAtUtc ?? now,
            now,
            idleExpiry,
            absoluteExpiry);
    }

    private BffSessionResponse ToResponse(BffSessionRecord session)
    {
        return new BffSessionResponse(
            session.User,
            session.Institutions,
            session.ActiveInstitution,
            session.IdleExpiresAtUtc,
            session.AbsoluteExpiresAtUtc,
            session.MigrationAccessExpiresAtUtc > timeProvider.GetUtcNow().UtcDateTime &&
                session.MigrationAccessInstitutionId == session.ActiveInstitution?.Id
                    ? session.MigrationAccessExpiresAtUtc
                    : null);
    }
}
