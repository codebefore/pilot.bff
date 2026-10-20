using System.Security.Cryptography;
using System.Text;
using System.Net;
using Microsoft.Extensions.Options;
using Pilot.Bff.Api.Options;

namespace Pilot.Bff.Api.Session;

public interface IBffSessionKeyBuilder
{
    string DataProtectionKeys { get; }

    string Session(string sessionId);

    string RefreshLock(string sessionId);

    string ProxyRate(
        Guid userId,
        Guid? institutionId,
        IPAddress? clientIpAddress,
        bool mutation);

    string ProxyConcurrency(bool mutation);

    string ProxyFileTransferConcurrency();
}

public sealed class BffSessionKeyBuilder(IOptions<BffRedisOptions> options) : IBffSessionKeyBuilder
{
    public string DataProtectionKeys => $"{Prefix()}:dataprotection-keys";

    public string Session(string sessionId)
    {
        return SessionScopedKey(sessionId, "session");
    }

    public string RefreshLock(string sessionId)
    {
        return SessionScopedKey(sessionId, "refresh-lock");
    }

    public string ProxyRate(
        Guid userId,
        Guid? institutionId,
        IPAddress? clientIpAddress,
        bool mutation)
    {
        var partition = string.Join(
            ':',
            userId.ToString("D"),
            institutionId?.ToString("D") ?? "none",
            clientIpAddress?.ToString() ?? "unknown",
            mutation ? "mutation" : "read");
        return $"{Prefix()}:{{{TrafficHashSlot()}}}:proxy-rate:{Hash(partition)}";
    }

    public string ProxyConcurrency(bool mutation) =>
        $"{Prefix()}:{{{TrafficHashSlot()}}}:proxy-concurrency:{(mutation ? "mutation" : "read")}";

    public string ProxyFileTransferConcurrency() =>
        $"{Prefix()}:{{{TrafficHashSlot()}}}:proxy-concurrency:file-transfer";

    private string SessionScopedKey(string sessionId, string purpose)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return $"{Prefix()}:{{{SessionHashSlot()}}}:{purpose}:{Hash(sessionId)}";
    }

    private string Prefix()
    {
        return $"{Sanitize(options.Value.InstanceName)}:{Sanitize(options.Value.EnvironmentName)}:bff";
    }

    private string SessionHashSlot()
    {
        return $"{Sanitize(options.Value.InstanceName)}-{Sanitize(options.Value.EnvironmentName)}-bff-sessions";
    }

    private string TrafficHashSlot()
    {
        return $"{Sanitize(options.Value.InstanceName)}-{Sanitize(options.Value.EnvironmentName)}-bff-traffic";
    }

    private string Hash(string value)
    {
        var pepper = options.Value.KeyPepper
            ?? throw new InvalidOperationException("Redis:KeyPepper is required for BFF sessions.");
        var hash = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(pepper),
            Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string Sanitize(string? value)
    {
        var source = string.IsNullOrWhiteSpace(value) ? "pilot" : value.Trim().ToLowerInvariant();
        return new string(source.Select(character => char.IsLetterOrDigit(character) ? character : '-').ToArray());
    }
}
