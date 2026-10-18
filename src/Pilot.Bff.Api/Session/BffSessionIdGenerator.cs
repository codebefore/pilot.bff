using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;

namespace Pilot.Bff.Api.Session;

public interface IBffSessionIdGenerator
{
    string Create();
}

public sealed class BffSessionIdGenerator : IBffSessionIdGenerator
{
    private const int SessionIdByteLength = 32;

    public string Create()
    {
        return WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(SessionIdByteLength));
    }
}
