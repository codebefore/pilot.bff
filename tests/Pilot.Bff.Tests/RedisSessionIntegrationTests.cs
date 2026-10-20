using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pilot.Bff.Api.Contracts;
using Pilot.Bff.Api.Proxy;
using Pilot.Bff.Api.Session;
using StackExchange.Redis;

namespace Pilot.Bff.Tests;

public sealed class RedisSessionIntegrationTests
{
    [Fact]
    public async Task TwoReplicas_ShareProxyRateAndConcurrencyLimits()
    {
        var redisConnection = Environment.GetEnvironmentVariable("BFF_TEST_REDIS_CONNECTION");
        if (string.IsNullOrWhiteSpace(redisConnection))
        {
            return;
        }

        var instance = $"pilot-bff-limit-it-{Guid.NewGuid():N}";
        const string certificatePassword = "integration-password";
        var certificate = CreateCertificate("pilot-bff-limit", certificatePassword);

        try
        {
            await using var replicaA = CreateReplica(
                redisConnection,
                instance,
                certificate,
                certificatePassword,
                readRate: 1,
                readConcurrency: 1,
                fileTransferConcurrency: 1);
            await using var replicaB = CreateReplica(
                redisConnection,
                instance,
                certificate,
                certificatePassword,
                readRate: 1,
                readConcurrency: 1,
                fileTransferConcurrency: 1);
            var limiterA = replicaA.GetRequiredService<IBffProxyTrafficLimiter>();
            var limiterB = replicaB.GetRequiredService<IBffProxyTrafficLimiter>();
            var firstUser = Guid.NewGuid();
            var secondUser = Guid.NewGuid();

            var first = await limiterA.TryAcquireAsync(
                firstUser, null, null, BffProxyTrafficKind.Read, default);
            Assert.NotNull(first);
            Assert.Null(await limiterB.TryAcquireAsync(
                secondUser, null, null, BffProxyTrafficKind.Read, default));

            await first.DisposeAsync();
            var afterRelease = await limiterB.TryAcquireAsync(
                secondUser, null, null, BffProxyTrafficKind.Read, default);
            Assert.NotNull(afterRelease);
            await afterRelease.DisposeAsync();

            Assert.Null(await limiterA.TryAcquireAsync(
                secondUser, null, null, BffProxyTrafficKind.Read, default));

            var firstFile = await limiterA.TryAcquireAsync(
                firstUser, null, null, BffProxyTrafficKind.FileTransfer, default);
            Assert.NotNull(firstFile);
            Assert.Null(await limiterB.TryAcquireAsync(
                secondUser, null, null, BffProxyTrafficKind.FileTransfer, default));

            await firstFile.DisposeAsync();
            await using var fileAfterRelease = await limiterB.TryAcquireAsync(
                secondUser, null, null, BffProxyTrafficKind.FileTransfer, default);
            Assert.NotNull(fileAfterRelease);
        }
        finally
        {
            await DeleteIntegrationKeysAsync(redisConnection, instance);
        }
    }

    [Fact]
    public async Task RedisOutage_ThrowsInsteadOfReturningFallbackSession()
    {
        var redisConnection = Environment.GetEnvironmentVariable("BFF_TEST_REDIS_OUTAGE_CONNECTION");
        if (string.IsNullOrWhiteSpace(redisConnection))
        {
            return;
        }

        const string certificatePassword = "integration-password";
        var certificate = CreateCertificate("pilot-bff-outage", certificatePassword);

        var exception = await Record.ExceptionAsync(async () =>
        {
            await using var replica = CreateReplica(
                redisConnection,
                $"pilot-bff-outage-{Guid.NewGuid():N}",
                certificate,
                certificatePassword);
            await replica.GetRequiredService<IBffSessionStore>()
                .GetAsync("unavailable-session", default);
        });

        Assert.NotNull(exception);
        Assert.True(
            exception is RedisException || exception.InnerException is RedisException,
            exception.ToString());
    }

    [Fact]
    public async Task TwoReplicas_ShareKeyRingLockAndAtomicRotation_AcrossCertificateRollover()
    {
        var redisConnection = Environment.GetEnvironmentVariable("BFF_TEST_REDIS_CONNECTION");
        if (string.IsNullOrWhiteSpace(redisConnection))
        {
            return;
        }

        var instance = $"pilot-bff-it-{Guid.NewGuid():N}";
        const string certificatePassword = "integration-password";
        var certificateA = CreateCertificate("pilot-bff-a", certificatePassword);
        var certificateB = CreateCertificate("pilot-bff-b", certificatePassword);

        try
        {
            await using var replicaA = CreateReplica(
                redisConnection,
                instance,
                certificateA,
                certificatePassword);
            var storeA = replicaA.GetRequiredService<IBffSessionStore>();
            var lockA = replicaA.GetRequiredService<IBffSessionLock>();
            var original = CreateSession("original");
            await storeA.SetAsync("browser-session", original, default);

            await using var replicaB = CreateReplica(
                redisConnection,
                instance,
                certificateB,
                certificatePassword,
                certificateA,
                certificatePassword);
            var storeB = replicaB.GetRequiredService<IBffSessionStore>();
            var lockB = replicaB.GetRequiredService<IBffSessionLock>();

            var readByReplicaB = await storeB.GetAsync("browser-session", default);
            Assert.Equal(original.RefreshToken, readByReplicaB?.RefreshToken);

            await using (var firstLease = await lockA.AcquireAsync(
                "browser-session",
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(15),
                default))
            {
                Assert.NotNull(firstLease);
                var blockedLease = await lockB.AcquireAsync(
                    "browser-session",
                    TimeSpan.FromMilliseconds(150),
                    TimeSpan.FromSeconds(15),
                    default);
                Assert.Null(blockedLease);
            }

            await using var nextLease = await lockB.AcquireAsync(
                "browser-session",
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(15),
                default);
            Assert.NotNull(nextLease);

            var now = DateTimeOffset.UtcNow;
            replicaB.GetRequiredService<IKeyManager>().CreateNewKey(
                now.AddMinutes(-1),
                now.AddDays(90));
            var replacement = CreateSession("replacement");
            Assert.True(await storeB.RotateAsync(
                "browser-session",
                "replacement-session",
                replacement,
                default));
            Assert.Null(await storeA.GetAsync("browser-session", default));

            await using var replicaC = CreateReplica(
                redisConnection,
                instance,
                certificateB,
                certificatePassword,
                certificateA,
                certificatePassword);
            var readByReplicaC = await replicaC
                .GetRequiredService<IBffSessionStore>()
                .GetAsync("replacement-session", default);
            Assert.Equal(replacement.AccessToken, readByReplicaC?.AccessToken);
        }
        finally
        {
            await DeleteIntegrationKeysAsync(redisConnection, instance);
        }
    }

    private static ServiceProvider CreateReplica(
        string redisConnection,
        string instance,
        string currentCertificate,
        string currentPassword,
        string? previousCertificate = null,
        string? previousPassword = null,
        int readRate = 120,
        int readConcurrency = 50,
        int fileTransferConcurrency = 200)
    {
        var values = new Dictionary<string, string?>
        {
            ["Bff:Session:Enabled"] = "true",
            ["Bff:Session:CookieName"] = "__Host-pilot_session",
            ["Redis:Enabled"] = "true",
            ["Redis:ConnectionString"] = redisConnection,
            ["Redis:InstanceName"] = instance,
            ["Redis:EnvironmentName"] = "integration",
            ["Redis:KeyPepper"] = new string('p', 32),
            ["Bff:DataProtection:CertificateBase64"] = currentCertificate,
            ["Bff:DataProtection:CertificatePassword"] = currentPassword,
            ["Bff:DataProtection:PreviousCertificateBase64"] = previousCertificate,
            ["Bff:DataProtection:PreviousCertificatePassword"] = previousPassword,
            ["Identity:TimeoutSeconds"] = "10",
            ["Bff:Proxy:ReadRequestsPerMinute"] = readRate.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Bff:Proxy:ReadConcurrentRequests"] = readConcurrency.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Bff:Proxy:FileTransferConcurrentRequests"] = fileTransferConcurrency.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBffSessionInfrastructure(configuration);
        services.AddBffProxy(configuration);
        return services.BuildServiceProvider();
    }

    private static BffSessionRecord CreateSession(string suffix)
    {
        var now = DateTime.UtcNow;
        var institution = new BffAuthInstitution(
            Guid.NewGuid(),
            "Integration Institution",
            "Admin",
            true,
            new Dictionary<string, string>());
        return new BffSessionRecord(
            $"access-{suffix}",
            now.AddMinutes(10),
            $"refresh-{suffix}",
            now.AddHours(12),
            new BffAuthUser(Guid.NewGuid(), "Integration User", null, "Admin", false),
            [institution],
            institution,
            now,
            now,
            now.AddMinutes(30),
            now.AddHours(12));
    }

    private static string CreateCertificate(string commonName, string password)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={commonName}",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddDays(1));
        return Convert.ToBase64String(certificate.Export(X509ContentType.Pkcs12, password));
    }

    private static async Task DeleteIntegrationKeysAsync(string redisConnection, string instance)
    {
        using var redis = await ConnectionMultiplexer.ConnectAsync(redisConnection);
        var database = redis.GetDatabase();
        var endpoints = redis.GetEndPoints();
        if (endpoints.Length == 0)
        {
            return;
        }

        var keys = redis.GetServer(endpoints[0])
            .Keys(database.Database, $"{instance}:integration:bff*")
            .ToArray();
        if (keys.Length > 0)
        {
            await database.KeyDeleteAsync(keys);
        }
    }
}
