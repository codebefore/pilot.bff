using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;

namespace Pilot.Observability;

public sealed class DependencyTelemetry : IDisposable
{
    public static readonly ActivitySource Source = new("Pilot.Dependencies", "1.0.0");
    private static readonly Meter Meter = new("Pilot.Dependencies", "1.0.0");
    private static readonly Histogram<double> Duration =
        Meter.CreateHistogram<double>("pilot.dependency.duration", "s");
    private readonly ActivityListener listener;
    private readonly ILogger<DependencyTelemetry> logger;
    private readonly string service;
    private readonly double slowMs;

    public DependencyTelemetry(
        ILogger<DependencyTelemetry> logger,
        IConfiguration configuration)
    {
        this.logger = logger;
        service = configuration["Performance:ServiceName"] ?? "pilot-bff";
        slowMs = Math.Max(
            0,
            configuration.GetValue("Performance:SlowDependencyMs", 250d));
        listener = new ActivityListener
        {
            ShouldListenTo = source =>
                source.Name is "Pilot.Dependencies" or "System.Net.Http",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllData,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) =>
                ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                var http = activity.Source.Name == "System.Net.Http";
                Record(
                    http
                        ? "http.client"
                        : activity.GetTagItem("dependency.kind")?.ToString() ?? "unknown",
                    http
                        ? activity.GetTagItem("http.request.method")?.ToString() ?? "HTTP"
                        : activity.OperationName,
                    activity.Duration,
                    activity.Status == ActivityStatusCode.Error ? "error" : "completed",
                    activity.GetTagItem("error.type")?.ToString(),
                    activity,
                    http ? activity.GetTagItem("server.address")?.ToString() : null,
                    http ? activity.GetTagItem("http.response.status_code") : null);
            }
        };
        ActivitySource.AddActivityListener(listener);
    }

    public void Record(
        string kind,
        string operation,
        TimeSpan elapsed,
        string outcome = "completed",
        string? errorType = null,
        Activity? activity = null,
        string? destination = null,
        object? status = null)
    {
        activity ??= Activity.Current;
        var milliseconds = elapsed.TotalMilliseconds;
        Duration.Record(elapsed.TotalSeconds, new TagList
        {
            { "service", service },
            { "dependency.kind", kind },
            { "operation", operation },
            { "outcome", outcome }
        });
        if (milliseconds < slowMs && outcome == "completed")
        {
            return;
        }

        var payload = JsonSerializer.Serialize(new
        {
            service,
            kind,
            operation,
            duration_ms = Math.Round(milliseconds, 3),
            outcome,
            error_type = errorType,
            trace_id = activity?.TraceId.ToString(),
            span_id = activity?.SpanId.ToString(),
            parent_span_id = activity?.ParentSpanId.ToString(),
            destination,
            status_code = status
        });
        logger.Log(
            outcome != "completed" || milliseconds >= slowMs
                ? LogLevel.Warning
                : LogLevel.Information,
            "dependency_performance {Performance}",
            payload);
    }

    public void Dispose() => listener.Dispose();
}
