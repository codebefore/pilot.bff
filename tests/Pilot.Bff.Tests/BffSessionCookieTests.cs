using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Pilot.Bff.Api.Options;
using Pilot.Bff.Api.Security;

namespace Pilot.Bff.Tests;

public sealed class BffSessionCookieTests
{
    [Fact]
    public void SessionCookie_UsesHostOnlySecurePolicy()
    {
        var context = new DefaultHttpContext();
        var cookie = new BffSessionCookie(Options.Create(new BffSessionOptions()));

        cookie.Append(context.Response, "opaque-session-id", DateTime.UtcNow.AddMinutes(30));

        var header = context.Response.Headers.SetCookie.ToString();
        Assert.False(string.IsNullOrWhiteSpace(header));
        Assert.StartsWith("__Host-pilot_session=opaque-session-id", header, StringComparison.Ordinal);
        Assert.Contains("path=/", header, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", header, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", header, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", header, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("domain=", header, StringComparison.OrdinalIgnoreCase);
    }
}
