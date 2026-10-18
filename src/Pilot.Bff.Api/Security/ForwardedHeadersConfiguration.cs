using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace Pilot.Bff.Api.Security;

public static class ForwardedHeadersConfiguration
{
    public static ForwardedHeadersSetup AddTrustedForwardedHeaders(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var section = configuration.GetSection("Bff:ForwardedHeaders");
        var enabled = section.GetValue("Enabled", false);
        var externalScheme = ParseExternalScheme(section["ExternalScheme"]);

        if (!enabled)
        {
            if (externalScheme is not null)
            {
                throw new InvalidOperationException(
                    "A forwarded external scheme requires trusted forwarded headers to be enabled.");
            }

            return new ForwardedHeadersSetup(false, null);
        }

        var knownProxies = ParseKnownProxies(section["KnownProxies"]);
        var knownNetworks = ParseKnownNetworks(section["KnownNetworks"]);

        if (knownProxies.Count == 0 && knownNetworks.Count == 0)
        {
            throw new InvalidOperationException(
                "Forwarded headers require at least one trusted proxy or network.");
        }

        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = 1;
            options.RequireHeaderSymmetry = true;
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();

            foreach (var proxy in knownProxies)
            {
                options.KnownProxies.Add(proxy);
            }

            foreach (var network in knownNetworks)
            {
                options.KnownIPNetworks.Add(network);
            }
        });

        return new ForwardedHeadersSetup(true, externalScheme);
    }

    private static string? ParseExternalScheme(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var scheme = value.Trim().ToLowerInvariant();
        return scheme is "http" or "https"
            ? scheme
            : throw new InvalidOperationException(
                $"Invalid forwarded external scheme: {value}");
    }

    private static List<IPAddress> ParseKnownProxies(string? value)
    {
        return Split(value)
            .Select(item => IPAddress.TryParse(item, out var address)
                ? address
                : throw new InvalidOperationException($"Invalid trusted proxy IP address: {item}"))
            .ToList();
    }

    private static List<System.Net.IPNetwork> ParseKnownNetworks(string? value)
    {
        return Split(value)
            .Select(item => System.Net.IPNetwork.TryParse(item, out var network)
                ? network
                : throw new InvalidOperationException($"Invalid trusted proxy network: {item}"))
            .ToList();
    }

    private static IEnumerable<string> Split(string? value)
    {
        return (value ?? string.Empty)
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}

public readonly record struct ForwardedHeadersSetup(bool Enabled, string? ExternalScheme);
