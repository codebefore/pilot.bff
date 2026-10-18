using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Pilot.Bff.Tests;

public sealed class BffBaselineTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public BffBaselineTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
        _client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
    }

    [Fact]
    public async Task Readiness_FailsClosed_WhenProxyIsEnabledWithoutSessions()
    {
        using var factory = _factory.WithWebHostBuilder(builder =>
            builder.UseSetting("Bff:Proxy:Enabled", "true"));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health/ready");
        using var livenessResponse = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, livenessResponse.StatusCode);
    }

    [Fact]
    public async Task EnabledProxyRoute_FailsClosed_WhenSessionsAreDisabled()
    {
        using var factory = _factory.WithWebHostBuilder(builder =>
            builder.UseSetting("Bff:Proxy:Enabled", "true"));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/bff/v1/candidates");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task EnabledProxy_RejectsOversizedBodyBeforeSessionAndAntiforgeryWork()
    {
        using var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Bff:Proxy:Enabled", "true");
            builder.UseSetting("Bff:Proxy:MaxRequestBodyBytes", "1024");
        });
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/bff/v1/candidates/imports")
        {
            Content = new ByteArrayContent(new byte[1025])
        };

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task ExplicitMultipartDocumentUpload_UsesRouteSpecificBodyLimit()
    {
        using var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Bff:Proxy:Enabled", "true");
            builder.UseSetting("Bff:Proxy:MaxRequestBodyBytes", "1024");
            builder.UseSetting("Bff:Proxy:DocumentUploadMaxRequestBodyBytes", "2048");
        });
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/bff/v1/document/candidates/{Guid.NewGuid():D}/documents")
        {
            Content = new ByteArrayContent(new byte[1500])
        };
        request.Content.Headers.ContentType =
            System.Net.Http.Headers.MediaTypeHeaderValue.Parse(
                "multipart/form-data; boundary=pilot-test");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/health/ready")]
    public async Task HealthEndpoints_ReturnSuccess(string path)
    {
        using var response = await _client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task ServiceInfo_ReportsDisabledSessionAndProxy()
    {
        using var response = await _client.GetAsync("/bff/service-info");
        var payload = await response.Content.ReadFromJsonAsync<ServiceInfo>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(payload);
        Assert.Equal("pilot-bff", payload.Service);
        Assert.False(payload.SessionEnabled);
        Assert.False(payload.ProxyEnabled);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
    }

    [Fact]
    public async Task UnknownBffRoute_IsNotProxied()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/bff/v1/candidates/candidates");
        request.Headers.Authorization = new("Bearer", "browser-supplied-token");
        request.Headers.TryAddWithoutValidation("X-Institution-Id", Guid.NewGuid().ToString());

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task UntrustedHost_IsRejected()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Host = "attacker.example";

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SessionEndpoint_IsUnavailableByDefault()
    {
        using var response = await _client.GetAsync("/bff/session");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task UnsafeBffRequest_FromForeignOrigin_IsRejected()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/bff/logout");
        request.Headers.TryAddWithoutValidation("Origin", "https://attacker.example");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "cross-site");

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task UnsafeBffRequest_FromAllowedOrigin_ReachesDisabledSessionGuard()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/bff/logout");
        request.Headers.TryAddWithoutValidation("Origin", "http://localhost:3000");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    private sealed record ServiceInfo(string Service, bool SessionEnabled, bool ProxyEnabled);
}
