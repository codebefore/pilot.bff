using StackExchange.Redis;

namespace Pilot.Bff.Api.Session;

public interface IBffSessionStore
{
    Task SetAsync(string sessionId, BffSessionRecord session, CancellationToken cancellationToken);

    Task<BffSessionRecord?> GetAsync(string sessionId, CancellationToken cancellationToken);

    Task<bool> TryUpdateAsync(
        string sessionId,
        BffSessionRecord session,
        CancellationToken cancellationToken);

    Task<bool> RotateAsync(
        string currentSessionId,
        string replacementSessionId,
        BffSessionRecord session,
        CancellationToken cancellationToken);

    Task DeleteAsync(string sessionId, CancellationToken cancellationToken);
}

public sealed class RedisBffSessionStore(
    IConnectionMultiplexer redis,
    IBffSessionKeyBuilder keys,
    IBffSessionProtector protector,
    TimeProvider timeProvider) : IBffSessionStore
{
    public async Task SetAsync(
        string sessionId,
        BffSessionRecord session,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Validate(session);

        var lifetime = CalculateLifetime(session);

        await redis.GetDatabase().StringSetAsync(
            keys.Session(sessionId),
            protector.Protect(session),
            lifetime);
    }

    public async Task<BffSessionRecord?> GetAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = keys.Session(sessionId);
        var value = await redis.GetDatabase().StringGetAsync(key);

        if (value.IsNullOrEmpty)
        {
            return null;
        }

        var session = protector.Unprotect(value.ToString());
        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (session is null ||
            session.RefreshTokenExpiresAtUtc <= now ||
            session.IdleExpiresAtUtc <= now ||
            session.AbsoluteExpiresAtUtc <= now)
        {
            await redis.GetDatabase().KeyDeleteAsync(key);
            return null;
        }

        return session;
    }

    public async Task DeleteAsync(string sessionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await redis.GetDatabase().KeyDeleteAsync(keys.Session(sessionId));
    }

    public async Task<bool> TryUpdateAsync(
        string sessionId,
        BffSessionRecord session,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Validate(session);
        var lifetime = CalculateLifetime(session);
        var result = await redis.GetDatabase().ScriptEvaluateAsync(
            """
            if redis.call('exists', KEYS[1]) == 0 then
                return 0
            end
            redis.call('psetex', KEYS[1], ARGV[1], ARGV[2])
            return 1
            """,
            [keys.Session(sessionId)],
            [(long)lifetime.TotalMilliseconds, protector.Protect(session)]);
        return (long)result == 1;
    }

    public async Task<bool> RotateAsync(
        string currentSessionId,
        string replacementSessionId,
        BffSessionRecord session,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Validate(session);
        var lifetime = CalculateLifetime(session);
        var result = await redis.GetDatabase().ScriptEvaluateAsync(
            """
            if redis.call('exists', KEYS[1]) == 0 then
                return 0
            end
            redis.call('psetex', KEYS[2], ARGV[1], ARGV[2])
            redis.call('del', KEYS[1])
            return 1
            """,
            [keys.Session(currentSessionId), keys.Session(replacementSessionId)],
            [(long)lifetime.TotalMilliseconds, protector.Protect(session)]);
        return (long)result == 1;
    }

    private static void Validate(BffSessionRecord session)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(session.AccessToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(session.RefreshToken);

        if (session.User.Id == Guid.Empty)
        {
            throw new InvalidOperationException("BFF session requires a user id.");
        }

        if (session.CreatedAtUtc > session.LastActivityAtUtc ||
            session.LastActivityAtUtc >= session.IdleExpiresAtUtc ||
            session.CreatedAtUtc >= session.AbsoluteExpiresAtUtc ||
            session.AccessTokenExpiresAtUtc > session.RefreshTokenExpiresAtUtc)
        {
            throw new InvalidOperationException("BFF session expiry values are inconsistent.");
        }
    }

    private TimeSpan CalculateLifetime(BffSessionRecord session)
    {
        var expiresAtUtc = new[]
        {
            session.RefreshTokenExpiresAtUtc,
            session.IdleExpiresAtUtc,
            session.AbsoluteExpiresAtUtc
        }.Min();
        var lifetime = expiresAtUtc - timeProvider.GetUtcNow().UtcDateTime;

        return lifetime > TimeSpan.Zero
            ? lifetime
            : throw new InvalidOperationException("BFF session expiry must be in the future.");
    }
}

public sealed class DisabledBffSessionStore : IBffSessionStore
{
    private const string Error = "BFF session storage is disabled.";

    public Task SetAsync(string sessionId, BffSessionRecord session, CancellationToken cancellationToken)
    {
        return Task.FromException(new InvalidOperationException(Error));
    }

    public Task<BffSessionRecord?> GetAsync(string sessionId, CancellationToken cancellationToken)
    {
        return Task.FromException<BffSessionRecord?>(new InvalidOperationException(Error));
    }

    public Task DeleteAsync(string sessionId, CancellationToken cancellationToken)
    {
        return Task.FromException(new InvalidOperationException(Error));
    }

    public Task<bool> TryUpdateAsync(
        string sessionId,
        BffSessionRecord session,
        CancellationToken cancellationToken)
    {
        return Task.FromException<bool>(new InvalidOperationException(Error));
    }

    public Task<bool> RotateAsync(
        string currentSessionId,
        string replacementSessionId,
        BffSessionRecord session,
        CancellationToken cancellationToken)
    {
        return Task.FromException<bool>(new InvalidOperationException(Error));
    }
}
