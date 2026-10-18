using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Pilot.Bff.Api.Health;

public sealed class BffRuntimeReadinessHealthCheck(IConfiguration configuration) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var proxyEnabled = configuration.GetValue("Bff:Proxy:Enabled", false);
        var sessionEnabled = configuration.GetValue("Bff:Session:Enabled", false);

        if (proxyEnabled && !sessionEnabled)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                "Proxy requires the BFF session runtime."));
        }

        return Task.FromResult(HealthCheckResult.Healthy("Configured BFF runtime is ready."));
    }
}
