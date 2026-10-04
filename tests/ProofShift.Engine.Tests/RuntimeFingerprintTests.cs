using ProofShift.Domain;
using Xunit;

namespace ProofShift.Engine.Tests;

public sealed class RuntimeFingerprintTests
{
    [Fact]
    public void FingerprintRetainsOpaqueHashesAndVersionedDependencies()
    {
        var connectors = new Dictionary<string, string> { ["synthetic"] = "2.0" };
        var fingerprint = new RuntimeFingerprint(
            "0.1.0",
            "config-fingerprint-v1",
            "graph-fingerprint-v1",
            connectors,
            new Dictionary<string, string> { ["proofshift.pension"] = "0.1.0" });
        connectors["synthetic"] = "changed";

        Assert.Equal("config-fingerprint-v1", fingerprint.ConfigurationHash);
        Assert.Equal("graph-fingerprint-v1", fingerprint.GraphHash);
        Assert.Equal("2.0", fingerprint.ConnectorVersions["synthetic"]);
        Assert.Equal("0.1.0", fingerprint.DomainPackVersions["proofshift.pension"]);
    }
}
