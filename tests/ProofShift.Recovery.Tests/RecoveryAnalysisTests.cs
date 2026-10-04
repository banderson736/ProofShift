using ProofShift.Configuration;
using ProofShift.Domain;
using Xunit;

namespace ProofShift.Recovery.Tests;

public sealed class RecoveryAnalysisTests
{
    [Fact]
    public void ReverseAnalysisRejectsStringNormalizationAsLossy()
    {
        var edge = Edge(new TransformationStep(TransformationStepType.NormalizeString, "1"));

        var result = TransformationLossAnalyzer.Analyze(edge);

        Assert.False(result.IsReversible);
        Assert.Contains("normalization", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReverseAnalysisRejectsManyToOneCodeMapAndAcceptsUniqueMap()
    {
        var lossy = Edge(new TransformationStep(TransformationStepType.CodeMap, "1",
        [
            new KeyValuePair<string, string>("A", "ACTIVE"),
            new KeyValuePair<string, string>("E", "ACTIVE")
        ]));
        var reversible = Edge(new TransformationStep(TransformationStepType.CodeMap, "1",
        [
            new KeyValuePair<string, string>("A", "ACTIVE"),
            new KeyValuePair<string, string>("R", "RETIRED")
        ]));

        var lossyResult = TransformationLossAnalyzer.Analyze(lossy);
        var reversibleResult = TransformationLossAnalyzer.Analyze(reversible);

        Assert.False(lossyResult.IsReversible);
        Assert.Contains("multiple source values", lossyResult.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.True(reversibleResult.IsReversible);
    }

    [Fact]
    public void RecoveryPolicyLoaderFingerprintsPolicyAndKeepsSourceCheckpointsDistinct()
    {
        var strict = LoadPolicy(allowIrreversible: false, maximumIrreversible: 0);
        var approved = LoadPolicy(allowIrreversible: true, maximumIrreversible: 2);

        Assert.False(strict.AllowIrreversibleOperations);
        Assert.True(strict.RequireRecoveryRehearsal);
        Assert.Single(strict.RequiredSourceCheckpoints);
        Assert.Equal("legacy-pension", strict.RequiredSourceCheckpoints.Single().SystemId);
        Assert.NotEqual(strict.Fingerprint, approved.Fingerprint);
        Assert.Equal("proofshift-recovery-policy-v1", EffectiveRecoveryPolicy.FingerprintVersion);
    }

    [Fact]
    public void CompensatorRegistryResolvesOnlyExplicitlyRegisteredStrategies()
    {
        var registry = new RecoveryCompensatorRegistry([new ShadowBaselineRestoreCompensator()]);

        Assert.Equal("restore-shadow-baseline", registry.Resolve("restore-shadow-baseline").StrategyId);
        var exception = Assert.Throws<RecoveryException>(() => registry.Resolve("arbitrary-command"));
        Assert.Equal(RecoveryIssueCodes.UnknownCompensator, exception.Code);
    }

    [Fact]
    public void ApprovedIrreversibleArtifactsRemainOutsideRecoverableCoverage()
    {
        var nodeId = new MigrationNodeId(Guid.NewGuid());
        var target = new ArtifactReference(new ArtifactId("target-1"), new SystemId("shadow"),
            new StorageEndpointId("target-store"), "row", "identity-1");
        var assessment = new RecoveryArtifactCoverage(new GraphArtifactReference(nodeId, target), "Pension.Member",
            [new MigrationEdgeId(Guid.NewGuid())], [RecoveryMode.Irreversible], covered: true,
            recoverable: false, approvedIrreversible: true, reason: "Explicitly approved irreversible target state.");

        var summary = new RecoveryCoverageSummary([], [assessment]);

        Assert.Equal(1, summary.AffectedArtifacts);
        Assert.Equal(0, summary.RecoverableArtifacts);
        Assert.Equal(1, summary.IrrecoverableArtifacts);
        Assert.Equal(0m, summary.RecoverablePercentage);
    }

    private static MigrationEdge Edge(TransformationStep step)
    {
        var source = new MigrationNodeId(Guid.NewGuid());
        var target = new MigrationNodeId(Guid.NewGuid());
        return new MigrationEdge(new MigrationEdgeId(Guid.NewGuid()), "test-edge", [source], [target],
            new MigrationOperation(MigrationOperationType.Transform,
                fields: [new TransformationFieldDefinition("status", "source_status", [step])]), "1",
            new RecoveryDefinition(RecoveryMode.Reverse));
    }

    private static EffectiveRecoveryPolicy LoadPolicy(bool allowIrreversible, long maximumIrreversible)
    {
        static ConfigurationScalarNode Text(string value) => new(ConfigurationScalarKind.Text, value);
        static ConfigurationScalarNode Boolean(bool value) => new(ConfigurationScalarKind.Boolean, value.ToString().ToLowerInvariant());
        static ConfigurationScalarNode Number(long value) => new(ConfigurationScalarKind.Number, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        static ConfigurationMappingNode Map(params (string Key, ConfigurationDocumentNode Value)[] items) =>
            new(items.Select(item => new KeyValuePair<string, ConfigurationDocumentNode>(item.Key, item.Value)));
        var requiredSource = Map(("system", Text("legacy-pension")), ("storage", Text("member-database")));
        var approval = Map(("irreversibleTransformations", Map(("required", Boolean(true)))));
        var document = Map(
            ("requireRecoveryForDestructiveOperations", Boolean(true)),
            ("allowIrreversible", Map(("default", Boolean(allowIrreversible)))),
            ("requireValidatedRestore", Boolean(true)),
            ("requireRecoveryRehearsal", Boolean(true)),
            ("maximumIrreversibleArtifacts", Number(maximumIrreversible)),
            ("requiredSnapshots", new ConfigurationSequenceNode([requiredSource])),
            ("approval", approval));
        var file = new ReferencedConfigurationFile(ConfigurationFileKind.RecoveryPolicy, "recovery/policy.yaml",
            "recovery-policy-v1", document);
        var root = new RootConfigurationDto(1, new ProjectConfigurationDto("test-project", "Synthetic"), null,
            [], null, null, "recovery/policy.yaml");
        var configuration = new LoadedProjectConfiguration(root, [], [], [file], "canonical-policy-test", new string('a', 64));
        return RecoveryPolicyConfigurationLoader.Load(configuration);
    }
}
