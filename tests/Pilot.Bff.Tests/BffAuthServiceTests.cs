using System.Net;
using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Pilot.Bff.Api.Auth;
using Pilot.Bff.Api.Contracts;
using Pilot.Bff.Api.Options;
using Pilot.Bff.Api.Session;

namespace Pilot.Bff.Tests;

public sealed class BffAuthServiceTests
{
    [Fact]
    public async Task VerifyLogin_StoresTokensServerSide_AndReturnsTokenlessSession()
    {
        var identity = new FakeIdentityAuthClient { Login = CreateLoginResponse() };
        var store = new InMemorySessionStore();
        var service = CreateService(identity, store);

        var result = await service.VerifyLoginCodeAsync(
            new VerifyLoginOtpRequest("905001112233", "123456"),
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal("session-id-1", result.SessionId);
        Assert.NotNull(result.Value);
        Assert.Equal(identity.Login.User, result.Value.User);
        Assert.DoesNotContain(
            identity.Login.AccessToken,
            System.Text.Json.JsonSerializer.Serialize(result.Value),
            StringComparison.Ordinal);
        var stored = await store.GetAsync(result.SessionId!, CancellationToken.None);
        Assert.Equal(identity.Login.AccessToken, stored?.AccessToken);
        Assert.Equal(identity.Login.RefreshToken, stored?.RefreshToken);
    }

    [Fact]
    public async Task SelectInstitution_RotatesBrowserSessionId()
    {
        var identity = new FakeIdentityAuthClient { Login = CreateLoginResponse() };
        var store = new InMemorySessionStore();
        var service = CreateService(identity, store);
        var login = await service.VerifyLoginCodeAsync(
            new VerifyLoginOtpRequest("905001112233", "123456"),
            CancellationToken.None);
        var target = Guid.NewGuid();
        identity.Login = CreateLoginResponse(target);

        var result = await service.SelectInstitutionAsync(
            login.SessionId,
            target,
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal("session-id-2", result.SessionId);
        Assert.Null(await store.GetAsync("session-id-1", CancellationToken.None));
        Assert.NotNull(await store.GetAsync("session-id-2", CancellationToken.None));
        Assert.Equal(target, result.Value?.ActiveInstitution?.Id);
    }

    [Fact]
    public async Task VerifyLogin_RevokesIdentityToken_WhenRedisWriteFails()
    {
        var identity = new FakeIdentityAuthClient { Login = CreateLoginResponse() };
        var store = new InMemorySessionStore { FailWrites = true };
        var service = CreateService(identity, store);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.VerifyLoginCodeAsync(
            new VerifyLoginOtpRequest("905001112233", "123456"),
            CancellationToken.None));

        Assert.Equal(identity.Login.RefreshToken, identity.LoggedOutRefreshToken);
    }

    [Fact]
    public async Task Logout_RevokesIdentityToken_AndDeletesLocalSession()
    {
        var identity = new FakeIdentityAuthClient { Login = CreateLoginResponse() };
        var store = new InMemorySessionStore();
        var service = CreateService(identity, store);
        var login = await service.VerifyLoginCodeAsync(
            new VerifyLoginOtpRequest("905001112233", "123456"),
            CancellationToken.None);

        await service.LogoutAsync(login.SessionId, CancellationToken.None);

        Assert.Equal(identity.Login.RefreshToken, identity.LoggedOutRefreshToken);
        Assert.Null(await store.GetAsync(login.SessionId!, CancellationToken.None));
    }

    [Fact]
    public async Task ConcurrentSessionReads_RefreshIdentityTokenOnlyOnce()
    {
        var identity = new FakeIdentityAuthClient
        {
            Login = CreateLoginResponse(accessTokenLifetime: TimeSpan.FromSeconds(30)),
            RefreshLogin = CreateLoginResponse(tokenSuffix: "refreshed")
        };
        identity.BlockRefresh();
        var store = new InMemorySessionStore();
        var service = CreateService(identity, store);
        var login = await service.VerifyLoginCodeAsync(
            new VerifyLoginOtpRequest("905001112233", "123456"),
            CancellationToken.None);

        var first = service.GetSessionAsync(login.SessionId, CancellationToken.None);
        await identity.RefreshEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = service.GetSessionAsync(login.SessionId, CancellationToken.None);
        identity.ReleaseRefresh();
        var results = await Task.WhenAll(first, second);

        Assert.All(results, result => Assert.Equal(HttpStatusCode.OK, result.StatusCode));
        Assert.Equal(1, identity.RefreshCount);
        var stored = await store.GetAsync(login.SessionId!, CancellationToken.None);
        Assert.Equal(identity.RefreshLogin.RefreshToken, stored?.RefreshToken);
    }

    [Fact]
    public async Task Refresh_DoesNotResurrectSessionDeletedBeforeAtomicUpdate()
    {
        var identity = new FakeIdentityAuthClient
        {
            Login = CreateLoginResponse(accessTokenLifetime: TimeSpan.FromSeconds(30)),
            RefreshLogin = CreateLoginResponse(tokenSuffix: "replacement")
        };
        var store = new InMemorySessionStore();
        var service = CreateService(identity, store);
        var login = await service.VerifyLoginCodeAsync(
            new VerifyLoginOtpRequest("905001112233", "123456"),
            CancellationToken.None);
        store.DeleteBeforeNextUpdate = true;

        var result = await service.GetSessionAsync(login.SessionId, CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, result.StatusCode);
        Assert.Null(await store.GetAsync(login.SessionId!, CancellationToken.None));
        Assert.Contains(identity.RefreshLogin.RefreshToken, identity.LoggedOutRefreshTokens);
    }

    [Fact]
    public async Task SessionRead_ExtendsIdleExpiryWithoutPassingAbsoluteExpiry()
    {
        var identity = new FakeIdentityAuthClient { Login = CreateLoginResponse() };
        var store = new InMemorySessionStore();
        var service = CreateService(identity, store);
        var login = await service.VerifyLoginCodeAsync(
            new VerifyLoginOtpRequest("905001112233", "123456"),
            CancellationToken.None);
        var current = (await store.GetAsync(login.SessionId!, CancellationToken.None))!;
        var absoluteExpiry = DateTime.UtcNow.AddMinutes(10);
        await store.SetAsync(
            login.SessionId!,
            current with
            {
                LastActivityAtUtc = DateTime.UtcNow.AddMinutes(-10),
                IdleExpiresAtUtc = DateTime.UtcNow.AddMinutes(2),
                AbsoluteExpiresAtUtc = absoluteExpiry
            },
            CancellationToken.None);

        var result = await service.GetSessionAsync(login.SessionId, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal(absoluteExpiry, result.Value?.IdleExpiresAtUtc);
        Assert.Equal(0, identity.RefreshCount);
    }

    [Fact]
    public async Task RefreshLockTimeout_ReturnsServiceUnavailableWithoutCallingIdentity()
    {
        var identity = new FakeIdentityAuthClient
        {
            Login = CreateLoginResponse(accessTokenLifetime: TimeSpan.FromSeconds(30))
        };
        var store = new InMemorySessionStore();
        var sessionLock = new InMemorySessionLock { FailAcquire = true };
        var service = CreateService(identity, store, sessionLock);
        var login = await service.VerifyLoginCodeAsync(
            new VerifyLoginOtpRequest("905001112233", "123456"),
            CancellationToken.None);

        var result = await service.GetSessionAsync(login.SessionId, CancellationToken.None);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, result.StatusCode);
        Assert.Equal(0, identity.RefreshCount);
        Assert.NotNull(await store.GetAsync(login.SessionId!, CancellationToken.None));
    }

    [Fact]
    public async Task RejectedRefresh_DeletesLocalSession()
    {
        var identity = new FakeIdentityAuthClient
        {
            Login = CreateLoginResponse(accessTokenLifetime: TimeSpan.FromSeconds(30)),
            RefreshStatusCode = HttpStatusCode.Unauthorized
        };
        var store = new InMemorySessionStore();
        var service = CreateService(identity, store);
        var login = await service.VerifyLoginCodeAsync(
            new VerifyLoginOtpRequest("905001112233", "123456"),
            CancellationToken.None);

        var result = await service.GetSessionAsync(login.SessionId, CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, result.StatusCode);
        Assert.Null(await store.GetAsync(login.SessionId!, CancellationToken.None));
    }

    [Fact]
    public async Task MigrationApproval_StoresTokenServerSide_AndReturnsOnlyExpiry()
    {
        var identity = new FakeIdentityAuthClient { Login = CreateLoginResponse() };
        var store = new InMemorySessionStore();
        var service = CreateService(identity, store);
        var login = await service.VerifyLoginCodeAsync(
            new VerifyLoginOtpRequest("905001112233", "123456"),
            CancellationToken.None);

        var result = await service.VerifyMigrationAccessCodeAsync(
            login.SessionId,
            new VerifyMigrationAccessCodeRequest("654321"),
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal(identity.MigrationAccess.ExpiresAtUtc, result.Value?.ExpiresAtUtc);
        Assert.DoesNotContain(
            identity.MigrationAccess.AccessToken,
            System.Text.Json.JsonSerializer.Serialize(result.Value),
            StringComparison.Ordinal);
        var stored = await store.GetAsync(login.SessionId!, CancellationToken.None);
        Assert.Equal(identity.MigrationAccess.AccessToken, stored?.MigrationAccessToken);
        Assert.Equal(stored?.ActiveInstitution?.Id, stored?.MigrationAccessInstitutionId);
        var authorization = await service.GetProxyAuthorizationAsync(
            login.SessionId,
            CancellationToken.None);
        Assert.Equal(identity.MigrationAccess.AccessToken, authorization.Value?.MigrationAccessToken);
    }

    [Fact]
    public async Task LogoutRacingRefresh_RevokesReplacementAndDeletesSession()
    {
        var identity = new FakeIdentityAuthClient
        {
            Login = CreateLoginResponse(accessTokenLifetime: TimeSpan.FromSeconds(30)),
            RefreshLogin = CreateLoginResponse(tokenSuffix: "replacement")
        };
        identity.BlockRefresh();
        var store = new InMemorySessionStore();
        var service = CreateService(identity, store);
        var login = await service.VerifyLoginCodeAsync(
            new VerifyLoginOtpRequest("905001112233", "123456"),
            CancellationToken.None);

        var refresh = service.GetSessionAsync(login.SessionId, CancellationToken.None);
        await identity.RefreshEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var logout = service.LogoutAsync(login.SessionId, CancellationToken.None);
        identity.ReleaseRefresh();
        await Task.WhenAll(refresh, logout);

        Assert.Contains(identity.RefreshLogin.RefreshToken, identity.LoggedOutRefreshTokens);
        Assert.Null(await store.GetAsync(login.SessionId!, CancellationToken.None));
    }

    private static BffAuthService CreateService(
        FakeIdentityAuthClient identity,
        InMemorySessionStore store,
        InMemorySessionLock? sessionLock = null)
    {
        return new BffAuthService(
            identity,
            store,
            sessionLock ?? new InMemorySessionLock(),
            new SequentialSessionIdGenerator(),
            Options.Create(new BffSessionOptions
            {
                Enabled = true,
                IdleTimeoutMinutes = 30,
                AbsoluteTimeoutHours = 12
            }),
            TimeProvider.System,
            new HttpContextAccessor { HttpContext = new DefaultHttpContext() });
    }

    private static IdentityLoginResponse CreateLoginResponse(
        Guid? activeInstitutionId = null,
        TimeSpan? accessTokenLifetime = null,
        string tokenSuffix = "secret")
    {
        var now = DateTime.UtcNow;
        var institution = new BffAuthInstitution(
            activeInstitutionId ?? Guid.NewGuid(),
            "Test Institution",
            "Admin",
            true,
            new Dictionary<string, string> { ["candidates"] = "manage" });
        return new IdentityLoginResponse(
            $"identity-access-token-{tokenSuffix}",
            now.Add(accessTokenLifetime ?? TimeSpan.FromHours(1)),
            $"identity-refresh-token-{tokenSuffix}",
            now.AddDays(14),
            new BffAuthUser(Guid.NewGuid(), "Test User", "905001112233", "Admin", false),
            [institution],
            institution);
    }

    private sealed class SequentialSessionIdGenerator : IBffSessionIdGenerator
    {
        private int value;

        public string Create() => $"session-id-{Interlocked.Increment(ref value)}";
    }

    private sealed class InMemorySessionStore : IBffSessionStore
    {
        private readonly ConcurrentDictionary<string, BffSessionRecord> sessions = [];

        public bool FailWrites { get; init; }

        public bool DeleteBeforeNextUpdate { get; set; }

        public Task SetAsync(
            string sessionId,
            BffSessionRecord session,
            CancellationToken cancellationToken)
        {
            if (FailWrites)
            {
                throw new InvalidOperationException("Redis write failed.");
            }

            sessions[sessionId] = session;
            return Task.CompletedTask;
        }

        public Task<BffSessionRecord?> GetAsync(
            string sessionId,
            CancellationToken cancellationToken)
        {
            sessions.TryGetValue(sessionId, out var session);
            return Task.FromResult(session);
        }

        public Task<bool> RotateAsync(
            string currentSessionId,
            string replacementSessionId,
            BffSessionRecord session,
            CancellationToken cancellationToken)
        {
            if (!sessions.TryRemove(currentSessionId, out _))
            {
                return Task.FromResult(false);
            }

            sessions[replacementSessionId] = session;
            return Task.FromResult(true);
        }

        public Task DeleteAsync(string sessionId, CancellationToken cancellationToken)
        {
            sessions.TryRemove(sessionId, out _);
            return Task.CompletedTask;
        }

        public Task<bool> TryUpdateAsync(
            string sessionId,
            BffSessionRecord session,
            CancellationToken cancellationToken)
        {
            if (DeleteBeforeNextUpdate)
            {
                DeleteBeforeNextUpdate = false;
                sessions.TryRemove(sessionId, out _);
                return Task.FromResult(false);
            }

            while (sessions.TryGetValue(sessionId, out var current))
            {
                if (sessions.TryUpdate(sessionId, session, current))
                {
                    return Task.FromResult(true);
                }
            }

            return Task.FromResult(false);
        }
    }

    private sealed class InMemorySessionLock : IBffSessionLock
    {
        private readonly ConcurrentDictionary<string, SemaphoreSlim> locks = [];

        public bool FailAcquire { get; init; }

        public async Task<IAsyncDisposable?> AcquireAsync(
            string sessionId,
            TimeSpan waitTimeout,
            TimeSpan leaseTime,
            CancellationToken cancellationToken)
        {
            if (FailAcquire)
            {
                return null;
            }

            var semaphore = locks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
            return await semaphore.WaitAsync(waitTimeout, cancellationToken)
                ? new SemaphoreLease(semaphore)
                : null;
        }

        private sealed class SemaphoreLease(SemaphoreSlim semaphore) : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                semaphore.Release();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class FakeIdentityAuthClient : IIdentityAuthClient
    {
        public required IdentityLoginResponse Login { get; set; }

        public IdentityLoginResponse RefreshLogin { get; set; } = CreateLoginResponse(tokenSuffix: "refreshed");

        public IdentityMigrationAccessResponse MigrationAccess { get; set; } = new(
            DateTime.UtcNow.AddMinutes(15),
            "server-migration-access-token");

        public ConcurrentQueue<string> LoggedOutRefreshTokens { get; } = [];

        public string? LoggedOutRefreshToken => LoggedOutRefreshTokens.LastOrDefault();

        public int RefreshCount => refreshCount;

        public HttpStatusCode RefreshStatusCode { get; init; } = HttpStatusCode.OK;

        public TaskCompletionSource RefreshEntered { get; private set; } = NewCompletionSource();

        private TaskCompletionSource? refreshRelease;
        private int refreshCount;

        public void BlockRefresh()
        {
            RefreshEntered = NewCompletionSource();
            refreshRelease = NewCompletionSource();
        }

        public void ReleaseRefresh() => refreshRelease?.TrySetResult();

        public Task<IdentityAuthResult<LoginOtpChallengeResponse>> RequestLoginCodeAsync(
            LoginOtpRequest request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new IdentityAuthResult<LoginOtpChallengeResponse>(
                HttpStatusCode.OK,
                new LoginOtpChallengeResponse(request.Phone, DateTime.UtcNow.AddMinutes(1))));
        }

        public Task<IdentityAuthResult<IdentityLoginResponse>> VerifyLoginCodeAsync(
            VerifyLoginOtpRequest request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new IdentityAuthResult<IdentityLoginResponse>(HttpStatusCode.OK, Login));
        }

        public Task<IdentityAuthResult<IdentityLoginResponse>> SelectInstitutionAsync(
            Guid institutionId,
            string accessToken,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new IdentityAuthResult<IdentityLoginResponse>(HttpStatusCode.OK, Login));
        }

        public Task<IdentityAuthResult<MigrationAccessChallengeResponse>> RequestMigrationAccessCodeAsync(
            string accessToken,
            CancellationToken cancellationToken) => Task.FromResult(
                new IdentityAuthResult<MigrationAccessChallengeResponse>(
                    HttpStatusCode.OK,
                    new MigrationAccessChallengeResponse(DateTime.UtcNow.AddMinutes(5), 1)));

        public Task<IdentityAuthResult<IdentityMigrationAccessResponse>> VerifyMigrationAccessCodeAsync(
            VerifyMigrationAccessCodeRequest request,
            string accessToken,
            CancellationToken cancellationToken) => Task.FromResult(
                new IdentityAuthResult<IdentityMigrationAccessResponse>(
                    HttpStatusCode.OK,
                    MigrationAccess));

        public async Task<IdentityAuthResult<IdentityLoginResponse>> RefreshAsync(
            string refreshToken,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref refreshCount);
            RefreshEntered.TrySetResult();
            if (refreshRelease is not null)
            {
                await refreshRelease.Task.WaitAsync(cancellationToken);
            }

            return new IdentityAuthResult<IdentityLoginResponse>(
                RefreshStatusCode,
                RefreshStatusCode == HttpStatusCode.OK ? RefreshLogin : null);
        }

        public Task<HttpStatusCode> LogoutAsync(
            string refreshToken,
            CancellationToken cancellationToken)
        {
            LoggedOutRefreshTokens.Enqueue(refreshToken);
            return Task.FromResult(HttpStatusCode.NoContent);
        }

        private static TaskCompletionSource NewCompletionSource() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
