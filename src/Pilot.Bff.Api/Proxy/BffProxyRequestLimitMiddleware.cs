using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using Pilot.Bff.Api.Options;

namespace Pilot.Bff.Api.Proxy;

public sealed class BffProxyRequestLimitMiddleware(
    RequestDelegate next,
    IOptions<BffProxyOptions> options,
    IBffProxyRequestBodyPolicy bodyPolicy)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!options.Value.Enabled ||
            !context.Request.Path.StartsWithSegments("/bff/v1", StringComparison.Ordinal))
        {
            await next(context);
            return;
        }

        var maxBody = bodyPolicy.GetMaxRequestBodyBytes(context.Request);
        if (context.Request.ContentLength > maxBody)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }

        var bodySizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodySizeFeature is { IsReadOnly: false })
        {
            bodySizeFeature.MaxRequestBodySize = maxBody;
        }

        await next(context);
    }
}
