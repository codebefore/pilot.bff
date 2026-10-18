using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.AspNetCore.Routing;

namespace Pilot.Observability;

public sealed class RequestPerformanceMiddleware(
    RequestDelegate next,
    ILogger<RequestPerformanceMiddleware> logger,
    IConfiguration configuration)
{
    private static readonly Meter Meter = new("Pilot.HttpPerformance", "1.0.0");
    private static readonly Histogram<double> Duration =
        Meter.CreateHistogram<double>("pilot.http.response.duration", "s");
    private readonly string service = configuration["Performance:ServiceName"] ?? "pilot-bff";
    private readonly double slowMs = Math.Max(
        0,
        configuration.GetValue("Performance:SlowRequestMs", 1000d));

    public async Task InvokeAsync(HttpContext context)
    {
        var started = Stopwatch.GetTimestamp();
        using var fallback = Activity.Current is null ? StartFallbackActivity(context) : null;
        var traceId = Activity.Current!.TraceId.ToString();
        var spanId = Activity.Current.SpanId.ToString();
        double? headersMs = null;
        double? appMs = null;
        var emitted = 0;
        var method = context.Request.Method;
        string Route() =>
            (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "unmatched";
        var health = context.Request.Path.StartsWithSegments("/health");
        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["TraceId"] = traceId,
            ["SpanId"] = spanId
        });

        void Emit(string outcome, int status, string? errorType = null)
        {
            if (Interlocked.Exchange(ref emitted, 1) != 0)
            {
                return;
            }

            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var route = Route();
            Duration.Record(elapsed / 1000, new TagList
            {
                { "service", service },
                { "http.route", route },
                { "http.request.method", method },
                { "http.response.status_code", status },
                { "outcome", outcome }
            });
            if (health && elapsed < slowMs && status < 400 && outcome == "completed")
            {
                return;
            }

            var payload = JsonSerializer.Serialize(new
            {
                service,
                trace_id = traceId,
                span_id = spanId,
                method,
                route,
                status_code = status,
                outcome,
                duration_ms = Math.Round(elapsed, 3),
                app_duration_ms = appMs,
                response_headers_ms = headersMs,
                error_type = errorType,
                stages_ms = context.Items.TryGetValue(
                    "Pilot.Performance.Stages",
                    out var stages)
                        ? stages
                        : null
            });
            logger.Log(
                elapsed >= slowMs || status >= 500 || outcome != "completed"
                    ? LogLevel.Warning
                    : LogLevel.Information,
                "http_performance {Performance}",
                payload);
        }

        context.Response.OnStarting(() =>
        {
            headersMs = Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds, 3);
            context.Response.Headers["X-Pilot-Trace-Id"] = traceId;
            return Task.CompletedTask;
        });
        context.Response.OnCompleted(() =>
        {
            Emit(
                context.RequestAborted.IsCancellationRequested ? "aborted" : "completed",
                context.Response.StatusCode);
            return Task.CompletedTask;
        });

        try
        {
            await next(context);
            appMs = Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds, 3);
            if (context.RequestAborted.IsCancellationRequested)
            {
                Emit("aborted", context.Response.StatusCode);
            }
        }
        catch (Exception exception)
        {
            appMs = Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds, 3);
            var aborted = context.RequestAborted.IsCancellationRequested;
            Emit(
                aborted ? "aborted" : "exception",
                aborted || context.Response.HasStarted ? context.Response.StatusCode : 500,
                exception.GetType().Name);
            throw;
        }
    }

    private static Activity StartFallbackActivity(HttpContext context)
    {
        var activity = new Activity("Pilot.HttpRequest").SetIdFormat(ActivityIdFormat.W3C);
        if (ActivityContext.TryParse(
                context.Request.Headers.TraceParent.ToString(),
                context.Request.Headers.TraceState.ToString(),
                out var parent))
        {
            activity.SetParentId(parent.TraceId, parent.SpanId, parent.TraceFlags);
        }

        return activity.Start();
    }
}
