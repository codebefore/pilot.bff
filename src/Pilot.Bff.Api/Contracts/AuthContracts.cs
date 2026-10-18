using System.Text.Json.Serialization;

namespace Pilot.Bff.Api.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter<LoginOtpChannel>))]
public enum LoginOtpChannel
{
    [JsonStringEnumMemberName("whatsapp")]
    WhatsApp = 0,

    [JsonStringEnumMemberName("sms")]
    Sms = 1
}

public sealed record LoginOtpRequest(string Phone, LoginOtpChannel Channel = LoginOtpChannel.WhatsApp);

public sealed record VerifyLoginOtpRequest(string Phone, string Code);

public sealed record RefreshTokenRequest(string RefreshToken);

public sealed record LoginOtpChallengeResponse(string Phone, DateTime ExpiresAtUtc);

public sealed record SelectInstitutionRequest(Guid InstitutionId);

public sealed record BffAuthUser(
    Guid Id,
    string FullName,
    string? Phone,
    string? RoleName,
    bool IsSuperAdmin);

public sealed record BffAuthInstitution(
    Guid Id,
    string Name,
    string? RoleName,
    bool IsDefault,
    IReadOnlyDictionary<string, string> Permissions);

public sealed record IdentityLoginResponse(
    string AccessToken,
    DateTime ExpiresAtUtc,
    string RefreshToken,
    DateTime RefreshTokenExpiresAtUtc,
    BffAuthUser User,
    IReadOnlyList<BffAuthInstitution> Institutions,
    BffAuthInstitution? ActiveInstitution);

public sealed record BffSessionResponse(
    BffAuthUser User,
    IReadOnlyList<BffAuthInstitution> Institutions,
    BffAuthInstitution? ActiveInstitution,
    DateTime IdleExpiresAtUtc,
    DateTime AbsoluteExpiresAtUtc,
    DateTime? MigrationAccessExpiresAtUtc = null);

public sealed record MigrationAccessChallengeResponse(
    DateTime ExpiresAtUtc,
    int RecipientCount);

public sealed record VerifyMigrationAccessCodeRequest(string Code);

public sealed record IdentityMigrationAccessResponse(
    DateTime ExpiresAtUtc,
    string AccessToken);

public sealed record BffMigrationAccessResponse(DateTime ExpiresAtUtc);

public sealed record AntiforgeryTokenResponse(string Token);
