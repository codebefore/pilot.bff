using System.Security.Cryptography;
using StackExchange.Redis;

namespace Pilot.Bff.Api.Session;

public interface IBffSessionLock
{
    Task<IAsyncDisposable?> AcquireAsync(
        string sessionId,
        TimeSpan waitTimeout,
        TimeSpan leaseTime,
        CancellationToken cancellationToken);
}

public sealed class RedisBffSessionLock(
    IConnectionMultiplexer redis,
    IBffSessionKeyBuilder keys,
    TimeProvider timeProvider) : IBffSessionLock
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(50);

    public async Task<IAsyncDisposable?> AcquireAsync(
        string sessionId,
        TimeSpan waitTimeout,
        TimeSpan leaseTime,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(waitTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(leaseTime, TimeSpan.Zero);

        var database = redis.GetDatabase();
        var key = keys.RefreshLock(sessionId);
        var owner = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var deadline = timeProvider.GetUtcNow() + waitTimeout;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await database.LockTakeAsync(key, owner, leaseTime))
            {
                return new RedisLockLease(database, key, owner);
            }

            var remaining = deadline - timeProvider.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                return null;
            }

            await Task.Delay(remaining < RetryDelay ? remaining : RetryDelay, cancellationToken);
        }
    }

    private sealed class RedisLockLease(IDatabase database, RedisKey key, RedisValue owner)
        : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await database.LockReleaseAsync(key, owner);
        }
    }
}

public sealed class DisabledBffSessionLock : IBffSessionLock
{
    public Task<IAsyncDisposable?> AcquireAsync(
        string sessionId,
        TimeSpan waitTimeout,
        TimeSpan leaseTime,
        CancellationToken cancellationToken)
    {
        return Task.FromException<IAsyncDisposable?>(
            new InvalidOperationException("BFF session locking is disabled."));
    }
}
