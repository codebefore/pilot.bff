using System.Net.Http.Headers;
using Microsoft.Net.Http.Headers;
using Pilot.Bff.Api.Auth;

namespace Pilot.Bff.Api.Proxy;

public interface IBffProxyRequestFactory
{
    HttpRequestMessage Create(HttpRequest request, string targetPath, BffProxyAuthorization authorization);
}

public sealed class BffProxyRequestFactory : IBffProxyRequestFactory
{
    private static readonly string[] ForwardedRequestHeaders =
        [HeaderNames.Accept, HeaderNames.AcceptLanguage, HeaderNames.IfMatch,
         HeaderNames.IfNoneMatch, HeaderNames.IfModifiedSince, HeaderNames.Range,
         "X-Pilot-Target-Client-Id", "X-Pilot-Transfer-Owner", "X-Course-Type"];

    public HttpRequestMessage Create(
        HttpRequest incoming,
        string targetPath,
        BffProxyAuthorization authorization)
    {
        var request = new HttpRequestMessage(
            new HttpMethod(incoming.Method),
            targetPath + incoming.QueryString);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authorization.AccessToken);
        if (authorization.InstitutionId is { } institutionId)
        {
            request.Headers.TryAddWithoutValidation("X-Institution-Id", institutionId.ToString("D"));
        }

        if (!string.IsNullOrWhiteSpace(authorization.MigrationAccessToken) &&
            IsMigrationImportPath(targetPath))
        {
            request.Headers.TryAddWithoutValidation(
                "X-Migration-Access-Token",
                authorization.MigrationAccessToken);
        }

        foreach (var name in ForwardedRequestHeaders)
        {
            if (incoming.Headers.TryGetValue(name, out var values))
            {
                request.Headers.TryAddWithoutValidation(name, values.ToArray());
            }
        }

        if (incoming.ContentLength is > 0 || incoming.Headers.ContainsKey(HeaderNames.TransferEncoding))
        {
            request.Content = new StreamContent(incoming.Body);
            if (!string.IsNullOrWhiteSpace(incoming.ContentType) &&
                System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(
                    incoming.ContentType,
                    out var contentType))
            {
                request.Content.Headers.ContentType = contentType;
            }

            request.Content.Headers.ContentLength = incoming.ContentLength;
        }

        return request;
    }

    private static bool IsMigrationImportPath(string targetPath)
    {
        var segments = targetPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 3 &&
            segments[0] == "v1" &&
            segments[1] is "candidates" or "document" or "finance" or "training" &&
            segments.Skip(2).Contains("imports", StringComparer.Ordinal);
    }
}
