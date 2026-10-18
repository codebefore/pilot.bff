using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Pilot.Bff.Api.Contracts;

namespace Pilot.Bff.Api.Auth;

public sealed record IdentityAuthResult<T>(HttpStatusCode StatusCode, T? Value)
{
    public bool IsSuccess => StatusCode is >= HttpStatusCode.OK and < HttpStatusCode.MultipleChoices;
}

public interface IIdentityAuthClient
{
    Task<IdentityAuthResult<LoginOtpChallengeResponse>> RequestLoginCodeAsync(
        LoginOtpRequest request,
        CancellationToken cancellationToken);

    Task<IdentityAuthResult<IdentityLoginResponse>> VerifyLoginCodeAsync(
        VerifyLoginOtpRequest request,
        CancellationToken cancellationToken);

    Task<IdentityAuthResult<IdentityLoginResponse>> RefreshAsync(
        string refreshToken,
        CancellationToken cancellationToken);

    Task<IdentityAuthResult<IdentityLoginResponse>> SelectInstitutionAsync(
        Guid institutionId,
        string accessToken,
        CancellationToken cancellationToken);

    Task<IdentityAuthResult<MigrationAccessChallengeResponse>> RequestMigrationAccessCodeAsync(
        string accessToken,
        CancellationToken cancellationToken);

    Task<IdentityAuthResult<IdentityMigrationAccessResponse>> VerifyMigrationAccessCodeAsync(
        VerifyMigrationAccessCodeRequest request,
        string accessToken,
        CancellationToken cancellationToken);

    Task<HttpStatusCode> LogoutAsync(string refreshToken, CancellationToken cancellationToken);
}

public sealed class IdentityAuthClient(HttpClient httpClient) : IIdentityAuthClient
{
    public Task<IdentityAuthResult<LoginOtpChallengeResponse>> RequestLoginCodeAsync(
        LoginOtpRequest request,
        CancellationToken cancellationToken)
    {
        return SendAsync<LoginOtpChallengeResponse>(
            HttpMethod.Post,
            "api/auth/login/request-code",
            request,
            accessToken: null,
            cancellationToken);
    }

    public Task<IdentityAuthResult<IdentityLoginResponse>> VerifyLoginCodeAsync(
        VerifyLoginOtpRequest request,
        CancellationToken cancellationToken)
    {
        return SendAsync<IdentityLoginResponse>(
            HttpMethod.Post,
            "api/auth/login/verify-code",
            request,
            accessToken: null,
            cancellationToken);
    }

    public Task<IdentityAuthResult<IdentityLoginResponse>> SelectInstitutionAsync(
        Guid institutionId,
        string accessToken,
        CancellationToken cancellationToken)
    {
        return SendAsync<IdentityLoginResponse>(
            HttpMethod.Post,
            $"api/auth/institutions/{institutionId:D}/select",
            new { },
            accessToken,
            cancellationToken);
    }

    public Task<IdentityAuthResult<IdentityLoginResponse>> RefreshAsync(
        string refreshToken,
        CancellationToken cancellationToken)
    {
        return SendAsync<IdentityLoginResponse>(
            HttpMethod.Post,
            "api/auth/refresh",
            new RefreshTokenRequest(refreshToken),
            accessToken: null,
            cancellationToken);
    }

    public Task<IdentityAuthResult<MigrationAccessChallengeResponse>> RequestMigrationAccessCodeAsync(
        string accessToken,
        CancellationToken cancellationToken)
    {
        return SendAsync<MigrationAccessChallengeResponse>(
            HttpMethod.Post,
            "api/auth/migration-access/request-code",
            new { },
            accessToken,
            cancellationToken);
    }

    public Task<IdentityAuthResult<IdentityMigrationAccessResponse>> VerifyMigrationAccessCodeAsync(
        VerifyMigrationAccessCodeRequest request,
        string accessToken,
        CancellationToken cancellationToken)
    {
        return SendAsync<IdentityMigrationAccessResponse>(
            HttpMethod.Post,
            "api/auth/migration-access/verify-code",
            request,
            accessToken,
            cancellationToken);
    }

    public async Task<HttpStatusCode> LogoutAsync(
        string refreshToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/auth/logout")
        {
            Content = JsonContent.Create(new { refreshToken })
        };
        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        return response.StatusCode;
    }

    private async Task<IdentityAuthResult<T>> SendAsync<T>(
        HttpMethod method,
        string path,
        object body,
        string? accessToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path)
        {
            Content = JsonContent.Create(body)
        };

        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return new IdentityAuthResult<T>(response.StatusCode, default);
        }

        var value = await response.Content.ReadFromJsonAsync<T>(cancellationToken);
        return value is null
            ? new IdentityAuthResult<T>(HttpStatusCode.BadGateway, default)
            : new IdentityAuthResult<T>(response.StatusCode, value);
    }
}
