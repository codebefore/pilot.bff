using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pilot.Bff.Api.Contracts;
using Pilot.Bff.Api.Options;
using Pilot.Bff.Api.Proxy;
using Pilot.Bff.Api.Session;

namespace Pilot.Bff.Tests;

public sealed class BffProxyIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string InstitutionContextSigningKey = "test-institution-context-signing-key-1234567890";
    private readonly WebApplicationFactory<Program> factory;

    public BffProxyIntegrationTests(WebApplicationFactory<Program> factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task Proxy_ForwardsOnlyServerAuthorityAndStripsUnsafeUpstreamHeaders()
    {
        var gateway = new RecordingGatewayHandler();
        var session = CreateSession();
        using var configuredFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Bff:Proxy:Enabled", "true");
            builder.UseSetting("Bff:Proxy:InstitutionContextSigningKey", InstitutionContextSigningKey);
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
                services.AddHttpClient(BffProxyInfrastructure.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => gateway);
            });
        });
        using var client = configuredFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        using var request = new HttpRequestMessage(HttpMethod.Get, "/bff/v1/candidates/123?view=summary");
        request.Headers.TryAddWithoutValidation("Cookie", "__Host-pilot_session=session-id; browser=secret");
        request.Headers.Authorization = new("Bearer", "browser-token");
        request.Headers.TryAddWithoutValidation("X-Institution-Id", Guid.NewGuid().ToString("D"));
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", "203.0.113.50");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("gateway-ok", await response.Content.ReadAsStringAsync());
        Assert.NotNull(gateway.Request);
        Assert.Equal(
            "http://pilot-gateway/v1/candidates/123?view=summary",
            gateway.Request.RequestUri?.ToString());
        Assert.Equal("server-access-token", gateway.Request.Headers.Authorization?.Parameter);
        Assert.Equal(
            session.ActiveInstitution?.Id.ToString("D"),
            gateway.Request.Headers.GetValues("X-Institution-Id").Single());
        Assert.True(gateway.Request.Headers.Contains("X-Pilot-Institution-Context-Timestamp"));
        Assert.True(gateway.Request.Headers.Contains("X-Pilot-Institution-Context-Signature"));
        Assert.False(gateway.Request.Headers.Contains("Cookie"));
        Assert.False(gateway.Request.Headers.Contains("X-Forwarded-For"));
        Assert.Null(response.Headers.Location);
        Assert.False(response.Headers.Contains("X-Internal-Secret"));
        Assert.Contains(
            response.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith("__Host-pilot_session=", StringComparison.Ordinal));
        Assert.DoesNotContain(
            response.Headers.GetValues("Set-Cookie"),
            value => value.Contains("upstream=bad", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Observability_UsesRouteTemplateAndNeverLogsBrowserSecrets()
    {
        var logProvider = new CaptureLogProvider();
        var session = CreateSession();
        using var configuredFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Bff:Proxy:Enabled", "true");
            builder.UseSetting("Performance:SlowRequestMs", "0");
            builder.ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddProvider(logProvider);
            });
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
                services.AddHttpClient(BffProxyInfrastructure.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => new RecordingGatewayHandler());
            });
        });
        using var client = configuredFactory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/bff/v1/candidates/private-candidate-id?token=private-query-value");
        request.Headers.TryAddWithoutValidation(
            "Cookie",
            "__Host-pilot_session=session-id; secret=private-cookie-value");
        request.Headers.Authorization = new("Bearer", "private-browser-token");
        const string browserInstitutionId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
        request.Headers.TryAddWithoutValidation("X-Institution-Id", browserInstitutionId);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.Contains("X-Pilot-Trace-Id"));
        await response.Content.ReadAsStringAsync();
        response.Dispose();
        await logProvider.WaitForPerformanceLogAsync();
        var logs = string.Join('\n', logProvider.Messages);
        Assert.Contains("/bff/v1/{service}/{**path}", logs, StringComparison.Ordinal);
        Assert.Contains(session.ActiveInstitution!.Id.ToString("D"), logs, StringComparison.Ordinal);
        Assert.DoesNotContain(browserInstitutionId, logs, StringComparison.Ordinal);
        Assert.DoesNotContain("private-candidate-id", logs, StringComparison.Ordinal);
        Assert.DoesNotContain("private-query-value", logs, StringComparison.Ordinal);
        Assert.DoesNotContain("private-cookie-value", logs, StringComparison.Ordinal);
        Assert.DoesNotContain("private-browser-token", logs, StringComparison.Ordinal);
        Assert.DoesNotContain("server-access-token", logs, StringComparison.Ordinal);
        Assert.DoesNotContain("server-refresh-token", logs, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionObservability_UsesActiveServerSideInstitution()
    {
        var logProvider = new CaptureLogProvider();
        var session = CreateSession();
        using var configuredFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Bff:Proxy:Enabled", "true");
            builder.UseSetting("Performance:SlowRequestMs", "0");
            builder.ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddProvider(logProvider);
            });
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
        using var client = configuredFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/bff/session");
        request.Headers.TryAddWithoutValidation("Cookie", "__Host-pilot_session=session-id");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await response.Content.ReadAsStringAsync();
        await logProvider.WaitForPerformanceLogAsync();
        Assert.Contains(
            session.ActiveInstitution!.Id.ToString("D"),
            string.Join('\n', logProvider.Messages),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpstreamUnauthorized_DeletesRevokedBrowserSession()
    {
        var store = new FixedSessionStore("session-id", CreateSession());
        using var configuredFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Bff:Proxy:Enabled", "true");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IBffSessionStore>();
                services.AddSingleton<IBffSessionStore>(store);
                services.AddSingleton<IOptions<BffSessionOptions>>(Options.Create(new BffSessionOptions
                {
                    Enabled = true,
                    IdleTimeoutMinutes = 30,
                    AbsoluteTimeoutHours = 12
                }));
                services.AddHttpClient(BffProxyInfrastructure.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() =>
                        new RecordingGatewayHandler(HttpStatusCode.Unauthorized));
            });
        });
        using var client = configuredFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/bff/v1/candidates/123");
        request.Headers.TryAddWithoutValidation("Cookie", "__Host-pilot_session=session-id");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(store.Deleted);
        Assert.Contains(
            response.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith("__Host-pilot_session=;", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DownloadRange_IsForwardedAndPartialResponseIsStreamed()
    {
        var gateway = new RangeGatewayHandler();
        using var configuredFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Bff:Proxy:Enabled", "true");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IBffSessionStore>();
                services.AddSingleton<IBffSessionStore>(
                    new FixedSessionStore("session-id", CreateSession()));
                services.AddSingleton<IOptions<BffSessionOptions>>(Options.Create(new BffSessionOptions
                {
                    Enabled = true,
                    IdleTimeoutMinutes = 30,
                    AbsoluteTimeoutHours = 12
                }));
                services.AddHttpClient(BffProxyInfrastructure.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => gateway);
            });
        });
        using var client = configuredFactory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/bff/v1/document/files/11111111-1111-1111-1111-111111111111/download");
        request.Headers.TryAddWithoutValidation("Cookie", "__Host-pilot_session=session-id");
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(10, 13);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal("bytes 10-13/100", response.Content.Headers.ContentRange?.ToString());
        Assert.Contains("bytes", response.Headers.AcceptRanges);
        Assert.Equal([10, 11, 12, 13], await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("bytes=10-13", gateway.Range);
    }

    [Theory]
    [InlineData(false, HttpStatusCode.BadGateway)]
    [InlineData(true, HttpStatusCode.GatewayTimeout)]
    public async Task DownstreamFailure_ReturnsBoundedGatewayStatus(
        bool timeout,
        HttpStatusCode expectedStatus)
    {
        using var configuredFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Bff:Proxy:Enabled", "true");
            builder.UseSetting("Bff:Proxy:TimeoutSeconds", "1");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IBffSessionStore>();
                services.AddSingleton<IBffSessionStore>(
                    new FixedSessionStore("session-id", CreateSession()));
                services.AddSingleton<IOptions<BffSessionOptions>>(Options.Create(new BffSessionOptions
                {
                    Enabled = true,
                    IdleTimeoutMinutes = 30,
                    AbsoluteTimeoutHours = 12
                }));
                services.AddHttpClient(BffProxyInfrastructure.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => new FailingGatewayHandler(timeout));
            });
        });
        using var client = configuredFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/bff/v1/candidates/123");
        request.Headers.TryAddWithoutValidation("Cookie", "__Host-pilot_session=session-id");

        using var response = await client.SendAsync(request);

        Assert.Equal(expectedStatus, response.StatusCode);
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

    private sealed class FixedSessionStore(string sessionId, BffSessionRecord session) : IBffSessionStore
    {
        public bool Deleted { get; private set; }

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

        public Task DeleteAsync(string requestedSessionId, CancellationToken cancellationToken)
        {
            Deleted = true;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingGatewayHandler(
        HttpStatusCode statusCode = HttpStatusCode.OK) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Request = request;
            var response = new HttpResponseMessage(statusCode)
            {
                Content = new StringContent("gateway-ok")
            };
            response.Headers.TryAddWithoutValidation("Set-Cookie", "upstream=bad");
            response.Headers.TryAddWithoutValidation("Location", "http://internal-service/secret");
            response.Headers.TryAddWithoutValidation("X-Internal-Secret", "do-not-forward");
            return Task.FromResult(response);
        }
    }

    private sealed class RangeGatewayHandler : HttpMessageHandler
    {
        public string? Range { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Range = request.Headers.Range?.ToString();
            var content = new ByteArrayContent([10, 11, 12, 13]);
            content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(10, 13, 100);
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = content
            };
            response.Headers.AcceptRanges.Add("bytes");
            return Task.FromResult(response);
        }
    }

    private sealed class FailingGatewayHandler(bool timeout) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (!timeout)
            {
                throw new HttpRequestException("Synthetic downstream failure.");
            }

            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            throw new InvalidOperationException("The timeout cancellation was not observed.");
        }
    }

    private sealed class CaptureLogProvider : ILoggerProvider
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> messages = new();
        private readonly TaskCompletionSource performanceLog = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyCollection<string> Messages => messages.ToArray();

        public ILogger CreateLogger(string categoryName) => new CaptureLogger(
            categoryName,
            messages,
            performanceLog);

        public Task WaitForPerformanceLogAsync() =>
            performanceLog.Task.WaitAsync(TimeSpan.FromSeconds(5));

        public void Dispose()
        {
        }

        private sealed class CaptureLogger(
            string categoryName,
            System.Collections.Concurrent.ConcurrentQueue<string> messages,
            TaskCompletionSource performanceLog) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (!categoryName.Equals(
                        "Pilot.Observability.RequestPerformanceMiddleware",
                        StringComparison.Ordinal))
                {
                    return;
                }

                messages.Enqueue(formatter(state, exception));
                performanceLog.TrySetResult();
            }
        }
    }
}
