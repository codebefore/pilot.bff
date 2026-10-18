using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Pilot.Bff.Api.Contracts;
using Pilot.Bff.Api.Options;
using Pilot.Bff.Api.Session;

namespace Pilot.Bff.Tests;

public sealed class BffWebSocketIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> factory;

    public BffWebSocketIntegrationTests(WebApplicationFactory<Program> factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task MessagingWebSocket_UsesServerSessionAuthority_AndBridgesFrames()
    {
        var observed = new TaskCompletionSource<(string? Authorization, string? Institution)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var upstream = await EchoWebSocketServer.StartAsync(observed);
        var session = CreateSession();
        using var configuredFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Bff:Proxy:Enabled", "true");
            builder.UseSetting("Bff:Proxy:GatewayBaseUrl", upstream.BaseAddress.ToString());
            builder.UseSetting("Bff:BrowserSecurity:AllowedOrigins:0", "http://localhost:3000");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IBffSessionStore>();
                services.AddSingleton<IBffSessionStore>(new FixedSessionStore(session));
                services.AddSingleton<IOptions<BffSessionOptions>>(Options.Create(new BffSessionOptions
                {
                    Enabled = true,
                    IdleTimeoutMinutes = 30,
                    AbsoluteTimeoutHours = 12
                }));
            });
        });
        var client = configuredFactory.Server.CreateWebSocketClient();
        client.ConfigureRequest = request =>
        {
            request.Headers.Cookie = "__Host-pilot_session=session-id";
            request.Headers.Origin = "http://localhost:3000";
            request.Headers.Authorization = "Bearer browser-token";
        };
        using var socket = await client.ConnectAsync(
            new Uri("ws://localhost/bff/v1/messaging/hubs/messaging?id=connection-id"),
            default);
        var payload = Encoding.UTF8.GetBytes("signalr-frame");

        await socket.SendAsync(payload, WebSocketMessageType.Text, true, default);
        var buffer = new byte[128];
        var received = await socket.ReceiveAsync(buffer, default);
        var authority = await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("signalr-frame", Encoding.UTF8.GetString(buffer, 0, received.Count));
        Assert.Equal("Bearer server-access-token", authority.Authorization);
        Assert.Equal(session.ActiveInstitution?.Id.ToString("D"), authority.Institution);
    }

    private static BffSessionRecord CreateSession()
    {
        var now = DateTime.UtcNow;
        var institution = new BffAuthInstitution(
            Guid.NewGuid(),
            "Test Institution",
            "Admin",
            true,
            new Dictionary<string, string>());
        return new BffSessionRecord(
            "server-access-token",
            now.AddHours(1),
            "server-refresh-token",
            now.AddDays(1),
            new BffAuthUser(Guid.NewGuid(), "Test User", null, "Admin", false),
            [institution],
            institution,
            now,
            now,
            now.AddMinutes(30),
            now.AddHours(12));
    }

    private sealed class FixedSessionStore(BffSessionRecord session) : IBffSessionStore
    {
        public Task SetAsync(string sessionId, BffSessionRecord value, CancellationToken token) =>
            Task.CompletedTask;

        public Task<BffSessionRecord?> GetAsync(string sessionId, CancellationToken token) =>
            Task.FromResult<BffSessionRecord?>(sessionId == "session-id" ? session : null);

        public Task<bool> TryUpdateAsync(string sessionId, BffSessionRecord value, CancellationToken token) =>
            Task.FromResult(true);

        public Task<bool> RotateAsync(
            string currentSessionId,
            string replacementSessionId,
            BffSessionRecord value,
            CancellationToken token) => Task.FromResult(true);

        public Task DeleteAsync(string sessionId, CancellationToken token) => Task.CompletedTask;
    }

    private sealed class EchoWebSocketServer(WebApplication app, Uri baseAddress) : IAsyncDisposable
    {
        public Uri BaseAddress { get; } = baseAddress;

        public static async Task<EchoWebSocketServer> StartAsync(
            TaskCompletionSource<(string? Authorization, string? Institution)> observed)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            app.UseWebSockets();
            app.Map("/v1/messaging/hubs/messaging", async context =>
            {
                observed.TrySetResult((
                    context.Request.Headers.Authorization.ToString(),
                    context.Request.Headers["X-Institution-Id"].ToString()));
                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                var buffer = new byte[128];
                var received = await socket.ReceiveAsync(buffer, context.RequestAborted);
                await socket.SendAsync(
                    buffer.AsMemory(0, received.Count),
                    received.MessageType,
                    received.EndOfMessage,
                    context.RequestAborted);
            });
            await app.StartAsync();
            var address = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
                .Features.Get<IServerAddressesFeature>()!
                .Addresses.Single();
            return new EchoWebSocketServer(app, new Uri(address));
        }

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
