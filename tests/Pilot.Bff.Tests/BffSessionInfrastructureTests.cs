using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Pilot.Bff.Api.Contracts;
using Pilot.Bff.Api.Options;
using Pilot.Bff.Api.Session;

namespace Pilot.Bff.Tests;

public sealed class BffSessionInfrastructureTests
{
    [Fact]
    public void SessionInfrastructure_RejectsSessionWithoutRedis()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Bff:Session:Enabled"] = "true"
            })
            .Build();
        var services = new ServiceCollection();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddBffSessionInfrastructure(configuration));

        Assert.Contains("require Redis", exception.Message);
    }

    [Fact]
    public void SessionInfrastructure_AcceptsSevenDaySessionPolicy()
    {
        const string password = "test-password";
        using var certificate = CreateCertificate();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Bff:Session:Enabled"] = "true",
                ["Bff:Session:IdleTimeoutMinutes"] = "10080",
                ["Bff:Session:AbsoluteTimeoutHours"] = "168",
                ["Redis:Enabled"] = "true",
                ["Redis:ConnectionString"] = "localhost:6379,abortConnect=false",
                ["Redis:KeyPepper"] = new string('p', 32),
                ["Bff:DataProtection:CertificateBase64"] = Convert.ToBase64String(
                    certificate.Export(X509ContentType.Pkcs12, password)),
                ["Bff:DataProtection:CertificatePassword"] = password
            })
            .Build();
        var services = new ServiceCollection();

        services.AddBffSessionInfrastructure(configuration);
    }

    [Fact]
    public void SessionProtector_RoundTripsWithoutExposingTokens()
    {
        var services = new ServiceCollection();
        services.AddDataProtection();
        using var serviceProvider = services.BuildServiceProvider();
        var protector = new BffSessionProtector(
            serviceProvider.GetRequiredService<IDataProtectionProvider>());
        var session = CreateSession();

        var protectedPayload = protector.Protect(session);
        var restored = protector.Unprotect(protectedPayload);

        Assert.DoesNotContain(session.AccessToken, protectedPayload, StringComparison.Ordinal);
        Assert.DoesNotContain(session.RefreshToken, protectedPayload, StringComparison.Ordinal);
        Assert.NotNull(restored);
        Assert.Equal(session.AccessToken, restored.AccessToken);
        Assert.Equal(session.RefreshToken, restored.RefreshToken);
        Assert.Equal(session.User, restored.User);
        Assert.Equal(session.AbsoluteExpiresAtUtc, restored.AbsoluteExpiresAtUtc);
        Assert.Empty(restored.Institutions);
        Assert.Null(protector.Unprotect(protectedPayload + "tampered"));
    }

    [Fact]
    public void SessionKey_DoesNotContainBrowserSessionId()
    {
        var sessionId = new BffSessionIdGenerator().Create();
        var keyBuilder = new BffSessionKeyBuilder(Options.Create(new BffRedisOptions
        {
            InstanceName = "pilot",
            EnvironmentName = "test",
            KeyPepper = new string('p', 32)
        }));

        var key = keyBuilder.Session(sessionId);
        var lockKey = keyBuilder.RefreshLock(sessionId);
        var userId = Guid.NewGuid();
        var institutionId = Guid.NewGuid();
        var clientIp = System.Net.IPAddress.Parse("203.0.113.10");
        var rateKey = keyBuilder.ProxyRate(userId, institutionId, clientIp, false);
        var concurrencyKey = keyBuilder.ProxyConcurrency(false);

        Assert.Equal(43, sessionId.Length);
        Assert.DoesNotContain(sessionId, key, StringComparison.Ordinal);
        Assert.StartsWith("pilot:test:bff:{pilot-test-bff-sessions}:session:", key, StringComparison.Ordinal);
        Assert.StartsWith("pilot:test:bff:{pilot-test-bff-sessions}:refresh-lock:", lockKey, StringComparison.Ordinal);
        Assert.Equal(key[(key.IndexOf('{'))..(key.IndexOf('}') + 1)], lockKey[(lockKey.IndexOf('{'))..(lockKey.IndexOf('}') + 1)]);
        Assert.DoesNotContain(userId.ToString("D"), rateKey, StringComparison.Ordinal);
        Assert.DoesNotContain(institutionId.ToString("D"), rateKey, StringComparison.Ordinal);
        Assert.DoesNotContain(clientIp.ToString(), rateKey, StringComparison.Ordinal);
        Assert.Equal(
            rateKey[(rateKey.IndexOf('{'))..(rateKey.IndexOf('}') + 1)],
            concurrencyKey[(concurrencyKey.IndexOf('{'))..(concurrencyKey.IndexOf('}') + 1)]);
    }

    private static BffSessionRecord CreateSession()
    {
        var now = DateTime.UtcNow;
        return new BffSessionRecord(
            "access-token-secret",
            now.AddMinutes(5),
            "refresh-token-secret",
            now.AddHours(12),
            new BffAuthUser(Guid.NewGuid(), "Test User", null, "Admin", false),
            [],
            null,
            now,
            now,
            now.AddMinutes(30),
            now.AddHours(12));
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=pilot-bff-tests",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddDays(1));
    }
}
