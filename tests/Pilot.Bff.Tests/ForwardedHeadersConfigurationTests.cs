using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pilot.Bff.Api.Security;

namespace Pilot.Bff.Tests;

public sealed class ForwardedHeadersConfigurationTests
{
    [Fact]
    public void ForwardedHeaders_CannotBeEnabledWithoutTrustedSource()
    {
        var configuration = CreateConfiguration(new Dictionary<string, string?>
        {
            ["Bff:ForwardedHeaders:Enabled"] = "true"
        });
        var services = new ServiceCollection();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddTrustedForwardedHeaders(configuration));

        Assert.Contains("trusted proxy or network", exception.Message);
    }

    [Fact]
    public void ForwardedHeaders_RemainDisabledByDefault()
    {
        var configuration = CreateConfiguration([]);
        var services = new ServiceCollection();

        var setup = services.AddTrustedForwardedHeaders(configuration);

        Assert.False(setup.Enabled);
        Assert.Null(setup.ExternalScheme);
    }

    [Fact]
    public void ExternalScheme_RequiresTrustedForwardedHeaders()
    {
        var configuration = CreateConfiguration(new Dictionary<string, string?>
        {
            ["Bff:ForwardedHeaders:ExternalScheme"] = "https"
        });
        var services = new ServiceCollection();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddTrustedForwardedHeaders(configuration));

        Assert.Contains("requires trusted forwarded headers", exception.Message);
    }

    [Fact]
    public void ExternalScheme_IsAcceptedWithTrustedNetwork()
    {
        var configuration = CreateConfiguration(new Dictionary<string, string?>
        {
            ["Bff:ForwardedHeaders:Enabled"] = "true",
            ["Bff:ForwardedHeaders:KnownNetworks"] = "10.42.0.0/16",
            ["Bff:ForwardedHeaders:ExternalScheme"] = "HTTPS"
        });
        var services = new ServiceCollection();

        var setup = services.AddTrustedForwardedHeaders(configuration);

        Assert.True(setup.Enabled);
        Assert.Equal("https", setup.ExternalScheme);
    }

    private static IConfiguration CreateConfiguration(
        Dictionary<string, string?> values)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }
}
