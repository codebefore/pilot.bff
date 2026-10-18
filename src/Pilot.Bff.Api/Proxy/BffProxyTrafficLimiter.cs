using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Pilot.Bff.Api.Options;
using System.Net;
using System.Security.Cryptography;

namespace Pilot.Bff.Api.Proxy;

public interface IBffProxyTrafficLimiter
{
    ValueTask<IAsyncDisposable?> TryAcquireAsync(
        Guid userId,
        Guid? institutionId,
        IPAddress? clientIpAddress,
        bool mutation,
        CancellationToken cancellationToken);
}

public sealed class BffProxyTrafficLimiter : IBffProxyTrafficLimiter, IDisposable
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private readonly BffProxyOptions options;
    private readonly MemoryCache cache;
    private readonly TimeProvider timeProvider;
    private readonly SemaphoreSlim readConcurrency;
    private readonly SemaphoreSlim mutationConcurrency;
    private readonly object rateLock = new();
    private readonly RateWindow readOverflowWindow = new();
    private readonly RateWindow mutationOverflowWindow = new();

    public BffProxyTrafficLimiter(
        IOptions<BffProxyOptions> options,
        TimeProvider timeProvider)
    {
        this.options = options.Value;
        cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 10_000 });
        this.timeProvider = timeProvider;
        readConcurrency = new SemaphoreSlim(this.options.ReadConcurrentRequests);
        mutationConcurrency = new SemaphoreSlim(this.options.MutationConcurrentRequests);
    }

    public ValueTask<IAsyncDisposable?> TryAcquireAsync(
        Guid userId,
        Guid? institutionId,
        IPAddress? clientIpAddress,
        bool mutation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ConsumeRatePermit(userId, institutionId, clientIpAddress, mutation))
        {
            return ValueTask.FromResult<IAsyncDisposable?>(null);
        }

        var concurrency = mutation ? mutationConcurrency : readConcurrency;
        return ValueTask.FromResult<IAsyncDisposable?>(concurrency.Wait(0)
            ? new ConcurrencyLease(concurrency)
            : null);
    }

    public void Dispose()
    {
        readConcurrency.Dispose();
        mutationConcurrency.Dispose();
        cache.Dispose();
    }

    private bool ConsumeRatePermit(
        Guid userId,
        Guid? institutionId,
        IPAddress? clientIpAddress,
        bool mutation)
    {
        var clientPartition = clientIpAddress is null
            ? "unknown"
            : Convert.ToHexString(SHA256.HashData(clientIpAddress.GetAddressBytes()));
        var key = $"bff-proxy-rate:{userId:D}:{institutionId?.ToString("D") ?? "none"}:{clientPartition}:{(mutation ? "mutation" : "read")}";
        var limit = mutation ? options.MutationRequestsPerMinute : options.ReadRequestsPerMinute;
        var now = timeProvider.GetUtcNow();

        lock (rateLock)
        {
            if (!cache.TryGetValue(key, out RateWindow? rateWindow) || rateWindow is null)
            {
                rateWindow = new RateWindow();
                cache.Set(
                    key,
                    rateWindow,
                    new MemoryCacheEntryOptions
                    {
                        Size = 1,
                        SlidingExpiration = TimeSpan.FromMinutes(5)
                    });
                if (!cache.TryGetValue(key, out RateWindow? storedWindow) || storedWindow is null)
                {
                    rateWindow = mutation ? mutationOverflowWindow : readOverflowWindow;
                }
            }

            while (rateWindow.Requests.TryPeek(out var oldest) && now - oldest >= Window)
            {
                rateWindow.Requests.Dequeue();
            }

            if (rateWindow.Requests.Count >= limit)
            {
                return false;
            }

            rateWindow.Requests.Enqueue(now);
            return true;
        }
    }

    private sealed class RateWindow
    {
        public Queue<DateTimeOffset> Requests { get; } = new();
    }

    private sealed class ConcurrencyLease(SemaphoreSlim semaphore) : IAsyncDisposable
    {
        private int released;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref released, 1) == 0)
            {
                semaphore.Release();
            }

            return ValueTask.CompletedTask;
        }
    }
}
