using Microsoft.Extensions.Options;
using Pilot.Bff.Api.Options;

namespace Pilot.Bff.Api.Security;

public sealed class BffSessionCookie(IOptions<BffSessionOptions> options)
{
    public string? Read(HttpRequest request)
    {
        return request.Cookies[options.Value.CookieName];
    }

    public void Append(HttpResponse response, string sessionId, DateTime expiresAtUtc)
    {
        response.Cookies.Append(
            options.Value.CookieName,
            sessionId,
            CreateOptions(expiresAtUtc));
    }

    public void Delete(HttpResponse response)
    {
        response.Cookies.Delete(options.Value.CookieName, CreateOptions(DateTime.UnixEpoch));
    }

    private static CookieOptions CreateOptions(DateTime expiresAtUtc)
    {
        return new CookieOptions
        {
            Secure = true,
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            IsEssential = true,
            Expires = new DateTimeOffset(expiresAtUtc, TimeSpan.Zero)
        };
    }
}
