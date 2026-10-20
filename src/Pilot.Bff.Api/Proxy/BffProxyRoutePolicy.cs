namespace Pilot.Bff.Api.Proxy;

public interface IBffProxyRoutePolicy
{
    bool TryResolve(string method, string service, string? path, out string targetPath);
    BffProxyTrafficKind ClassifyTraffic(string method, string targetPath);
}

public sealed class BffProxyRoutePolicy : IBffProxyRoutePolicy
{
    private static readonly HashSet<string> ReadOnlyPostPaths =
        new(StringComparer.Ordinal)
        {
            "/v1/document/documents/candidate-photos",
            "/v1/training/practice-candidates/lesson-summaries/query"
        };

    private static readonly HashSet<string> AllowedMethods =
        new(StringComparer.OrdinalIgnoreCase) { "GET", "HEAD", "POST", "PUT", "PATCH", "DELETE" };

    private static readonly HashSet<string> ServicesWithApplicationApiRoot =
        new(StringComparer.Ordinal)
        {
            "candidates", "catalog", "document", "finance", "training", "mebbis"
        };

    private static readonly HashSet<string> PlatformRoots =
        new(StringComparer.Ordinal)
        {
            "institution-settings", "stats", "notifications", "outbox", "inbox",
            "system-health", "institution-backups", "institutions", "browser-performance",
            "releases", "grafana", "local-agent"
        };

    private static readonly HashSet<string> IdentityRoots =
        new(StringComparer.Ordinal)
        {
            "users", "roles", "role-permissions", "user-notes", "institutions", "contracts"
        };

    private static readonly HashSet<string> IdentityAuthRoots =
        new(StringComparer.Ordinal) { "sessions", "account-deletion" };

    private static readonly HashSet<string> MessagingRoots =
        new(StringComparer.Ordinal) { "whatsapp-chat" };

    private static readonly HashSet<string> ForbiddenSegments =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".", "..", "internal", "health", "metrics", "swagger", "openapi",
            "service-info", "gateway"
        };

    public bool TryResolve(string method, string service, string? path, out string targetPath)
    {
        targetPath = string.Empty;
        if (!AllowedMethods.Contains(method) ||
            string.IsNullOrWhiteSpace(service) ||
            service != service.ToLowerInvariant() ||
            path?.Length > 2048 ||
            path?.Contains('%', StringComparison.Ordinal) == true ||
            path?.Contains('\\', StringComparison.Ordinal) == true)
        {
            return false;
        }

        var segments = (path ?? string.Empty)
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var explicitlyAllowedOperationalRead = IsExplicitlyAllowedOperationalRead(
            method,
            service,
            segments);
        if (segments.Any(segment =>
                string.IsNullOrWhiteSpace(segment) ||
                ForbiddenSegments.Contains(segment) ||
                segment.Any(char.IsControl)) &&
            !explicitlyAllowedOperationalRead)
        {
            return false;
        }

        if (!explicitlyAllowedOperationalRead && !IsAllowedServicePath(service, segments))
        {
            return false;
        }

        var encodedPath = string.Join('/', segments.Select(Uri.EscapeDataString));
        targetPath = encodedPath.Length == 0
            ? $"/v1/{service}"
            : $"/v1/{service}/{encodedPath}";
        return true;
    }

    public BffProxyTrafficKind ClassifyTraffic(string method, string targetPath)
    {
        if (IsFileTransfer(method, targetPath))
        {
            return BffProxyTrafficKind.FileTransfer;
        }

        return !HttpMethods.IsGet(method) &&
               !HttpMethods.IsHead(method) &&
               !(HttpMethods.IsPost(method) && ReadOnlyPostPaths.Contains(targetPath))
            ? BffProxyTrafficKind.Mutation
            : BffProxyTrafficKind.Read;
    }

    private static bool IsFileTransfer(string method, string targetPath)
    {
        if (!HttpMethods.IsGet(method) && !HttpMethods.IsHead(method))
        {
            return false;
        }

        var segments = targetPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments switch
        {
            ["v1", "document", "candidates", var candidateId, "documents", var documentId, "download"]
                when AreGuids(candidateId, documentId) => true,
            ["v1", "document", "vehicles", var vehicleId, "documents", var documentId, "download"]
                when AreGuids(vehicleId, documentId) => true,
            ["v1", "document", "instructors", var instructorId, "assignments", var assignmentId,
                "documents", var documentId, "file"]
                when AreGuids(instructorId, assignmentId, documentId) => true,
            ["v1", "document", "correspondence", var correspondenceId, "files", var fileId, "download"]
                when AreGuids(correspondenceId, fileId) => true,
            ["v1", "document", "correspondence", var correspondenceId, "packet-pdf"]
                when AreGuids(correspondenceId) => true,
            ["v1", "document", "legislation", "documents", var legislationId, "files", var fileId, "download"]
                when AreGuids(legislationId, fileId) => true,
            ["v1", "training", "instructors", var instructorId, "photo"]
                when AreGuids(instructorId) => true,
            ["v1", "platform", "institution-settings", "logo", "file"] => true,
            ["v1", "platform", "institution-backups", var backupId, "download"]
                when AreGuids(backupId) => true,
            _ => false
        };
    }

    private static bool AreGuids(params string[] values) =>
        values.All(value => Guid.TryParse(value, out var parsed) && parsed != Guid.Empty);

    private static bool IsExplicitlyAllowedOperationalRead(
        string method,
        string service,
        string[] segments) =>
        (string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase)) &&
        service == "platform" &&
        segments is ["maintenance"] or ["outbox", "health"] or ["health", "domain-events"];

    private static bool IsAllowedServicePath(string service, string[] segments)
    {
        if (ServicesWithApplicationApiRoot.Contains(service))
        {
            return true;
        }

        if (segments.Length == 0)
        {
            return false;
        }

        return service switch
        {
            "platform" => PlatformRoots.Contains(segments[0]),
            "messaging" => MessagingRoots.Contains(segments[0]) ||
                IsMessagingHubPath(segments),
            "identity" => IdentityRoots.Contains(segments[0]) ||
                segments.Length >= 2 &&
                segments[0] == "auth" &&
                IdentityAuthRoots.Contains(segments[1]),
            _ => false
        };
    }

    private static bool IsMessagingHubPath(string[] segments) =>
        segments is ["hubs", "messaging"] or ["hubs", "messaging", "negotiate"];
}
