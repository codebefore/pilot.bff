using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.Options;
using Pilot.Bff.Api.Options;

namespace Pilot.Bff.Api.Security;

public sealed class SessionEnabledEndpointFilter(IOptions<BffSessionOptions> options) : IEndpointFilter
{
    public ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        return options.Value.Enabled
            ? next(context)
            : ValueTask.FromResult<object?>(Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "BFF sessions are disabled."));
    }
}

public sealed class AntiforgeryEndpointFilter(IAntiforgery antiforgery) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        if (HttpMethods.IsGet(context.HttpContext.Request.Method) ||
            HttpMethods.IsHead(context.HttpContext.Request.Method) ||
            HttpMethods.IsOptions(context.HttpContext.Request.Method) ||
            HttpMethods.IsTrace(context.HttpContext.Request.Method))
        {
            return await next(context);
        }

        try
        {
            await antiforgery.ValidateRequestAsync(context.HttpContext);
            return await next(context);
        }
        catch (AntiforgeryValidationException)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Antiforgery validation failed.");
        }
    }
}
