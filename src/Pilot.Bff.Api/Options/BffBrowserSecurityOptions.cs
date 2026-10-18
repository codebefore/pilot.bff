namespace Pilot.Bff.Api.Options;

public sealed class BffBrowserSecurityOptions
{
    public const string SectionName = "Bff:BrowserSecurity";

    public string[] AllowedOrigins { get; init; } = [];
}
