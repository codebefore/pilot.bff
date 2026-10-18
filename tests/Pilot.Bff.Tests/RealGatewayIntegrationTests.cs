using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Pilot.Bff.Api.Contracts;
using Pilot.Bff.Api.Options;
using Pilot.Bff.Api.Session;

namespace Pilot.Bff.Tests;

public sealed class RealGatewayIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> factory;

    public RealGatewayIntegrationTests(WebApplicationFactory<Program> factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task Bff_ForwardsThroughActualGateway_ToTransformedServiceRoute()
    {
        var gatewayDll = Environment.GetEnvironmentVariable("BFF_TEST_GATEWAY_DLL");
        if (string.IsNullOrWhiteSpace(gatewayDll))
        {
            return;
        }

        Assert.True(File.Exists(gatewayDll), $"Gateway assembly does not exist: {gatewayDll}");

        var received = new TaskCompletionSource<DownstreamRequest>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var downstream = await StartDownstreamAsync(received);
        using var gateway = StartGateway(gatewayDll, downstream.BaseAddress);
        await WaitUntilHealthyAsync(gateway.BaseAddress, gateway.Process);

        var session = CreateSession();
        using var configuredFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Bff:Proxy:Enabled", "true");
            builder.UseSetting("Bff:Proxy:GatewayBaseUrl", gateway.BaseAddress.ToString());
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IBffSessionStore>();
                services.AddSingleton<IBffSessionStore>(new FixedSessionStore("session-id", session));
                services.AddSingleton<IOptions<BffSessionOptions>>(Options.Create(new BffSessionOptions
                {
                    Enabled = true,
                    IdleTimeoutMinutes = 30,
                    AbsoluteTimeoutHours = 12
                }));
            });
        });
        using var client = configuredFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/bff/v1/candidates/123/notes?view=summary");
        request.Headers.TryAddWithoutValidation("Cookie", "__Host-pilot_session=session-id");

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("actual-gateway-ok", await response.Content.ReadAsStringAsync());

        var captured = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("/api/candidates/123/notes", captured.Path);
        Assert.Equal("?view=summary", captured.Query);
        Assert.Equal("Bearer server-access-token", captured.Authorization);
        Assert.Equal(session.ActiveInstitution?.Id.ToString("D"), captured.InstitutionId);
    }

    private static async Task<DownstreamServer> StartDownstreamAsync(
        TaskCompletionSource<DownstreamRequest> received)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.Run(async context =>
        {
            received.TrySetResult(new DownstreamRequest(
                context.Request.Path.Value,
                context.Request.QueryString.Value,
                context.Request.Headers.Authorization.ToString(),
                context.Request.Headers["X-Institution-Id"].ToString()));
            context.Response.ContentType = "text/plain";
            await context.Response.WriteAsync("actual-gateway-ok");
        });
        await app.StartAsync();

        var addresses = app.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>();
        var address = addresses?.Addresses.Single()
            ?? throw new InvalidOperationException("The downstream test server did not publish an address.");
        return new DownstreamServer(app, new Uri(address));
    }

    private static GatewayProcess StartGateway(string gatewayDll, Uri downstreamAddress)
    {
        var gatewayAddress = new Uri($"http://127.0.0.1:{ReservePort()}");
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.GetDirectoryName(gatewayDll)
                ?? throw new InvalidOperationException("Gateway assembly directory could not be resolved."),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(gatewayDll);
        startInfo.ArgumentList.Add("--urls");
        startInfo.ArgumentList.Add(gatewayAddress.ToString());
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        startInfo.Environment[
            "ReverseProxy__Clusters__candidates__Destinations__destination1__Address"] =
            downstreamAddress.ToString();

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The Gateway process could not be started.");
        return new GatewayProcess(process, gatewayAddress);
    }

    private static async Task WaitUntilHealthyAsync(Uri gatewayAddress, Process process)
    {
        using var client = new HttpClient
        {
            BaseAddress = gatewayAddress,
            Timeout = TimeSpan.FromSeconds(1)
        };
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (process.HasExited)
            {
                var output = await process.StandardOutput.ReadToEndAsync();
                var error = await process.StandardError.ReadToEndAsync();
                throw new InvalidOperationException(
                    $"Gateway exited before becoming healthy. stdout: {output} stderr: {error}");
            }

            try
            {
                using var response = await client.GetAsync("/health");
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
            }
            catch (TaskCanceledException)
            {
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("Gateway did not become healthy within 10 seconds.");
    }

    private static int ReservePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static BffSessionRecord CreateSession()
    {
        var now = DateTime.UtcNow;
        var institution = new BffAuthInstitution(
            Guid.NewGuid(),
            "Gateway Integration Institution",
            "Admin",
            true,
            new Dictionary<string, string>());
        return new BffSessionRecord(
            "server-access-token",
            now.AddHours(1),
            "server-refresh-token",
            now.AddDays(1),
            new BffAuthUser(Guid.NewGuid(), "Gateway Integration User", null, "Admin", false),
            [institution],
            institution,
            now,
            now,
            now.AddMinutes(30),
            now.AddHours(12));
    }

    private sealed class FixedSessionStore(string sessionId, BffSessionRecord session) : IBffSessionStore
    {
        public Task SetAsync(
            string requestedSessionId,
            BffSessionRecord requestedSession,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<BffSessionRecord?> GetAsync(
            string requestedSessionId,
            CancellationToken cancellationToken) =>
            Task.FromResult<BffSessionRecord?>(requestedSessionId == sessionId ? session : null);

        public Task<bool> TryUpdateAsync(
            string requestedSessionId,
            BffSessionRecord requestedSession,
            CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<bool> RotateAsync(
            string currentSessionId,
            string replacementSessionId,
            BffSessionRecord requestedSession,
            CancellationToken cancellationToken) => Task.FromResult(false);

        public Task DeleteAsync(string requestedSessionId, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed record DownstreamRequest(
        string? Path,
        string? Query,
        string Authorization,
        string InstitutionId);

    private sealed class DownstreamServer(WebApplication app, Uri baseAddress) : IAsyncDisposable
    {
        public Uri BaseAddress { get; } = baseAddress;

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private sealed class GatewayProcess : IDisposable
    {
        public GatewayProcess(Process process, Uri baseAddress)
        {
            Process = process;
            BaseAddress = baseAddress;
        }

        public Process Process { get; }

        public Uri BaseAddress { get; }

        public void Dispose()
        {
            if (!Process.HasExited)
            {
                Process.Kill(entireProcessTree: true);
                Process.WaitForExit(TimeSpan.FromSeconds(5));
            }

            Process.Dispose();
        }
    }
}
