using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Pilot.Bff.Api.Health;

public static class HealthResponseWriter
{
    public static Task WriteAsync(HttpContext context, HealthReport report)
    {
        return context.Response.WriteAsJsonAsync(new
        {
            service = "pilot-bff",
            status = report.Status.ToString().ToLowerInvariant(),
            checkedAtUtc = DateTime.UtcNow,
            checks = report.Entries.Select(entry => new
            {
                name = entry.Key,
                status = entry.Value.Status.ToString().ToLowerInvariant(),
                description = entry.Value.Description
            })
        });
    }
}
