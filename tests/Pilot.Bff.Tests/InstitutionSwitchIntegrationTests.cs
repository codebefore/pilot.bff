using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Pilot.Bff.Api.Auth;
using Pilot.Bff.Api.Contracts;
using Pilot.Bff.Api.Options;
using Pilot.Bff.Api.Proxy;
using Pilot.Bff.Api.Session;

namespace Pilot.Bff.Tests;

public sealed partial class BffAuthServiceTests
{
    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task LateProxyResponse_PreservesSelectedInstitution(HttpStatusCode lateStatus)
    {
        var gateway = new DelayedGateway(lateStatus);
        var identity = new FakeIdentityAuthClient { Login = CreateLoginResponse() };
        using var root = new WebApplicationFactory<Program>();
        using var factory = root.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Bff:Proxy:Enabled", "true");
            builder.UseSetting("Bff:BrowserSecurity:AllowedOrigins:0", "https://localhost");
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IIdentityAuthClient>(identity);
                services.AddSingleton<IBffSessionStore>(new InMemorySessionStore());
                services.AddSingleton<IBffSessionLock>(new InMemorySessionLock());
                services.AddSingleton<IOptions<BffSessionOptions>>(Options.Create(new BffSessionOptions { Enabled = true }));
                services.AddHttpClient(BffProxyInfrastructure.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => gateway);
            });
        });
        using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        var csrf = await client.GetFromJsonAsync<AntiforgeryTokenResponse>("/bff/antiforgery");
        client.DefaultRequestHeaders.Add("Origin", "https://localhost");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", csrf!.Token);
        using var login = await client.PostAsJsonAsync("/bff/auth/otp/verify", new VerifyLoginOtpRequest("905001112233", "123456"));
        login.EnsureSuccessStatusCode();

        var loginSummary = await login.Content.ReadFromJsonAsync<BffSessionResponse>();
        var loginCookie = Microsoft.Net.Http.Headers.SetCookieHeaderValue.Parse(login.Headers.GetValues("Set-Cookie").Single());
        var pending = client.GetAsync("/bff/v1/candidates/123");
        await gateway.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var target = Guid.NewGuid();
        identity.Login = CreateLoginResponse(target) with { User = identity.Login.User };
        using var selected = await client.PostAsJsonAsync("/bff/session/institution", new SelectInstitutionRequest(target));
        gateway.Release.SetResult();
        selected.EnsureSuccessStatusCode();
        var selectedSummary = await selected.Content.ReadFromJsonAsync<BffSessionResponse>();
        Assert.Equal(loginSummary!.AbsoluteExpiresAtUtc, selectedSummary!.AbsoluteExpiresAtUtc);
        Assert.Equal(new DateTimeOffset(loginSummary.AbsoluteExpiresAtUtc).ToUnixTimeSeconds(), loginCookie.Expires!.Value.ToUnixTimeSeconds());
        var selectedCookie = Microsoft.Net.Http.Headers.SetCookieHeaderValue.Parse(selected.Headers.GetValues("Set-Cookie").Single());
        Assert.Equal(loginCookie.Expires, selectedCookie.Expires);
        Assert.NotEqual(loginCookie.Value, selectedCookie.Value);
        using var late = await pending;
        Assert.Equal(lateStatus, late.StatusCode);
        using var session = await client.GetAsync("/bff/session");
        Assert.Equal(HttpStatusCode.OK, session.StatusCode);
        Assert.Equal(target, (await session.Content.ReadFromJsonAsync<BffSessionResponse>())?.ActiveInstitution?.Id);
        Assert.False(session.Headers.Contains("Set-Cookie"));
        using var oldClient = factory.CreateClient(new() { BaseAddress = client.BaseAddress!, HandleCookies = false });
        oldClient.DefaultRequestHeaders.Add("Cookie", $"{loginCookie.Name}={loginCookie.Value}");
        using var previous = await oldClient.GetAsync("/bff/session");
        Assert.Equal(HttpStatusCode.Unauthorized, previous.StatusCode);
        Assert.False(previous.Headers.Contains("Set-Cookie"));
        using var logout = await client.PostAsync("/bff/logout", null);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        using var ended = await client.GetAsync("/bff/session");
        Assert.Equal(HttpStatusCode.Unauthorized, ended.StatusCode);
    }

    private sealed class DelayedGateway(HttpStatusCode status) : HttpMessageHandler
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Entered.SetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(status);
        }
    }
}
