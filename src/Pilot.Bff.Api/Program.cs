using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Pilot.Bff.Api.Auth;
using Pilot.Bff.Api.Endpoints;
using Pilot.Bff.Api.Health;
using Pilot.Bff.Api.Options;
using Pilot.Bff.Api.Proxy;
using Pilot.Bff.Api.Security;
using Pilot.Bff.Api.Session;
using Pilot.Observability;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.AddSingleton<DependencyTelemetry>();
builder.Services
    .AddHealthChecks()
    .AddCheck<BffRuntimeReadinessHealthCheck>("bff_runtime", tags: ["ready"]);

var forwardedHeaders = builder.Services.AddTrustedForwardedHeaders(builder.Configuration);
builder.Services.AddBffSessionInfrastructure(builder.Configuration);
builder.Services.AddBffIdentityAuth(builder.Configuration);
builder.Services.AddBffProxy(builder.Configuration);
builder.Services.Configure<BffBrowserSecurityOptions>(
    builder.Configuration.GetSection(BffBrowserSecurityOptions.SectionName));
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.Name = "__Host-pilot_xsrf";
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.Path = "/";
});
builder.Services.AddTransient<SessionEnabledEndpointFilter>();
builder.Services.AddTransient<AntiforgeryEndpointFilter>();
builder.Services.AddScoped<BffAuthService>();
builder.Services.AddSingleton<BffSessionCookie>();

var app = builder.Build();
_ = app.Services.GetRequiredService<DependencyTelemetry>();

if (forwardedHeaders.Enabled)
{
    app.UseForwardedHeaders();
    if (forwardedHeaders.ExternalScheme is not null)
    {
        app.Use((context, next) =>
        {
            context.Request.Scheme = forwardedHeaders.ExternalScheme;
            return next(context);
        });
    }
}

app.UseMiddleware<RequestPerformanceMiddleware>();
app.UseExceptionHandler();
app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers.Pragma = "no-cache";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.XFrameOptions = "DENY";
    context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'; base-uri 'none'";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    await next(context);
});
app.UseMiddleware<BffProxyRequestLimitMiddleware>();
app.UseWebSockets();
app.UseMiddleware<BrowserRequestSecurityMiddleware>();

app.MapHealthChecks("/health", new HealthCheckOptions
{
    AllowCachingResponses = false,
    Predicate = _ => false,
    ResponseWriter = HealthResponseWriter.WriteAsync
});

app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    AllowCachingResponses = false,
    Predicate = registration => registration.Tags.Contains("ready"),
    ResponseWriter = HealthResponseWriter.WriteAsync
});

app.MapGet("/bff/service-info", (IConfiguration configuration) => Results.Ok(new
{
    service = "pilot-bff",
    version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0",
    sessionEnabled = configuration.GetValue("Bff:Session:Enabled", false),
    proxyEnabled = configuration.GetValue("Bff:Proxy:Enabled", false)
}));

app.MapBffAuthEndpoints();
if (builder.Configuration.GetValue("Bff:Proxy:Enabled", false))
{
    app.MapBffProxyEndpoints();
}

app.Run();

public partial class Program;
