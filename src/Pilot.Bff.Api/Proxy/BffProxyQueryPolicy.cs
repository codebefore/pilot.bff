namespace Pilot.Bff.Api.Proxy;

public interface IBffProxyQueryPolicy
{
    bool IsSafe(IQueryCollection query);
}

public sealed class BffProxyQueryPolicy : IBffProxyQueryPolicy
{
    private static readonly HashSet<string> CredentialParameters = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "access_token",
        "refresh_token",
        "authorization",
        "api_key",
        "migration_access_token"
    };

    public bool IsSafe(IQueryCollection query) =>
        query.Keys.All(key => !CredentialParameters.Contains(key));
}
