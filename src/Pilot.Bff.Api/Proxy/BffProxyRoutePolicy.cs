namespace Pilot.Bff.Api.Proxy;

public interface IBffProxyRoutePolicy
{
    bool TryResolve(string method, string service, string? path, out string targetPath);
}

public sealed class BffProxyRoutePolicy : IBffProxyRoutePolicy
{
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
        if (segments.Any(segment =>
                string.IsNullOrWhiteSpace(segment) ||
                ForbiddenSegments.Contains(segment) ||
                segment.Any(char.IsControl)))
        {
            return false;
        }

        if (!IsAllowedServicePath(service, segments))
        {
            return false;
        }

        var encodedPath = string.Join('/', segments.Select(Uri.EscapeDataString));
        targetPath = encodedPath.Length == 0
            ? $"/v1/{service}"
            : $"/v1/{service}/{encodedPath}";
        return true;
    }

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
