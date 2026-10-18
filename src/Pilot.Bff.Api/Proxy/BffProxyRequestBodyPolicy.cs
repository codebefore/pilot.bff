using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Pilot.Bff.Api.Options;

namespace Pilot.Bff.Api.Proxy;

public interface IBffProxyRequestBodyPolicy
{
    long GetMaxRequestBodyBytes(HttpRequest request);
}

public sealed class BffProxyRequestBodyPolicy(IOptions<BffProxyOptions> options)
    : IBffProxyRequestBodyPolicy
{
    public long GetMaxRequestBodyBytes(HttpRequest request)
    {
        var configured = options.Value;
        if (!IsMultipart(request.ContentType))
        {
            return configured.MaxRequestBodyBytes;
        }

        var segments = request.Path.Value?
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
        if (segments.Length < 4 ||
            segments[0] != "bff" ||
            segments[1] != "v1" ||
            segments[2] != "document")
        {
            return configured.MaxRequestBodyBytes;
        }

        if (IsInstructorAssignmentDocumentCreate(request.Method, segments))
        {
            return Math.Max(
                configured.MaxRequestBodyBytes,
                configured.InstructorDocumentUploadMaxRequestBodyBytes);
        }

        return IsStandardDocumentUpload(request.Method, segments)
            ? Math.Max(configured.MaxRequestBodyBytes, configured.DocumentUploadMaxRequestBodyBytes)
            : configured.MaxRequestBodyBytes;
    }

    private static bool IsMultipart(string? contentType)
    {
        if (!MediaTypeHeaderValue.TryParse(contentType, out var parsed) ||
            !string.Equals(parsed.MediaType.Value, "multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var boundary = HeaderUtilities.RemoveQuotes(parsed.Boundary).Value;
        return !string.IsNullOrWhiteSpace(boundary);
    }

    private static bool IsInstructorAssignmentDocumentCreate(string method, string[] segments) =>
        HttpMethods.IsPost(method) &&
        segments.Length == 8 &&
        segments[3] == "instructors" &&
        IsGuid(segments[4]) &&
        segments[5] == "assignments" &&
        IsGuid(segments[6]) &&
        segments[7] == "documents";

    private static bool IsStandardDocumentUpload(string method, string[] segments)
    {
        if (HttpMethods.IsPost(method) &&
            segments.Length == 6 &&
            segments[3] == "candidates" &&
            IsGuid(segments[4]) &&
            segments[5] == "documents")
        {
            return true;
        }

        if (segments.Length >= 6 &&
            segments[3] == "vehicles" &&
            IsGuid(segments[4]) &&
            segments[5] == "documents")
        {
            return HttpMethods.IsPost(method) && segments.Length == 6 ||
                HttpMethods.IsPut(method) && segments.Length == 7 && IsGuid(segments[6]);
        }

        return HttpMethods.IsPost(method) &&
            segments.Length == 6 &&
            segments[3] == "correspondence" &&
            IsGuid(segments[4]) &&
            segments[5] == "files";
    }

    private static bool IsGuid(string value) =>
        Guid.TryParse(value, out var parsed) && parsed != Guid.Empty;
}
