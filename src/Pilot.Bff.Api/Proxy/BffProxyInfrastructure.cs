using Microsoft.Extensions.Options;
using Pilot.Bff.Api.Options;

namespace Pilot.Bff.Api.Proxy;

public static class BffProxyInfrastructure
{
    public const string HttpClientName = "bff-gateway";

    public static IServiceCollection AddBffProxy(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<BffProxyOptions>(configuration.GetSection(BffProxyOptions.SectionName));
        services.AddSingleton<IBffProxyRoutePolicy, BffProxyRoutePolicy>();
        services.AddSingleton<IBffProxyRequestFactory, BffProxyRequestFactory>();
        services.AddSingleton<IBffProxyRequestBodyPolicy, BffProxyRequestBodyPolicy>();
        services.AddSingleton<IBffProxyQueryPolicy, BffProxyQueryPolicy>();
        services.AddSingleton<IBffWebSocketProxy, BffWebSocketProxy>();
        var sessionEnabled = configuration.GetValue("Bff:Session:Enabled", false);
        var redisEnabled = configuration.GetValue("Redis:Enabled", false);
        if (sessionEnabled && redisEnabled)
        {
            services.AddSingleton<IBffProxyTrafficLimiter, RedisBffProxyTrafficLimiter>();
        }
        else
        {
            services.AddSingleton<IBffProxyTrafficLimiter, BffProxyTrafficLimiter>();
        }

        var proxy = configuration.GetSection(BffProxyOptions.SectionName)
            .Get<BffProxyOptions>() ?? new BffProxyOptions();
        Validate(proxy);

        services.AddHttpClient(HttpClientName, (provider, client) =>
        {
            var options = provider.GetRequiredService<IOptions<BffProxyOptions>>().Value;
            client.BaseAddress = new Uri(AppendTrailingSlash(options.GatewayBaseUrl), UriKind.Absolute);
            client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        })
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false
        })
        .RemoveAllLoggers();

        return services;
    }

    private static void Validate(BffProxyOptions options)
    {
        if (!Uri.TryCreate(AppendTrailingSlash(options.GatewayBaseUrl), UriKind.Absolute, out var uri) ||
            !(uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            uri.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidOperationException("Bff:Proxy:GatewayBaseUrl must be an absolute HTTP(S) origin.");
        }

        if (options.TimeoutSeconds is < 1 or > 60 ||
            options.ResponseStreamingTimeoutSeconds is < 30 or > 3_600 ||
            options.MaxRequestBodyBytes is < 1024 or > 100 * 1024 * 1024 ||
            options.DocumentUploadMaxRequestBodyBytes is < 1024 or > 30 * 1024 * 1024 ||
            options.InstructorDocumentUploadMaxRequestBodyBytes is < 1024 or > 30 * 1024 * 1024 ||
            options.ReadRequestsPerMinute is < 1 or > 10_000 ||
            options.MutationRequestsPerMinute is < 1 or > 10_000 ||
            options.ReadConcurrentRequests is < 1 or > 1_000 ||
            options.MutationConcurrentRequests is < 1 or > 1_000 ||
            options.FileTransferConcurrentRequests is < 1 or > 1_000)
        {
            throw new InvalidOperationException("BFF proxy limits are outside the allowed range.");
        }
    }

    private static string AppendTrailingSlash(string value) =>
        value.EndsWith("/", StringComparison.Ordinal) ? value : $"{value}/";
}
