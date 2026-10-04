using ProofShift.Configuration;
using ProofShift.Domain;
using Xunit;

namespace ProofShift.Graph.Tests;

public sealed class MigrationGraphCompilerTests
{
    [Fact]
    public async Task SimpleGraphCompilesDomainSelectorsTransformsAndRecovery()
    {
        var result = await CompileFixtureAsync("valid-simple.yaml");

        Assert.True(result.IsValid, FormatIssues(result));
        var graph = Assert.IsType<MigrationGraph>(result.Graph);
        Assert.Equal("proofshift-graph-canonical-v1", result.GraphCanonicalizationVersion);
        Assert.Equal(64, graph.GraphHash.Length);
        Assert.Equal(graph.GraphHash, result.GraphHash);
        Assert.Equal(2, graph.Nodes.Count);
        Assert.Equal("dbo.MEMBER", graph.Nodes.Single(node => node.Name == "legacy-member").Selector.Properties["name"]);

        var edge = Assert.Single(graph.Edges);
        Assert.Equal("member-migration", edge.Name);
        Assert.Single(edge.Sources);
        Assert.Single(edge.Targets);
        Assert.Equal("3", edge.Version);
        Assert.Equal(RecoveryMode.Restore, edge.Recovery!.Mode);
        Assert.Equal("2", edge.Operation.Fields.Single().Pipeline[1].Version);
        Assert.Equal(1, result.Summary!.SourceAccounted);
        Assert.Equal(1, result.Summary.TargetConnected);
    }

    [Fact]
    public async Task SupportedTopologiesAndEveryOperationTypeCompile()
    {
        foreach (var fixture in new[]
        {
            "valid-multi-source.yaml",
            "valid-split.yaml",
            "valid-merge.yaml",
            "valid-archive.yaml",
            "valid-exclusion.yaml"
        })
        {
            var result = await CompileFixtureAsync(fixture);
            Assert.True(result.IsValid, $"{fixture}{Environment.NewLine}{FormatIssues(result)}");
        }

        var allOperations = await CompileFixtureAsync("valid-all-operations.yaml");
        Assert.True(allOperations.IsValid, FormatIssues(allOperations));
        Assert.Equal(10, allOperations.Graph!.Edges.Count);
        Assert.Equal(10, allOperations.Summary!.OperationCounts.Values.Sum());
        Assert.Equal(2, allOperations.Summary.SourceAccounted);
        Assert.Equal(2, allOperations.Summary.TargetConnected);
        Assert.Contains(allOperations.Graph.Edges, edge => edge.Sources.Count == 2 && edge.Targets.Count == 1);
        Assert.Contains(allOperations.Graph.Edges, edge => edge.Sources.Count == 1 && edge.Targets.Count == 2);
    }

    [Fact]
    public async Task RelationshipOnlyCycleIsWarningButExecutionCycleIsError()
    {
        var relationshipCycle = await CompileFixtureAsync("valid-relationship-cycle.yaml");
        Assert.True(relationshipCycle.IsValid, FormatIssues(relationshipCycle));
        Assert.Contains(relationshipCycle.Issues, issue =>
            issue.Code == GraphIssueCodes.CycleDetected && issue.Severity == GraphValidationSeverity.Warning);

        var executionCycle = await CompileFixtureAsync("invalid-cycle.yaml");
        Assert.False(executionCycle.IsValid);
        Assert.Contains(executionCycle.Issues, issue =>
            issue.Code == GraphIssueCodes.CycleDetected && issue.Severity == GraphValidationSeverity.Error);
        Assert.Null(executionCycle.GraphHash);
    }

    [Theory]
    [InlineData("invalid-duplicate-node.yaml", GraphIssueCodes.DuplicateNodeId)]
    [InlineData("invalid-duplicate-edge.yaml", GraphIssueCodes.DuplicateEdgeId)]
    [InlineData("invalid-system.yaml", GraphIssueCodes.UnknownSystem)]
    [InlineData("invalid-endpoint.yaml", GraphIssueCodes.UnknownStorageEndpoint)]
    [InlineData("invalid-endpoint-other-system.yaml", GraphIssueCodes.UnknownStorageEndpoint)]
    [InlineData("invalid-source-reference.yaml", GraphIssueCodes.UnknownSourceNode)]
    [InlineData("invalid-target-reference.yaml", GraphIssueCodes.UnknownTargetNode)]
    [InlineData("invalid-orphan-source.yaml", GraphIssueCodes.OrphanSource)]
    [InlineData("invalid-orphan-target.yaml", GraphIssueCodes.OrphanTarget)]
    [InlineData("invalid-missing-recovery.yaml", GraphIssueCodes.MissingRecovery)]
    [InlineData("invalid-recovery.yaml", GraphIssueCodes.InvalidRecovery)]
    [InlineData("invalid-compensation-recovery.yaml", GraphIssueCodes.InvalidRecovery)]
    [InlineData("invalid-archive-target.yaml", GraphIssueCodes.InvalidCardinality)]
    [InlineData("invalid-cardinality.yaml", GraphIssueCodes.InvalidCardinality)]
    [InlineData("invalid-operation.yaml", GraphIssueCodes.InvalidOperation)]
    [InlineData("invalid-selector.yaml", GraphIssueCodes.InvalidSelector)]
    [InlineData("invalid-empty-source.yaml", GraphIssueCodes.InvalidCardinality)]
    [InlineData("invalid-empty-target.yaml", GraphIssueCodes.InvalidCardinality)]
    [InlineData("unsupported-version.yaml", GraphIssueCodes.UnsupportedVersion)]
    [InlineData("empty-graph.yaml", GraphIssueCodes.InvalidGraphDocument)]
        [InlineData("invalid-self-cycle.yaml", GraphIssueCodes.CycleDetected)]
    public async Task InvalidFixturesProduceStableExpectedDiagnostics(string fixture, string expectedCode)
    {
        var first = await CompileFixtureAsync(fixture);
        var second = await CompileFixtureAsync(fixture);

        Assert.False(first.IsValid, FormatIssues(first));
        Assert.Null(first.Graph);
        Assert.Null(first.GraphHash);
        Assert.Contains(first.Issues, issue => issue.Code == expectedCode);
        Assert.Equal(first.Issues, second.Issues);
    }

    [Fact]
    public async Task EquivalentYamlFormattingAndDeclarationOrderHaveTheSameGraphHash()
    {
        var original = await CompileFixtureAsync("valid-simple.yaml");
        var reordered = await CompileFixtureAsync("valid-simple.yaml", directory =>
            File.WriteAllText(Path.Combine(directory, "migration", "graph.yaml"), """
                # semantically identical, different presentation
                edges:
                  member-migration:
                    recovery: { requiresSnapshot: true, mode: restore }
                    operation:
                      fields:
                        status:
                          pipeline:
                            - type: trim
                            - values: { R: RETIRED, A: ACTIVE }
                              version: '2'
                              type: code-map
                          source: MEMBER_STATUS
                      version: '3'
                      type: transform
                    to: [participant]
                    from: [legacy-member]
                nodes:
                  participant:
                    selector:
                      identity: [id]
                      properties: { name: participant }
                      kind: table
                    semanticType: Generic.Member
                    storage: participant-store
                    system: modern
                    type: target
                  legacy-member:
                    selector:
                      identity: [MEMBER_ID]
                      properties: { name: dbo.MEMBER }
                      kind: table
                    semanticType: Generic.Member
                    storage: member-store
                    system: legacy
                    type: source
                version: 1
                """));

        Assert.True(original.IsValid, FormatIssues(original));
        Assert.True(reordered.IsValid, FormatIssues(reordered));
        Assert.Equal(original.CanonicalGraph, reordered.CanonicalGraph);
        Assert.Equal(original.GraphHash, reordered.GraphHash);
    }

    [Fact]
    public async Task CanonicalizerSortsNodeEdgeAndEndpointSetsIndependentlyOfInputOrder()
    {
        var result = await CompileFixtureAsync("valid-all-operations.yaml");
        Assert.True(result.IsValid, FormatIssues(result));
        var graph = result.Graph!;
        var externalKeys = graph.Nodes.ToDictionary(node => node.Id, node => node.Name);
        var permutedEdges = graph.Edges.Reverse().Select(edge => new MigrationEdge(
            edge.Id,
            edge.Name,
            edge.Sources.Reverse(),
            edge.Targets.Reverse(),
            edge.Operation,
            edge.Version,
            edge.Recovery));

        var canonical = GraphCanonicalizer.Canonicalize(
            result.GraphVersion!.Value,
            graph.Nodes.Reverse(),
            permutedEdges,
            externalKeys);

        Assert.Equal(result.CanonicalGraph, canonical);
    }

    [Fact]
    public async Task SemanticSystemEndpointTransformationRecoveryAndTopologyChangesAlterTheHash()
    {
        var baseline = await CompileFixtureAsync("valid-simple.yaml");
        var baselineHash = baseline.GraphHash;
        var changed = new[]
        {
            await CompileFixtureAsync("valid-simple.yaml", directory => ReplaceGraph(directory, "Generic.Member", "Generic.Person")),
            await CompileFixtureAsync("valid-simple.yaml", directory => ReplaceGraph(directory, "dbo.MEMBER", "dbo.PERSON")),
            await CompileFixtureAsync("valid-simple.yaml", directory => ReplaceGraph(directory, "system: legacy", "system: legacy-alt")),
            await CompileFixtureAsync("valid-simple.yaml", directory => ReplaceGraph(directory, "storage: member-store", "storage: member-store-alt")),
            await CompileFixtureAsync("valid-simple.yaml", directory => ReplaceGraph(directory, "A: ACTIVE", "A: ENROLLED")),
            await CompileFixtureAsync("valid-simple.yaml", directory => ReplaceGraph(directory, "mode: restore", "mode: compensate\n      strategy: rebuild-source")),
            await CompileFixtureAsync("valid-simple.yaml", directory => ReplaceGraph(directory, "type: transform", "type: map")),
            await CompileFixtureAsync("valid-split.yaml")
        };

        Assert.All(changed, result =>
        {
            Assert.True(result.IsValid, FormatIssues(result));
            Assert.NotEqual(baselineHash, result.GraphHash);
        });
    }

    [Fact]
    public async Task RepeatedCompilationProducesStableGraphIdsHashAndImmutableCollections()
    {
        var first = await CompileFixtureAsync("valid-simple.yaml");
        var second = await CompileFixtureAsync("valid-simple.yaml");
        var firstGraph = Assert.IsType<MigrationGraph>(first.Graph);
        var secondGraph = Assert.IsType<MigrationGraph>(second.Graph);

        Assert.Equal(first.GraphHash, second.GraphHash);
        Assert.Equal(firstGraph.Nodes, secondGraph.Nodes);
        Assert.Equal(firstGraph.Edges, secondGraph.Edges);
        Assert.IsType<DomainList<MigrationNode>>(firstGraph.Nodes);
        Assert.IsType<DomainList<MigrationEdge>>(firstGraph.Edges);
        Assert.IsType<DomainDictionary<string>>(firstGraph.Nodes[0].Selector.Properties);
        Assert.Equal(GraphCanonicalizer.FormatVersion, firstGraph.GraphCanonicalizationVersion);
    }

    private static async Task<GraphCompilationResult> CompileFixtureAsync(
        string graphFixture,
        Action<string>? updateProject = null)
    {
        var directory = CopyProjectFixture();
        try
        {
            File.Copy(GraphFixturePath(graphFixture), Path.Combine(directory, "migration", "graph.yaml"), overwrite: true);
            updateProject?.Invoke(directory);
            var loaded = await new ConfigurationLoader().LoadAsync(
                Path.Combine(directory, "proofshift.yaml"),
                TestContext.Current.CancellationToken);
            Assert.True(loaded.IsValid, FormatConfigurationIssues(loaded));
            return MigrationGraphCompiler.Compile(Assert.IsType<LoadedProjectConfiguration>(loaded.Configuration));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CopyProjectFixture()
    {
        var sourceDirectory = Path.Combine(AppContext.BaseDirectory, "fixtures", "project");
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"proofshift-graph-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        foreach (var sourcePath in Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, sourcePath);
            var destinationPath = Path.Combine(temporaryDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(sourcePath, destinationPath);
        }

        return temporaryDirectory;
    }

    private static string GraphFixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", "graphs", name);

    private static void ReplaceGraph(string directory, string oldValue, string newValue)
    {
        var graphPath = Path.Combine(directory, "migration", "graph.yaml");
        var graph = File.ReadAllText(graphPath);
        Assert.Contains(oldValue, graph, StringComparison.Ordinal);
        File.WriteAllText(graphPath, graph.Replace(oldValue, newValue, StringComparison.Ordinal));
    }

    private static string FormatIssues(GraphCompilationResult result) =>
        string.Join(Environment.NewLine, result.Issues.Select(issue =>
            $"{issue.Code} {issue.Severity} {issue.File} {issue.Path}: {issue.Message}"));

    private static string FormatConfigurationIssues(ConfigurationLoadResult result) =>
        string.Join(Environment.NewLine, result.Issues.Select(issue =>
            $"{issue.Code} {issue.File} {issue.Path}: {issue.Message}"));
}
