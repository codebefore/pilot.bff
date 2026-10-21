using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Pilot.Observability;

namespace Pilot.Bff.Tests;

public sealed class InstitutionTelemetryTests
{
    [Fact]
    public void SignedGatewayContextBindsInstitutionTimestampAndTrace()
    {
        const string signingKey = "test-institution-context-signing-key-1234567890";
        var institutionId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        using var activity = new Activity("request").SetIdFormat(ActivityIdFormat.W3C).Start();

        var signed = InstitutionContextSignature.Create(institutionId, signingKey);
        Assert.NotNull(signed);
        var payload = $"{institutionId:D}\n{signed.Value.Timestamp}\n{activity.TraceId}";
        var expected = Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(signingKey),
            Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();

        Assert.Equal(expected, signed.Value.Signature);
    }

    [Fact]
    public void DependencyLogIncludesNormalizedInstitutionFromParentActivityTag()
    {
        var logger = new CaptureLogger();
        using var telemetry = new DependencyTelemetry(logger, new ConfigurationBuilder().Build());
        using var activity = new Activity("request").SetIdFormat(ActivityIdFormat.W3C).Start();
        const string institutionId = "11111111-2222-3333-4444-555555555555";
        activity.SetTag("pilot.institution_id", institutionId.ToUpperInvariant());

        telemetry.Record(
            "http.client",
            "GET",
            TimeSpan.FromMilliseconds(1),
            outcome: "error",
            errorType: "SyntheticError",
            activity: activity,
            destination: "pilot-gateway",
            status: 500);

        var line = Assert.Single(logger.Messages);
        using var json = JsonDocument.Parse(line.Split("dependency_performance ")[1]);
        Assert.Equal(institutionId, json.RootElement.GetProperty("institution_id").GetString());
    }

    private sealed class CaptureLogger : ILogger<DependencyTelemetry>
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Enqueue(formatter(state, exception));
    }
}
