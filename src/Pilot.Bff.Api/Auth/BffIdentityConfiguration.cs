using Microsoft.Extensions.Options;
using Pilot.Bff.Api.Options;

namespace Pilot.Bff.Api.Auth;

public static class BffIdentityConfiguration
{
    public static IServiceCollection AddBffIdentityAuth(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<IdentityAuthOptions>(
            configuration.GetSection(IdentityAuthOptions.SectionName));

        var identity = configuration
            .GetSection(IdentityAuthOptions.SectionName)
            .Get<IdentityAuthOptions>() ?? new IdentityAuthOptions();
        var sessionEnabled = configuration.GetValue("Bff:Session:Enabled", false);
        Validate(identity, sessionEnabled);

        services.AddHttpClient<IIdentityAuthClient, IdentityAuthClient>((provider, client) =>
        {
            var options = provider.GetRequiredService<IOptions<IdentityAuthOptions>>().Value;
            client.BaseAddress = new Uri(AppendTrailingSlash(options.BaseUrl), UriKind.Absolute);
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

    private static void Validate(IdentityAuthOptions options, bool sessionEnabled)
    {
        if (!Uri.TryCreate(AppendTrailingSlash(options.BaseUrl), UriKind.Absolute, out var uri) ||
            !(string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            uri.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidOperationException("Identity:BaseUrl must be an absolute HTTP(S) origin.");
        }

        if (sessionEnabled && options.TimeoutSeconds is < 1 or > 30)
        {
            throw new InvalidOperationException("Identity:TimeoutSeconds must be between 1 and 30.");
        }

    }

    private static string AppendTrailingSlash(string value)
    {
        return value.EndsWith("/", StringComparison.Ordinal) ? value : $"{value}/";
    }
}
