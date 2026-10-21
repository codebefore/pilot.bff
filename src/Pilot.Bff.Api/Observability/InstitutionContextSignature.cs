using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Pilot.Observability;

public static class InstitutionContextSignature
{
    public const string TimestampHeader = "X-Pilot-Institution-Context-Timestamp";
    public const string SignatureHeader = "X-Pilot-Institution-Context-Signature";

    public static (string Timestamp, string Signature)? Create(Guid? institutionId, string? signingKey)
    {
        if (institutionId is not { } value || value == Guid.Empty ||
            string.IsNullOrWhiteSpace(signingKey) || signingKey.Length < 32 ||
            Activity.Current is not { } activity)
        {
            return null;
        }

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var payload = Payload(value, timestamp, activity.TraceId.ToString());
        var signature = Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(signingKey),
            Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
        return (timestamp, signature);
    }

    internal static string Payload(Guid institutionId, string timestamp, string traceId) =>
        $"{institutionId:D}\n{timestamp}\n{traceId}";
}
