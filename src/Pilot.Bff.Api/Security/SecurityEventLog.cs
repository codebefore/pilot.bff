namespace Pilot.Bff.Api.Security;

public static class SecurityEventLog
{
    public static void Rejected(ILogger logger, string kind)
    {
        logger.LogWarning("security_event service=pilot-bff kind={Kind}", kind);
    }
}
