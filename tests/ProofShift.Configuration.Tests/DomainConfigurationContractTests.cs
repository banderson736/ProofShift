using ProofShift.Domain;
using Xunit;

namespace ProofShift.Configuration.Tests;

public sealed class DomainConfigurationContractTests
{
    [Fact]
    public void StorageEndpointCopiesConfigurationAndSystemRetainsEndpoint()
    {
        var configuration = new Dictionary<string, string> { ["secretRef"] = "LEGACY_DB" };
        var endpoint = new StorageEndpointDefinition(
            new StorageEndpointId("member-store"),
            new ConnectorId("synthetic"),
            configuration);
        configuration["secretRef"] = "resolved-secret-value";
        var system = new SystemDefinition(
            new SystemId("legacy"), "Legacy System", SystemRole.Source, [endpoint]);

        Assert.Equal("LEGACY_DB", endpoint.Configuration["secretRef"]);
        Assert.Same(endpoint, Assert.Single(system.StorageEndpoints));
    }
}
