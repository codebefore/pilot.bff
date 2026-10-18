using Pilot.Bff.Api.Contracts;

namespace Pilot.Bff.Api.Session;

public sealed record BffSessionRecord(
    string AccessToken,
    DateTime AccessTokenExpiresAtUtc,
    string RefreshToken,
    DateTime RefreshTokenExpiresAtUtc,
    BffAuthUser User,
    IReadOnlyList<BffAuthInstitution> Institutions,
    BffAuthInstitution? ActiveInstitution,
    DateTime CreatedAtUtc,
    DateTime LastActivityAtUtc,
    DateTime IdleExpiresAtUtc,
    DateTime AbsoluteExpiresAtUtc,
    string? MigrationAccessToken = null,
    DateTime? MigrationAccessExpiresAtUtc = null,
    Guid? MigrationAccessInstitutionId = null);
