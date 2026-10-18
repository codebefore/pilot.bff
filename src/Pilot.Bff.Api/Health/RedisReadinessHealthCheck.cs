using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Pilot.Bff.Api.Options;
using StackExchange.Redis;

namespace Pilot.Bff.Api.Health;

public sealed class RedisReadinessHealthCheck(
    IOptions<BffSessionOptions> sessionOptions,
    IServiceProvider services) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (!sessionOptions.Value.Enabled)
        {
            return HealthCheckResult.Healthy("BFF sessions are disabled.");
        }

        try
        {
            var redis = services.GetRequiredService<IConnectionMultiplexer>();
            await redis.GetDatabase().PingAsync();
            return HealthCheckResult.Healthy("Redis is available.");
        }
        catch (Exception exception) when (
            exception is RedisException or TimeoutException or InvalidOperationException)
        {
            return HealthCheckResult.Unhealthy("Redis is unavailable.", exception);
        }
    }
}
