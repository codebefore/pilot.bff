using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Pilot.Bff.Api.Health;
using Pilot.Bff.Api.Options;
using StackExchange.Redis;

namespace Pilot.Bff.Api.Session;

public static class BffSessionInfrastructure
{
    public static IServiceCollection AddBffSessionInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<BffSessionOptions>(configuration.GetSection(BffSessionOptions.SectionName));
        services.Configure<BffRedisOptions>(configuration.GetSection(BffRedisOptions.SectionName));
        services.Configure<BffDataProtectionOptions>(
            configuration.GetSection(BffDataProtectionOptions.SectionName));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IBffSessionIdGenerator, BffSessionIdGenerator>();
        services.AddSingleton<IBffSessionKeyBuilder, BffSessionKeyBuilder>();
        services.AddSingleton<IBffSessionProtector, BffSessionProtector>();
        services.AddHealthChecks().AddCheck<RedisReadinessHealthCheck>("redis", tags: ["ready"]);

        var sessionOptions = configuration
            .GetSection(BffSessionOptions.SectionName)
            .Get<BffSessionOptions>() ?? new BffSessionOptions();
        var redisOptions = configuration
            .GetSection(BffRedisOptions.SectionName)
            .Get<BffRedisOptions>() ?? new BffRedisOptions();
        var dataProtectionOptions = configuration
            .GetSection(BffDataProtectionOptions.SectionName)
            .Get<BffDataProtectionOptions>() ?? new BffDataProtectionOptions();

        Validate(sessionOptions, redisOptions, dataProtectionOptions);
        ValidateLockLease(
            sessionOptions,
            configuration.GetValue("Identity:TimeoutSeconds", 10));

        var dataProtection = services.AddDataProtection()
            .SetApplicationName("pilot-bff-v1");

        if (!sessionOptions.Enabled)
        {
            services.AddSingleton<IBffSessionStore, DisabledBffSessionStore>();
            services.AddSingleton<IBffSessionLock, DisabledBffSessionLock>();
            return services;
        }

        var redisConfiguration = ConfigurationOptions.Parse(redisOptions.ConnectionString!);
        redisConfiguration.AbortOnConnectFail = false;
        var lazyConnection = new Lazy<IConnectionMultiplexer>(
            () => ConnectionMultiplexer.Connect(redisConfiguration),
            LazyThreadSafetyMode.ExecutionAndPublication);

        services.AddSingleton(_ => lazyConnection.Value);
        services.AddSingleton<IBffSessionStore, RedisBffSessionStore>();
        services.AddSingleton<IBffSessionLock, RedisBffSessionLock>();

        var keyBuilder = new BffSessionKeyBuilder(Microsoft.Extensions.Options.Options.Create(redisOptions));
        var currentCertificate = LoadCertificate(
            dataProtectionOptions.CertificateBase64!,
            dataProtectionOptions.CertificatePassword);
        dataProtection
            .PersistKeysToStackExchangeRedis(
                () => lazyConnection.Value.GetDatabase(),
                keyBuilder.DataProtectionKeys)
            .ProtectKeysWithCertificate(currentCertificate);

        if (!string.IsNullOrWhiteSpace(dataProtectionOptions.PreviousCertificateBase64))
        {
            dataProtection.UnprotectKeysWithAnyCertificate(LoadCertificate(
                dataProtectionOptions.PreviousCertificateBase64,
                dataProtectionOptions.PreviousCertificatePassword));
        }

        return services;
    }

    private static void Validate(
        BffSessionOptions session,
        BffRedisOptions redis,
        BffDataProtectionOptions dataProtection)
    {
        if (!session.Enabled)
        {
            return;
        }

        if (!string.Equals(session.CookieName, "__Host-pilot_session", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Bff:Session:CookieName must be __Host-pilot_session.");
        }

        if (session.IdleTimeoutMinutes is < 5 or > 10080)
        {
            throw new InvalidOperationException(
                "Bff:Session:IdleTimeoutMinutes must be between 5 and 10080.");
        }

        if (session.AbsoluteTimeoutHours is < 1 or > 168)
        {
            throw new InvalidOperationException(
                "Bff:Session:AbsoluteTimeoutHours must be between 1 and 168.");
        }

        if (TimeSpan.FromMinutes(session.IdleTimeoutMinutes) >
            TimeSpan.FromHours(session.AbsoluteTimeoutHours))
        {
            throw new InvalidOperationException(
                "Bff:Session idle timeout cannot exceed the absolute timeout.");
        }

        if (session.AccessTokenRefreshSkewSeconds is < 15 or > 300)
        {
            throw new InvalidOperationException(
                "Bff:Session:AccessTokenRefreshSkewSeconds must be between 15 and 300.");
        }

        if (session.ActivityRenewalMinutes is < 1 ||
            session.ActivityRenewalMinutes >= session.IdleTimeoutMinutes)
        {
            throw new InvalidOperationException(
                "Bff:Session:ActivityRenewalMinutes must be positive and less than the idle timeout.");
        }

        if (session.RefreshLockLeaseSeconds is < 5 or > 60 ||
            session.RefreshLockWaitMilliseconds is < 100 or > 10000)
        {
            throw new InvalidOperationException(
                "Bff session refresh-lock timing is outside the allowed range.");
        }

        if (!redis.Enabled || string.IsNullOrWhiteSpace(redis.ConnectionString))
        {
            throw new InvalidOperationException(
                "Bff sessions require Redis:Enabled and Redis:ConnectionString.");
        }

        if (string.IsNullOrWhiteSpace(redis.KeyPepper) || redis.KeyPepper.Length < 32)
        {
            throw new InvalidOperationException(
                "Bff sessions require Redis:KeyPepper with at least 32 characters.");
        }

        if (string.IsNullOrWhiteSpace(dataProtection.CertificateBase64))
        {
            throw new InvalidOperationException(
                "Bff sessions require Bff:DataProtection:CertificateBase64.");
        }


        if (string.IsNullOrWhiteSpace(dataProtection.PreviousCertificateBase64) !=
            string.IsNullOrWhiteSpace(dataProtection.PreviousCertificatePassword))
        {
            throw new InvalidOperationException(
                "The previous BFF Data Protection certificate and password must be configured together.");
        }
    }

    private static X509Certificate2 LoadCertificate(string certificateBase64, string? password)
    {
        try
        {
            var certificateBytes = Convert.FromBase64String(certificateBase64);
            X509Certificate2 certificate;
            try
            {
                certificate = X509CertificateLoader.LoadPkcs12(
                    certificateBytes,
                    password,
                    X509KeyStorageFlags.EphemeralKeySet);
            }
            catch (PlatformNotSupportedException) when (OperatingSystem.IsMacOS())
            {
                certificate = X509CertificateLoader.LoadPkcs12(
                    certificateBytes,
                    password,
                    X509KeyStorageFlags.DefaultKeySet);
            }

            if (!certificate.HasPrivateKey)
            {
                certificate.Dispose();
                throw new InvalidOperationException(
                    "The BFF Data Protection certificate must include a private key.");
            }

            return certificate;
        }
        catch (Exception exception) when (
            exception is FormatException or CryptographicException)
        {
            throw new InvalidOperationException(
                "The BFF Data Protection certificate could not be loaded.",
                exception);
        }
    }

    private static void ValidateLockLease(BffSessionOptions session, int identityTimeoutSeconds)
    {
        if (session.Enabled &&
            session.RefreshLockLeaseSeconds < identityTimeoutSeconds + 5)
        {
            throw new InvalidOperationException(
                "Bff:Session:RefreshLockLeaseSeconds must exceed Identity:TimeoutSeconds by at least 5 seconds.");
        }
    }
}
