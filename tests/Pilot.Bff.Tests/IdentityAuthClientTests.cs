using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Pilot.Bff.Api.Auth;
using Pilot.Bff.Api.Contracts;

namespace Pilot.Bff.Tests;

public sealed class IdentityAuthClientTests
{
    private const string BrowserAgent = "Mozilla/5.0 (Windows NT 10.0) Chrome/120.0 Safari/537.36";

    private static HttpContextAccessor BrowserContext(string userAgent)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.UserAgent = userAgent;
        context.Request.Headers.Authorization = "Bearer browser-token";
        return new HttpContextAccessor { HttpContext = context };
    }

    [Fact]
    public async Task SelectInstitution_UsesFixedIdentityPathAndServerBearerToken()
    {
        var institutionId = Guid.NewGuid();
        var login = CreateLoginResponse(institutionId);
        var handler = new RecordingHandler(JsonSerializer.Serialize(login));
        var client = new IdentityAuthClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("http://pilot-identity/")
        }, BrowserContext(BrowserAgent));

        var result = await client.SelectInstitutionAsync(
            institutionId,
            "server-side-access-token",
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal($"http://pilot-identity/api/auth/institutions/{institutionId:D}/select", handler.Uri?.ToString());
        Assert.Equal("Bearer", handler.AuthorizationScheme);
        Assert.Equal("server-side-access-token", handler.AuthorizationParameter);
        Assert.Equal(BrowserAgent, handler.UserAgent);
    }

    [Theory]
    [InlineData(BrowserAgent, BrowserAgent)]
    [InlineData("", "")]
    [InlineData("invalid\r\nvalue", "")]
    public async Task VerifyLogin_ForwardsValidAgentAndRejectsEmptySuccessPayload(string agent, string expected)
    {
        var handler = new RecordingHandler("null");
        var client = new IdentityAuthClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("http://pilot-identity/")
        }, BrowserContext(agent));

        var result = await client.VerifyLoginCodeAsync(
            new VerifyLoginOtpRequest("905001112233", "123456"),
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadGateway, result.StatusCode);
        Assert.Null(result.Value);
        Assert.Equal(expected, handler.UserAgent);
        Assert.Null(handler.AuthorizationScheme);
    }

    [Fact]
    public async Task Refresh_UsesFixedIdentityPathWithoutBrowserAuthorizationHeader()
    {
        var login = CreateLoginResponse(Guid.NewGuid());
        var handler = new RecordingHandler(JsonSerializer.Serialize(login));
        var client = new IdentityAuthClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("http://pilot-identity/")
        }, BrowserContext(BrowserAgent));

        var result = await client.RefreshAsync("server-side-refresh-token", CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("http://pilot-identity/api/auth/refresh", handler.Uri?.ToString());
        Assert.Null(handler.AuthorizationScheme);
        Assert.Contains("server-side-refresh-token", handler.Body, StringComparison.Ordinal);
        Assert.Equal(BrowserAgent, handler.UserAgent);
    }

    private static IdentityLoginResponse CreateLoginResponse(Guid institutionId)
    {
        var institution = new BffAuthInstitution(
            institutionId,
            "Test Institution",
            "Admin",
            true,
            new Dictionary<string, string>());
        return new IdentityLoginResponse(
            "access-token",
            DateTime.UtcNow.AddHours(1),
            "refresh-token",
            DateTime.UtcNow.AddDays(14),
            new BffAuthUser(Guid.NewGuid(), "Test User", null, "Admin", false),
            [institution],
            institution);
    }

    private sealed class RecordingHandler(string responseBody) : HttpMessageHandler
    {
        public Uri? Uri { get; private set; }

        public string? AuthorizationScheme { get; private set; }

        public string? AuthorizationParameter { get; private set; }

        public string? Body { get; private set; }
        public string? UserAgent { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Uri = request.RequestUri;
            UserAgent = request.Headers.UserAgent.ToString();
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            AuthorizationParameter = request.Headers.Authorization?.Parameter;
            Body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            };
        }
    }
}
