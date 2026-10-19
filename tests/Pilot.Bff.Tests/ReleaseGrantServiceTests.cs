using System.IdentityModel.Tokens.Jwt;
using System.Net;
using Microsoft.Extensions.Options;
using Pilot.Bff.Api.Contracts;
using Pilot.Bff.Api.Options;
using Pilot.Bff.Api.Security;
using Pilot.Bff.Api.Session;

namespace Pilot.Bff.Tests;

public sealed class ReleaseGrantServiceTests
{
    private const string SigningKey = "release-grant-tests-only-signing-key-123456789";

    [Fact]
    public async Task SuperAdminReceivesShortLivedReleaseOnlyGrant()
    {
        var now = DateTime.UtcNow;
        var userId = Guid.NewGuid();
        var service = Create(new BffSessionRecord(
            "access", now.AddMinutes(5), "refresh", now.AddHours(1),
            new BffAuthUser(userId, "Admin", null, "Admin", true), [], null,
            now.AddMinutes(-1), now, now.AddMinutes(30), now.AddHours(1)));

        var result = await service.IssueAsync("session", default);

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.NotNull(result.Value);
        var token = new JwtSecurityTokenHandler().ReadJwtToken(result.Value.Token);
        Assert.Equal("pilot-bff", token.Issuer);
        Assert.Contains("pilot-release-control", token.Audiences);
        Assert.Equal(userId.ToString("D"), token.Subject);
        Assert.Equal("true", token.Claims.Single(claim => claim.Type == "is_super_admin").Value);
        Assert.Equal("release-control", token.Claims.Single(claim => claim.Type == "purpose").Value);
        Assert.InRange(result.Value.ExpiresAtUtc, now.AddSeconds(50), now.AddSeconds(70));
    }

    [Fact]
    public async Task MissingSessionAndOrdinaryUserCannotReceiveGrant()
    {
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await Create(null).IssueAsync("missing", default)).StatusCode);

        var now = DateTime.UtcNow;
        var ordinary = new BffSessionRecord(
            "access", now.AddMinutes(5), "refresh", now.AddHours(1),
            new BffAuthUser(Guid.NewGuid(), "User", null, "User", false), [], null,
            now.AddMinutes(-1), now, now.AddMinutes(30), now.AddHours(1));
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Create(ordinary).IssueAsync("session", default)).StatusCode);
    }

    private static ReleaseGrantService Create(BffSessionRecord? session) => new(
        new FixedSessionStore(session),
        Options.Create(new ReleaseGrantOptions
        {
            Enabled = true,
            SigningKey = SigningKey,
            Issuer = "pilot-bff",
            Audience = "pilot-release-control",
            LifetimeSeconds = 60
        }),
        TimeProvider.System);

    private sealed class FixedSessionStore(BffSessionRecord? session) : IBffSessionStore
    {
        public Task<BffSessionRecord?> GetAsync(string sessionId, CancellationToken cancellationToken) =>
            Task.FromResult(sessionId == "session" ? session : null);
        public Task SetAsync(string sessionId, BffSessionRecord value, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<bool> TryUpdateAsync(string sessionId, BffSessionRecord value, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<bool> RotateAsync(string currentSessionId, string replacementSessionId, BffSessionRecord value, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task DeleteAsync(string sessionId, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
