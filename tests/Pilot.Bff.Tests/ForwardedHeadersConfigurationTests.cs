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

        var enabled = services.AddTrustedForwardedHeaders(configuration);

        Assert.False(enabled);
    }

    private static IConfiguration CreateConfiguration(
        Dictionary<string, string?> values)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }
}
