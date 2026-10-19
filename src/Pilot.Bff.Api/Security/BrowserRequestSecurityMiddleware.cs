using Microsoft.Extensions.Options;
using Pilot.Bff.Api.Options;

namespace Pilot.Bff.Api.Security;

public sealed class BrowserRequestSecurityMiddleware(
    RequestDelegate next,
    IOptions<BffBrowserSecurityOptions> options,
    ILogger<BrowserRequestSecurityMiddleware> logger)
{
    private static readonly HashSet<string> SafeMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        HttpMethods.Get,
        HttpMethods.Head,
        HttpMethods.Options
    };

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/bff") ||
            SafeMethods.Contains(context.Request.Method) && !context.WebSockets.IsWebSocketRequest)
        {
            await next(context);
            return;
        }

        var origin = context.Request.Headers.Origin.ToString();
        var allowed = options.Value.AllowedOrigins.Any(candidate =>
            string.Equals(candidate.TrimEnd('/'), origin.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
        var fetchSite = context.Request.Headers["Sec-Fetch-Site"].ToString();
        var fetchSiteAllowed = string.IsNullOrEmpty(fetchSite) ||
            string.Equals(fetchSite, "same-origin", StringComparison.OrdinalIgnoreCase);

        if (!allowed || !fetchSiteAllowed)
        {
            SecurityEventLog.Rejected(logger, "origin_rejected");
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new
            {
                title = "Browser request origin was rejected.",
                status = StatusCodes.Status403Forbidden
            });
            return;
        }

        await next(context);
    }
}
