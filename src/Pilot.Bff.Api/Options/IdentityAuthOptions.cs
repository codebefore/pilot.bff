namespace Pilot.Bff.Api.Options;

public sealed class IdentityAuthOptions
{
    public const string SectionName = "Identity";

    public string BaseUrl { get; init; } = "http://pilot-identity";

    public int TimeoutSeconds { get; init; } = 10;
}
