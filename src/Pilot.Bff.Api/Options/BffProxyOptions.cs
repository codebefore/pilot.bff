namespace Pilot.Bff.Api.Options;

public sealed class BffProxyOptions
{
    public const string SectionName = "Bff:Proxy";

    public bool Enabled { get; init; }

    public string GatewayBaseUrl { get; init; } = "http://pilot-gateway";

    public int TimeoutSeconds { get; init; } = 30;

    public int ResponseStreamingTimeoutSeconds { get; init; } = 300;

    public long MaxRequestBodyBytes { get; init; } = 10 * 1024 * 1024;

    public long DocumentUploadMaxRequestBodyBytes { get; init; } = 11 * 1024 * 1024;

    public long InstructorDocumentUploadMaxRequestBodyBytes { get; init; } = 26 * 1024 * 1024;

    public int ReadRequestsPerMinute { get; init; } = 120;

    public int MutationRequestsPerMinute { get; init; } = 30;

    public int ReadConcurrentRequests { get; init; } = 50;

    public int MutationConcurrentRequests { get; init; } = 10;

    public int FileTransferConcurrentRequests { get; init; } = 200;
}
