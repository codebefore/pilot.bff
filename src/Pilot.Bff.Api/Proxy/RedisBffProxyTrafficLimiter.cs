using Microsoft.Extensions.Options;
using Pilot.Bff.Api.Options;
using Pilot.Bff.Api.Session;
using StackExchange.Redis;
using System.Net;

namespace Pilot.Bff.Api.Proxy;

public sealed class RedisBffProxyTrafficLimiter(
    IConnectionMultiplexer redis,
    IBffSessionKeyBuilder keyBuilder,
    IOptions<BffProxyOptions> options) : IBffProxyTrafficLimiter
{
    private const int WindowMilliseconds = 60_000;
    private const string AcquireScript = """
        local clock = redis.call('TIME')
        local now = (clock[1] * 1000) + math.floor(clock[2] / 1000)
        local rate_window = tonumber(ARGV[4])
        local lease_ms = tonumber(ARGV[5])

        redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', now - rate_window)
        redis.call('ZREMRANGEBYSCORE', KEYS[2], '-inf', now)

        if redis.call('ZCARD', KEYS[1]) >= tonumber(ARGV[2]) then
            return 0
        end
        if redis.call('ZCARD', KEYS[2]) >= tonumber(ARGV[3]) then
            return 0
        end

        redis.call('ZADD', KEYS[1], now, ARGV[1])
        redis.call('PEXPIRE', KEYS[1], rate_window)
        redis.call('ZADD', KEYS[2], now + lease_ms, ARGV[1])
        redis.call('PEXPIRE', KEYS[2], lease_ms)
        return 1
        """;
    private const string AcquireConcurrencyScript = """
        local clock = redis.call('TIME')
        local now = (clock[1] * 1000) + math.floor(clock[2] / 1000)
        local lease_ms = tonumber(ARGV[3])

        redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', now)
        if redis.call('ZCARD', KEYS[1]) >= tonumber(ARGV[2]) then
            return 0
        end

        redis.call('ZADD', KEYS[1], now + lease_ms, ARGV[1])
        redis.call('PEXPIRE', KEYS[1], lease_ms)
        return 1
        """;

    public async ValueTask<IAsyncDisposable?> TryAcquireAsync(
        Guid userId,
        Guid? institutionId,
        IPAddress? clientIpAddress,
        BffProxyTrafficKind trafficKind,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var value = options.Value;
        var requestId = Guid.NewGuid().ToString("N");
        if (trafficKind == BffProxyTrafficKind.FileTransfer)
        {
            var fileTransferLeaseMilliseconds = checked(
                (value.ResponseStreamingTimeoutSeconds + 10) * 1000);
            var fileTransferKey = keyBuilder.ProxyFileTransferConcurrency();
            var fileTransferResult = await redis.GetDatabase().ScriptEvaluateAsync(
                AcquireConcurrencyScript,
                [fileTransferKey],
                [requestId, value.FileTransferConcurrentRequests, fileTransferLeaseMilliseconds])
                .WaitAsync(cancellationToken);
            return (long)fileTransferResult == 1
                ? new RedisConcurrencyLease(redis.GetDatabase(), fileTransferKey, requestId)
                : null;
        }

        var mutation = trafficKind == BffProxyTrafficKind.Mutation;
        var leaseMilliseconds = checked((value.TimeoutSeconds + 10) * 1000);
        var concurrencyKey = keyBuilder.ProxyConcurrency(mutation);
        var result = await redis.GetDatabase().ScriptEvaluateAsync(
            AcquireScript,
            [keyBuilder.ProxyRate(userId, institutionId, clientIpAddress, mutation), concurrencyKey],
            [
                requestId,
                mutation ? value.MutationRequestsPerMinute : value.ReadRequestsPerMinute,
                mutation ? value.MutationConcurrentRequests : value.ReadConcurrentRequests,
                WindowMilliseconds,
                leaseMilliseconds
            ]).WaitAsync(cancellationToken);

        return (long)result == 1
            ? new RedisConcurrencyLease(redis.GetDatabase(), concurrencyKey, requestId)
            : null;
    }

    private sealed class RedisConcurrencyLease(
        IDatabase database,
        RedisKey key,
        RedisValue member) : IAsyncDisposable
    {
        private int released;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref released, 1) == 0)
            {
                await database.SortedSetRemoveAsync(key, member);
            }
        }
    }
}
