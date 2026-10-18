using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace Pilot.Bff.Api.Session;

public interface IBffSessionProtector
{
    string Protect(BffSessionRecord session);

    BffSessionRecord? Unprotect(string protectedPayload);
}

public sealed class BffSessionProtector(IDataProtectionProvider provider) : IBffSessionProtector
{
    private const string Purpose = "Pilot.Bff.SessionPayload.v1";
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly IDataProtector protector = provider.CreateProtector(Purpose);

    public string Protect(BffSessionRecord session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return protector.Protect(JsonSerializer.Serialize(session, SerializerOptions));
    }

    public BffSessionRecord? Unprotect(string protectedPayload)
    {
        if (string.IsNullOrWhiteSpace(protectedPayload))
        {
            return null;
        }

        try
        {
            var json = protector.Unprotect(protectedPayload);
            return JsonSerializer.Deserialize<BffSessionRecord>(json, SerializerOptions);
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException)
        {
            return null;
        }
    }
}
