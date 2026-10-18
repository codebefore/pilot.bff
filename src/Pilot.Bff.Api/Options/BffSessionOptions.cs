namespace Pilot.Bff.Api.Options;

public sealed class BffSessionOptions
{
    public const string SectionName = "Bff:Session";

    public bool Enabled { get; init; }

    public string CookieName { get; init; } = "__Host-pilot_session";

    public int IdleTimeoutMinutes { get; init; } = 30;

    public int AbsoluteTimeoutHours { get; init; } = 12;

    public int AccessTokenRefreshSkewSeconds { get; init; } = 60;

    public int ActivityRenewalMinutes { get; init; } = 5;

    public int RefreshLockLeaseSeconds { get; init; } = 15;

    public int RefreshLockWaitMilliseconds { get; init; } = 2000;
}

public sealed class BffRedisOptions
{
    public const string SectionName = "Redis";

    public bool Enabled { get; init; }

    public string? ConnectionString { get; init; }

    public string InstanceName { get; init; } = "pilot";

    public string EnvironmentName { get; init; } = "dev";

    public string? KeyPepper { get; init; }
}

public sealed class BffDataProtectionOptions
{
    public const string SectionName = "Bff:DataProtection";

    public string? CertificateBase64 { get; init; }

    public string? CertificatePassword { get; init; }

    public string? PreviousCertificateBase64 { get; init; }

    public string? PreviousCertificatePassword { get; init; }
}
