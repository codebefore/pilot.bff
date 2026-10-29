using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Pilot.Bff.Api.Auth;
using Pilot.Bff.Api.Options;
using Pilot.Bff.Api.Proxy;

namespace Pilot.Bff.Tests;

public sealed class BffProxySecurityTests
{
    [Theory]
    [InlineData("GET", "candidates", "123/notes", "/v1/candidates/123/notes")]
    [InlineData("POST", "training", "groups", "/v1/training/groups")]
    [InlineData("GET", "identity", "users/123", "/v1/identity/users/123")]
    [InlineData("DELETE", "identity", "auth/sessions/123", "/v1/identity/auth/sessions/123")]
    [InlineData("GET", "platform", "notifications", "/v1/platform/notifications")]
    [InlineData("GET", "platform", "maintenance", "/v1/platform/maintenance")]
    [InlineData("HEAD", "platform", "maintenance", "/v1/platform/maintenance")]
    [InlineData("GET", "platform", "outbox/health", "/v1/platform/outbox/health")]
    [InlineData("HEAD", "platform", "health/domain-events", "/v1/platform/health/domain-events")]
    [InlineData("GET", "messaging", "hubs/messaging", "/v1/messaging/hubs/messaging")]
    [InlineData("POST", "messaging", "hubs/messaging/negotiate", "/v1/messaging/hubs/messaging/negotiate")]
    public void RoutePolicy_AllowsOnlyExplicitApplicationRoutes(
        string method,
        string service,
        string path,
        string expected)
    {
        var policy = new BffProxyRoutePolicy();

        var allowed = policy.TryResolve(method, service, path, out var target);

        Assert.True(allowed);
        Assert.Equal(expected, target);
    }

    [Theory]
    [InlineData("GET", "unknown", "anything")]
    [InlineData("OPTIONS", "candidates", "123")]
    [InlineData("GET", "Candidates", "123")]
    [InlineData("GET", "candidates", "internal/export")]
    [InlineData("GET", "training", "health")]
    [InlineData("POST", "platform", "outbox/health")]
    [InlineData("GET", "platform", "health/ready")]
    [InlineData("GET", "candidates", "outbox/health")]
    [InlineData("POST", "identity", "auth/refresh")]
    [InlineData("POST", "identity", "auth/logout")]
    [InlineData("POST", "platform", "maintenance")]
    [InlineData("GET", "platform", "maintenance/details")]
    [InlineData("GET", "identity", "maintenance")]
    [InlineData("GET", "messaging", "hubs/chat")]
    [InlineData("GET", "candidates", "../internal")]
    [InlineData("GET", "candidates", "%2e%2e/internal")]
    public void RoutePolicy_RejectsUnknownInternalAndManagementRoutes(
        string method,
        string service,
        string path)
    {
        var policy = new BffProxyRoutePolicy();

        Assert.False(policy.TryResolve(method, service, path, out _));
    }

    [Theory]
    [InlineData("GET", "/v1/training/practice-candidates", BffProxyTrafficKind.Read)]
    [InlineData("HEAD", "/v1/training/practice-candidates", BffProxyTrafficKind.Read)]
    [InlineData("POST", "/v1/training/practice-candidates/lesson-summaries/query", BffProxyTrafficKind.Read)]
    [InlineData("POST", "/v1/document/documents/candidate-photos", BffProxyTrafficKind.Read)]
    [InlineData("POST", "/v1/training/practice-candidates/lesson-summaries", BffProxyTrafficKind.Mutation)]
    [InlineData("POST", "/v1/training/practice-candidates/lesson-summaries/query/other", BffProxyTrafficKind.Mutation)]
    [InlineData("POST", "/v1/document/documents/candidate-photos/other", BffProxyTrafficKind.Mutation)]
    [InlineData("PUT", "/v1/training/practice-candidates/lesson-summaries/query", BffProxyTrafficKind.Mutation)]
    [InlineData("PUT", "/v1/document/documents/candidate-photos", BffProxyTrafficKind.Mutation)]
    [InlineData("GET", "/v1/document/candidates/11111111-1111-1111-1111-111111111111/documents/22222222-2222-2222-2222-222222222222/download", BffProxyTrafficKind.FileTransfer)]
    [InlineData("HEAD", "/v1/document/vehicles/11111111-1111-1111-1111-111111111111/documents/22222222-2222-2222-2222-222222222222/download", BffProxyTrafficKind.FileTransfer)]
    [InlineData("GET", "/v1/document/instructors/11111111-1111-1111-1111-111111111111/assignments/22222222-2222-2222-2222-222222222222/documents/33333333-3333-3333-3333-333333333333/file", BffProxyTrafficKind.FileTransfer)]
    [InlineData("GET", "/v1/document/correspondence/11111111-1111-1111-1111-111111111111/files/22222222-2222-2222-2222-222222222222/download", BffProxyTrafficKind.FileTransfer)]
    [InlineData("GET", "/v1/document/correspondence/11111111-1111-1111-1111-111111111111/packet-pdf", BffProxyTrafficKind.FileTransfer)]
    [InlineData("GET", "/v1/document/legislation/documents/11111111-1111-1111-1111-111111111111/files/22222222-2222-2222-2222-222222222222/download", BffProxyTrafficKind.FileTransfer)]
    [InlineData("GET", "/v1/training/instructors/11111111-1111-1111-1111-111111111111/photo", BffProxyTrafficKind.FileTransfer)]
    [InlineData("GET", "/v1/platform/institution-settings/logo/file", BffProxyTrafficKind.FileTransfer)]
    [InlineData("GET", "/v1/platform/institution-backups/11111111-1111-1111-1111-111111111111/download", BffProxyTrafficKind.FileTransfer)]
    [InlineData("POST", "/v1/document/candidates/11111111-1111-1111-1111-111111111111/documents/22222222-2222-2222-2222-222222222222/download", BffProxyTrafficKind.Mutation)]
    [InlineData("GET", "/v1/document/candidates/not-a-guid/documents/22222222-2222-2222-2222-222222222222/download", BffProxyTrafficKind.Read)]
    [InlineData("GET", "/v1/document/candidates/11111111-1111-1111-1111-111111111111/documents/22222222-2222-2222-2222-222222222222/download/extra", BffProxyTrafficKind.Read)]
    public void RoutePolicy_ClassifiesTrafficByExactMethodAndPath(
        string method,
        string targetPath,
        BffProxyTrafficKind expectedTrafficKind)
    {
        var policy = new BffProxyRoutePolicy();

        Assert.Equal(expectedTrafficKind, policy.ClassifyTraffic(method, targetPath));
    }

    [Fact]
    public void RequestFactory_StripsBrowserCredentialsAndAddsServerSessionAuthority()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.QueryString = new QueryString("?page=2");
        context.Request.Headers.Authorization = "Bearer browser-token";
        context.Request.Headers.Cookie = "secret=cookie";
        context.Request.Headers.Host = "evil.example";
        context.Request.Headers["X-Institution-Id"] = Guid.NewGuid().ToString("D");
        context.Request.Headers["X-Forwarded-For"] = "203.0.113.10";
        var targetClientId = Guid.NewGuid();
        var transferOwner = Guid.NewGuid();
        context.Request.Headers["X-Pilot-Target-Client-Id"] = targetClientId.ToString("D");
        context.Request.Headers["X-Pilot-Transfer-Owner"] = transferOwner.ToString("D");
        context.Request.Headers["X-Course-Type"] = "SRC";
        context.Request.Headers.Accept = "application/json";
        var institutionId = Guid.NewGuid();
        var authorization = new BffProxyAuthorization(
            Guid.NewGuid(),
            "server-access-token",
            institutionId,
            DateTime.UtcNow.AddMinutes(30));

        using var request = new BffProxyRequestFactory().Create(
            context.Request,
            "/v1/candidates/123",
            authorization);

        Assert.Equal("/v1/candidates/123?page=2", request.RequestUri?.ToString());
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal("server-access-token", request.Headers.Authorization?.Parameter);
        Assert.Equal(institutionId.ToString("D"), request.Headers.GetValues("X-Institution-Id").Single());
        Assert.Equal(targetClientId.ToString("D"), request.Headers.GetValues("X-Pilot-Target-Client-Id").Single());
        Assert.Equal(transferOwner.ToString("D"), request.Headers.GetValues("X-Pilot-Transfer-Owner").Single());
        Assert.Equal("SRC", request.Headers.GetValues("X-Course-Type").Single());
        Assert.Equal("application/json", request.Headers.Accept.Single().MediaType);
        Assert.False(request.Headers.Contains("Cookie"));
        Assert.False(request.Headers.Contains("Host"));
        Assert.False(request.Headers.Contains("X-Forwarded-For"));
        Assert.False(request.Headers.Contains("X-Migration-Access-Token"));
    }

    [Fact]
    public void RequestFactory_AddsServerMigrationCredentialOnlyToImportRoutes()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Headers["X-Migration-Access-Token"] = "browser-token";
        var authorization = new BffProxyAuthorization(
            Guid.NewGuid(),
            "server-access-token",
            Guid.NewGuid(),
            DateTime.UtcNow.AddMinutes(30),
            "server-migration-token");
        var factory = new BffProxyRequestFactory();

        using var importRequest = factory.Create(
            context.Request,
            "/v1/candidates/imports/cizgisoft",
            authorization);
        using var ordinaryRequest = factory.Create(
            context.Request,
            "/v1/candidates/123",
            authorization);

        Assert.Equal(
            "server-migration-token",
            importRequest.Headers.GetValues("X-Migration-Access-Token").Single());
        Assert.False(ordinaryRequest.Headers.Contains("X-Migration-Access-Token"));
    }

    [Theory]
    [InlineData("POST", "/bff/v1/document/candidates/11111111-1111-1111-1111-111111111111/documents", 2048)]
    [InlineData("POST", "/bff/v1/document/vehicles/11111111-1111-1111-1111-111111111111/documents", 2048)]
    [InlineData("PUT", "/bff/v1/document/vehicles/11111111-1111-1111-1111-111111111111/documents/11111111-1111-1111-1111-111111111111", 2048)]
    [InlineData("POST", "/bff/v1/document/correspondence/11111111-1111-1111-1111-111111111111/files", 2048)]
    [InlineData("POST", "/bff/v1/document/instructors/11111111-1111-1111-1111-111111111111/assignments/11111111-1111-1111-1111-111111111111/documents", 4096)]
    public void RequestBodyPolicy_AllowsOnlyExplicitMultipartDocumentRoutes(
        string method,
        string path,
        long expected)
    {
        var policy = CreateBodyPolicy();
        var context = CreateRequest(method, path, "multipart/form-data; boundary=pilot-test");

        Assert.Equal(expected, policy.GetMaxRequestBodyBytes(context.Request));
    }

    [Theory]
    [InlineData("POST", "/bff/v1/document/candidates/11111111-1111-1111-1111-111111111111/documents", "application/json")]
    [InlineData("GET", "/bff/v1/document/candidates/11111111-1111-1111-1111-111111111111/documents", "multipart/form-data; boundary=pilot-test")]
    [InlineData("POST", "/bff/v1/document/candidates/00000000-0000-0000-0000-000000000000/documents", "multipart/form-data; boundary=pilot-test")]
    [InlineData("POST", "/bff/v1/candidates/imports/cizgisoft/sources", "multipart/form-data; boundary=pilot-test")]
    [InlineData("POST", "/bff/v1/document/instructors/11111111-1111-1111-1111-111111111111/assignments/11111111-1111-1111-1111-111111111111/documents", "multipart/form-data")]
    [InlineData("POST", "/bff/v1/document/instructors/11111111-1111-1111-1111-111111111111/assignments/11111111-1111-1111-1111-111111111111/documents", "multipart/form-data; boundary=\"\"")]
    public void RequestBodyPolicy_KeepsDefaultForOtherMethodsRoutesAndContentTypes(
        string method,
        string path,
        string contentType)
    {
        var policy = CreateBodyPolicy();
        var context = CreateRequest(method, path, contentType);

        Assert.Equal(1024, policy.GetMaxRequestBodyBytes(context.Request));
    }

    [Fact]
    public async Task TrafficLimiter_EnforcesPerUserRateWindow()
    {
        var time = new TestTimeProvider(DateTimeOffset.UtcNow);
        using var limiter = CreateTrafficLimiter(time, readRate: 1, readConcurrency: 2);
        var userId = Guid.NewGuid();

        await using var first = await limiter.TryAcquireAsync(
            userId, null, null, BffProxyTrafficKind.Read, default);
        var rejected = await limiter.TryAcquireAsync(
            userId, null, null, BffProxyTrafficKind.Read, default);

        Assert.NotNull(first);
        Assert.Null(rejected);

        time.Advance(TimeSpan.FromMinutes(1));
        await using var afterWindow = await limiter.TryAcquireAsync(
            userId, null, null, BffProxyTrafficKind.Read, default);
        Assert.NotNull(afterWindow);
    }

    [Fact]
    public async Task TrafficLimiter_FailsFastAtConcurrencyLimitAndRecoversAfterRelease()
    {
        using var limiter = CreateTrafficLimiter(
            new TestTimeProvider(DateTimeOffset.UtcNow),
            readRate: 10,
            readConcurrency: 1);
        var userId = Guid.NewGuid();
        var first = await limiter.TryAcquireAsync(
            userId, null, null, BffProxyTrafficKind.Read, default);

        var rejected = await limiter.TryAcquireAsync(
            userId, null, null, BffProxyTrafficKind.Read, default);
        Assert.NotNull(first);
        Assert.Null(rejected);

        await first.DisposeAsync();
        await using var afterRelease = await limiter.TryAcquireAsync(
            userId, null, null, BffProxyTrafficKind.Read, default);
        Assert.NotNull(afterRelease);
    }

    [Fact]
    public async Task TrafficLimiter_FileTransfersBypassRateWindowAndUseSeparateConcurrency()
    {
        using var limiter = CreateTrafficLimiter(
            new TestTimeProvider(DateTimeOffset.UtcNow),
            readRate: 1,
            readConcurrency: 1,
            fileTransferConcurrency: 2);
        var userId = Guid.NewGuid();

        var firstFile = await limiter.TryAcquireAsync(
            userId, null, null, BffProxyTrafficKind.FileTransfer, default);
        var secondFile = await limiter.TryAcquireAsync(
            userId, null, null, BffProxyTrafficKind.FileTransfer, default);
        var rejectedFile = await limiter.TryAcquireAsync(
            userId, null, null, BffProxyTrafficKind.FileTransfer, default);
        await using var firstRead = await limiter.TryAcquireAsync(
            userId, null, null, BffProxyTrafficKind.Read, default);

        Assert.NotNull(firstFile);
        Assert.NotNull(secondFile);
        Assert.Null(rejectedFile);
        Assert.NotNull(firstRead);
        Assert.Null(await limiter.TryAcquireAsync(
            userId, null, null, BffProxyTrafficKind.Read, default));

        await firstFile.DisposeAsync();
        await using var afterRelease = await limiter.TryAcquireAsync(
            userId, null, null, BffProxyTrafficKind.FileTransfer, default);
        Assert.NotNull(afterRelease);
        await secondFile.DisposeAsync();
    }

    [Theory]
    [InlineData("access_token")]
    [InlineData("refresh_token")]
    [InlineData("Authorization")]
    [InlineData("api_key")]
    [InlineData("migration_access_token")]
    public void QueryPolicy_RejectsCredentialParameters(string parameter)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString($"?{parameter}=browser-secret&id=allowed");

        Assert.False(new BffProxyQueryPolicy().IsSafe(context.Request.Query));
    }

    private static BffProxyTrafficLimiter CreateTrafficLimiter(
        TimeProvider timeProvider,
        int readRate,
        int readConcurrency,
        int fileTransferConcurrency = 200)
    {
        return new BffProxyTrafficLimiter(
            Options.Create(new BffProxyOptions
            {
                ReadRequestsPerMinute = readRate,
                MutationRequestsPerMinute = 10,
                ReadConcurrentRequests = readConcurrency,
                MutationConcurrentRequests = 1,
                FileTransferConcurrentRequests = fileTransferConcurrency
            }),
            timeProvider);
    }

    private static BffProxyRequestBodyPolicy CreateBodyPolicy() =>
        new(Options.Create(new BffProxyOptions
        {
            MaxRequestBodyBytes = 1024,
            DocumentUploadMaxRequestBodyBytes = 2048,
            InstructorDocumentUploadMaxRequestBodyBytes = 4096
        }));

    private static DefaultHttpContext CreateRequest(
        string method,
        string path,
        string contentType)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Request.ContentType = contentType;
        return context;
    }

    private sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; private set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;

        public void Advance(TimeSpan duration) => Now += duration;
    }
}
