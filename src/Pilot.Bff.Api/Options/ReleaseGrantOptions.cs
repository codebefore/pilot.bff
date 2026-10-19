namespace Pilot.Bff.Api.Options;

public sealed class ReleaseGrantOptions
{
    public const string SectionName = "ReleaseGrant";

    public bool Enabled { get; set; }
    public string Issuer { get; set; } = "pilot-bff";
    public string Audience { get; set; } = "pilot-release-control";
    public string SigningKey { get; set; } = "";
    public int LifetimeSeconds { get; set; } = 60;
}
