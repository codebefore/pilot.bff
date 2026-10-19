using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Pilot.Bff.Api.Contracts;
using Pilot.Bff.Api.Options;
using Pilot.Bff.Api.Session;

namespace Pilot.Bff.Api.Security;

public sealed class ReleaseGrantService(
    IBffSessionStore sessions,
    IOptions<ReleaseGrantOptions> options,
    TimeProvider timeProvider)
{
    public async Task<(HttpStatusCode StatusCode, ReleaseGrantResponse? Value)> IssueAsync(
        string? sessionId,
        CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled)
            return (HttpStatusCode.ServiceUnavailable, null);
        if (string.IsNullOrWhiteSpace(sessionId))
            return (HttpStatusCode.Unauthorized, null);

        var session = await sessions.GetAsync(sessionId, cancellationToken);
        if (session is null)
            return (HttpStatusCode.Unauthorized, null);
        if (!session.User.IsSuperAdmin)
            return (HttpStatusCode.Forbidden, null);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var expiresAtUtc = new[]
        {
            now.AddSeconds(options.Value.LifetimeSeconds),
            session.IdleExpiresAtUtc,
            session.AbsoluteExpiresAtUtc,
            session.RefreshTokenExpiresAtUtc
        }.Min();
        if (expiresAtUtc <= now)
            return (HttpStatusCode.Unauthorized, null);

        var userId = session.User.Id.ToString("D");
        var token = new JwtSecurityToken(
            issuer: options.Value.Issuer,
            audience: options.Value.Audience,
            claims:
            [
                new Claim(JwtRegisteredClaimNames.Sub, userId),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
                new Claim(ClaimTypes.NameIdentifier, userId),
                new Claim("is_super_admin", "true"),
                new Claim("purpose", "release-control")
            ],
            notBefore: now.AddSeconds(-5),
            expires: expiresAtUtc,
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.Value.SigningKey)),
                SecurityAlgorithms.HmacSha256));

        return (HttpStatusCode.OK, new ReleaseGrantResponse(
            new JwtSecurityTokenHandler().WriteToken(token), expiresAtUtc));
    }
}
