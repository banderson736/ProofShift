using System.Text;
using System.Globalization;
using ProofShift.Configuration;
using ProofShift.Connectors.Abstractions;
using ProofShift.Domain;
using ProofShift.Engine;
using ProofShift.Projection;
using Xunit;

namespace ProofShift.EndToEnd.Tests;

public sealed class ProjectionRuntimeTests
{
    [Fact]
    public void CopyPreservesAllCurrentNormalizedValueKinds()
    {
        var sourceSystem = new SystemId("source-system");
        var sourceEndpoint = new StorageEndpointId("source-store");
        var targetSystem = new SystemId("target-system");
        var targetEndpoint = new StorageEndpointId("shadow-store");
        var source = new RecordEnvelope(
            new ArtifactReference(new ArtifactId("copy-source"), sourceSystem, sourceEndpoint, "row", "id=1"),
            "Generic.Record",
            new Dictionary<string, ValueNode>
            {
                ["id"] = new IntegerValue(1),
                ["null"] = new NullValue(),
                ["text"] = new StringValue("comma,value"),
                ["integer"] = new IntegerValue(long.MaxValue),
                ["decimal"] = new DecimalValue(12345678901234567890.12345678m),
                ["boolean"] = new BooleanValue(true),
                ["date"] = new DateValue(new DateOnly(2025, 3, 4)),
                ["instant"] = new InstantValue(new DateTimeOffset(2025, 3, 4, 5, 6, 7, TimeSpan.FromHours(3))),
                ["offset"] = new OffsetDateTimeValue(new DateTimeOffset(2025, 3, 4, 5, 6, 7, TimeSpan.FromHours(-4))),
                ["local"] = new LocalDateTimeValue(new DateTime(2025, 3, 4, 5, 6, 7, DateTimeKind.Unspecified)),
                ["binary"] = new BinaryReferenceValue("file-v1:opaque", 3, new string('a', 64)),
                ["collection"] = new CollectionValue([new StringValue("a,b"), new IntegerValue(2)]),
                ["object"] = new ObjectValue([new KeyValuePair<string, ValueNode>("nested", new BooleanValue(false))])
            },
            new ProvenanceMetadata(new ConnectorId("synthetic"), sourceEndpoint, "source", DateTimeOffset.UnixEpoch));
        var target = new MigrationNode(new MigrationNodeId(Guid.NewGuid()), "target", MigrationNodeType.Target,
            "Generic.Record", targetSystem, targetEndpoint,
            new ArtifactSelector("table", [new KeyValuePair<string, string>("name", "target")], ["id"]));
        var edge = new MigrationEdge(new MigrationEdgeId(Guid.NewGuid()), "copy", [new MigrationNodeId(Guid.NewGuid())],
            [target.Id], new MigrationOperation(MigrationOperationType.Copy), "1", new RecoveryDefinition(RecoveryMode.Reverse));

        var projected = TransformationRuntime.Transform(source, edge, target);

        Assert.Equal(source.Values, projected.Values);
        Assert.Equal(ProjectionIdentity.CanonicalValue(source.Values["collection"]),
            ProjectionIdentity.CanonicalValue(projected.Values["collection"]));
        Assert.Equal(ProjectionIdentity.CanonicalValue(source.Values["object"]),
            ProjectionIdentity.CanonicalValue(projected.Values["object"]));
    }

    [Theory]
    [InlineData(SystemRole.Target)]
    [InlineData(SystemRole.Source)]
    [InlineData(SystemRole.Archive)]
    public async Task ProjectionFailsClosedForNonShadowSystemsBeforePreparingConnector(SystemRole targetRole)
    {
        var scenario = Scenario(targetRole, CreateSourceRecords());
        var sourceConnector = new FakeSourceConnector(scenario.SourceNode.Name, CreateSourceRecords());
        var targetConnector = new FakeShadowTargetConnector();
        var service = new ShadowProjectionService(
            new ConnectorRegistry([sourceConnector]),
            new ShadowTargetConnectorRegistry([targetConnector]));
        var directory = CreateTemporaryDirectory();
        try
        {
            var run = await service.ProjectAsync(scenario.Configuration, scenario.Graph, directory, TestContext.Current.CancellationToken);

            Assert.Equal(ProjectionStatus.Failed, run.Status);
            Assert.Equal(0, targetConnector.PrepareCount);
            Assert.Null(run.Fingerprint);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ProjectionCancellationReturnsCancelledAndDoesNotPrepareTargets()
    {
        var records = CreateSourceRecords();
        var scenario = Scenario(SystemRole.ShadowTarget, records);
        var targetConnector = new FakeShadowTargetConnector();
        var service = new ShadowProjectionService(
            new ConnectorRegistry([new FakeSourceConnector(scenario.SourceNode.Name, records)]),
            new ShadowTargetConnectorRegistry([targetConnector]));
        var directory = CreateTemporaryDirectory();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            var run = await service.ProjectAsync(scenario.Configuration, scenario.Graph, directory, cancellation.Token);

            Assert.Equal(ProjectionStatus.Cancelled, run.Status);
            Assert.Equal(0, targetConnector.PrepareCount);
            Assert.Null(run.Fingerprint);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ProjectionRejectsChainedTargetInputBeforePreparingShadowConnectors()
    {
        var records = CreateSourceRecords();
        var scenario = Scenario(SystemRole.ShadowTarget, records);
        var firstTarget = scenario.Graph.Nodes.Single(node => node.Name == "target-a");
        var targetSystem = scenario.Configuration.Systems.Single(system => system.Role == SystemRole.ShadowTarget);
        var secondTarget = new MigrationNode(new MigrationNodeId(Guid.NewGuid()), "target-b", MigrationNodeType.Target,
            "Pension.Member", targetSystem.Id, targetSystem.StorageEndpoints[0].Id,
            new ArtifactSelector("table", [new KeyValuePair<string, string>("name", "participant_copy")], ["id"]));
        var chainedEdge = new MigrationEdge(new MigrationEdgeId(Guid.NewGuid()), "chained-edge", [firstTarget.Id], [secondTarget.Id],
            new MigrationOperation(MigrationOperationType.Copy), "1", new RecoveryDefinition(RecoveryMode.Reverse));
        var invalidGraph = new MigrationGraph(new MigrationGraphId(Guid.NewGuid()), [.. scenario.Graph.Nodes, secondTarget],
            [.. scenario.Graph.Edges, chainedEdge], "graph-hash", "test-v1");
        var targetConnector = new FakeShadowTargetConnector();
        var service = new ShadowProjectionService(
            new ConnectorRegistry([new FakeSourceConnector(scenario.SourceNode.Name, records)]),
            new ShadowTargetConnectorRegistry([targetConnector]));
        var directory = CreateTemporaryDirectory();
        try
        {
            var run = await service.ProjectAsync(scenario.Configuration, invalidGraph, directory, TestContext.Current.CancellationToken);

            Assert.Equal(ProjectionStatus.Failed, run.Status);
            Assert.Equal("PSPROJ_PATH_UNSUPPORTED", run.FailureCode);
            Assert.Equal(0, targetConnector.PrepareCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ProjectionRejectsShadowRootOverlappingLiveFilesystemSourceRoot()
    {
        var rootDirectory = CreateTemporaryDirectory();
        try
        {
            var sourceEndpoint = new StorageEndpointDefinition(new StorageEndpointId("source-files"), new ConnectorId("files"),
                [new KeyValuePair<string, string>("root", rootDirectory)]);
            var targetEndpoint = new StorageEndpointDefinition(new StorageEndpointId("shadow-files"), new ConnectorId("files"),
                [new KeyValuePair<string, string>("root", rootDirectory)]);
            var sourceSystem = new SystemDefinition(new SystemId("source-filesystem"), "Source", SystemRole.Source, [sourceEndpoint]);
            var targetSystem = new SystemDefinition(new SystemId("shadow-filesystem"), "Shadow", SystemRole.ShadowTarget, [targetEndpoint]);
            var root = new RootConfigurationDto(1, new ProjectConfigurationDto("overlap", "Overlap"), null, [], null, null, null);
            var configuration = new LoadedProjectConfiguration(root,
                [
                    new SystemConfigurationDto(sourceSystem.Id.Value, sourceSystem.Name, "source",
                        [new StorageEndpointConfigurationDto(sourceEndpoint.Id.Value, "files", [])]),
                    new SystemConfigurationDto(targetSystem.Id.Value, targetSystem.Name, "shadow-target",
                        [new StorageEndpointConfigurationDto(targetEndpoint.Id.Value, "files", [])])
                ], [sourceSystem, targetSystem], [], "canonical", "config-hash");
            var source = new MigrationNode(new MigrationNodeId(Guid.NewGuid()), "source-file", MigrationNodeType.Source,
                "Generic.Document", sourceSystem.Id, sourceEndpoint.Id,
                new ArtifactSelector("file-pattern", [new KeyValuePair<string, string>("pattern", "**/*")], ["relativePath"]));
            var target = new MigrationNode(new MigrationNodeId(Guid.NewGuid()), "target-file", MigrationNodeType.Archive,
                "Generic.Document", targetSystem.Id, targetEndpoint.Id,
                new ArtifactSelector("file-pattern", [new KeyValuePair<string, string>("pathField", "relativePath")], ["relativePath"]));
            var edge = new MigrationEdge(new MigrationEdgeId(Guid.NewGuid()), "archive", [source.Id], [target.Id],
                new MigrationOperation(MigrationOperationType.Archive), "1", new RecoveryDefinition(RecoveryMode.Restore, requiresSnapshot: true));
            var graph = new MigrationGraph(new MigrationGraphId(Guid.NewGuid()), [source, target], [edge], "graph-hash");
            var targetConnector = new FakeShadowTargetConnector();
            var service = new ShadowProjectionService(new ConnectorRegistry([]), new ShadowTargetConnectorRegistry([targetConnector]));

            var run = await service.ProjectAsync(configuration, graph, rootDirectory, TestContext.Current.CancellationToken);

            Assert.Equal(ProjectionStatus.Failed, run.Status);
            Assert.Equal("PSPROJ_SOURCE_SHADOW_OVERLAP", run.FailureCode);
            Assert.Equal(0, targetConnector.PrepareCount);
        }
        finally
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ProjectionExecutesDeterministicTransformsAndSplitWithJournalAndStableReadbackFingerprint()
    {
        var records = CreateSourceRecords();
        var scenario = Scenario(SystemRole.ShadowTarget, records, split: true);
        var sourceConnector = new FakeSourceConnector(scenario.SourceNode.Name, records);
        var targetConnector = new FakeShadowTargetConnector();
        var service = new ShadowProjectionService(
            new ConnectorRegistry([sourceConnector]),
            new ShadowTargetConnectorRegistry([targetConnector]));
        var firstDirectory = CreateTemporaryDirectory();
        var secondDirectory = CreateTemporaryDirectory();
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var first = await service.ProjectAsync(scenario.Configuration, scenario.Graph, firstDirectory, TestContext.Current.CancellationToken);
            var second = await service.ProjectAsync(scenario.Configuration, scenario.Graph, secondDirectory, TestContext.Current.CancellationToken);

            Assert.Equal(ProjectionStatus.Succeeded, first.Status);
            Assert.Equal(2, first.SourceArtifactCount);
            Assert.Equal(4, first.TargetArtifactCount);
            Assert.Equal(first.Fingerprint, second.Fingerprint);
            Assert.NotEqual(first.Id, second.Id);
            Assert.Equal(ProjectionStatus.Succeeded, second.Status);
            var journalPath = Path.Combine(firstDirectory, first.JournalPath.Replace('/', Path.DirectorySeparatorChar));
            var journal = await File.ReadAllTextAsync(journalPath, TestContext.Current.CancellationToken);
            Assert.Contains("target-a", journal, StringComparison.Ordinal);
            Assert.Contains("target-b", journal, StringComparison.Ordinal);
            Assert.Contains("normalize-string", journal, StringComparison.Ordinal);
            Assert.Contains("code-map", journal, StringComparison.Ordinal);

            var materialized = targetConnector.RecordsFor(first.Id);
            Assert.Equal(4, materialized.Count);
            Assert.All(materialized, record => Assert.Equal("ADA LOVELACE", Assert.IsType<StringValue>(record.Values["name"]).Value));
            Assert.All(materialized, record => Assert.Equal("ACTIVE", Assert.IsType<StringValue>(record.Values["status"]).Value));
            Assert.All(materialized, record => Assert.Equal(new DecimalValue(123.4500m), record.Values["amount"]));
            Assert.All(materialized, record => Assert.Equal(new NullValue(), record.Values["optional"]));
            Assert.All(materialized, record => Assert.Equal(new DateValue(new DateOnly(1990, 2, 3)), record.Values["birth_date"]));
            Assert.All(materialized, record => Assert.Equal(new DateValue(new DateOnly(1990, 2, 3)), record.Values["normalized_date"]));
            Assert.All(materialized, record => Assert.Equal(new CollectionValue([new StringValue("Ada"), new StringValue("Lovelace")]), record.Values["name_parts"]));
            Assert.All(materialized, record => Assert.Equal("  Ada Lovelace  |A", Assert.IsType<StringValue>(record.Values["display"]).Value));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            Directory.Delete(firstDirectory, recursive: true);
            Directory.Delete(secondDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ProjectionDuplicateTargetIdentityFailsAndKeepsJournaledPartialOutput()
    {
        var records = CreateSourceRecords(duplicateTargetId: true);
        var scenario = Scenario(SystemRole.ShadowTarget, records);
        var targetConnector = new FakeShadowTargetConnector();
        var service = new ShadowProjectionService(
            new ConnectorRegistry([new FakeSourceConnector(scenario.SourceNode.Name, records)]),
            new ShadowTargetConnectorRegistry([targetConnector]));
        var directory = CreateTemporaryDirectory();
        try
        {
            var run = await service.ProjectAsync(scenario.Configuration, scenario.Graph, directory, TestContext.Current.CancellationToken);

            Assert.Equal(ProjectionStatus.Failed, run.Status);
            Assert.Null(run.Fingerprint);
            Assert.Single(targetConnector.RecordsFor(run.Id));
            var journalPath = Path.Combine(directory, run.JournalPath.Replace('/', Path.DirectorySeparatorChar));
            var journal = await File.ReadAllTextAsync(journalPath, TestContext.Current.CancellationToken);
            Assert.Contains("produced", journal, StringComparison.Ordinal);
            Assert.Contains("failed", journal, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ProjectionExplicitExclusionIsJournaledWithoutAWrite()
    {
        var records = CreateSourceRecords().Take(1).ToArray();
        var scenario = Scenario(SystemRole.ShadowTarget, records, exclusion: true);
        var targetConnector = new FakeShadowTargetConnector();
        var service = new ShadowProjectionService(
            new ConnectorRegistry([new FakeSourceConnector(scenario.SourceNode.Name, records)]),
            new ShadowTargetConnectorRegistry([targetConnector]));
        var directory = CreateTemporaryDirectory();
        try
        {
            var run = await service.ProjectAsync(scenario.Configuration, scenario.Graph, directory, TestContext.Current.CancellationToken);

            Assert.Equal(ProjectionStatus.Succeeded, run.Status);
            Assert.Equal(1, run.SourceArtifactCount);
            Assert.Equal(0, run.TargetArtifactCount);
            Assert.Empty(targetConnector.RecordsFor(run.Id));
            var journalPath = Path.Combine(directory, run.JournalPath.Replace('/', Path.DirectorySeparatorChar));
            var journal = await File.ReadAllTextAsync(journalPath, TestContext.Current.CancellationToken);
            Assert.Contains("excluded", journal, StringComparison.Ordinal);
            Assert.Contains("source-id-1", journal, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static ScenarioData Scenario(SystemRole targetRole, IReadOnlyCollection<RecordEnvelope> records, bool split = false, bool exclusion = false)
    {
        var sourceEndpoint = new StorageEndpointDefinition(new StorageEndpointId("source-store"), new ConnectorId("fake-source"));
        var targetEndpoint = new StorageEndpointDefinition(new StorageEndpointId("shadow-store"), new ConnectorId("fake-target"));
        var sourceSystem = new SystemDefinition(new SystemId("source-system"), "Synthetic Source", SystemRole.Source, [sourceEndpoint]);
        var targetSystem = new SystemDefinition(new SystemId("target-system"), "Synthetic Destination", targetRole, [targetEndpoint]);
        var root = new RootConfigurationDto(1, new ProjectConfigurationDto("projection-test", "Projection Test"), null, [], null, null, null);
        var loaded = new LoadedProjectConfiguration(root,
            [
                new SystemConfigurationDto(sourceSystem.Id.Value, sourceSystem.Name, "source", [new StorageEndpointConfigurationDto("source-store", "fake-source", [])]),
                new SystemConfigurationDto(targetSystem.Id.Value, targetSystem.Name, targetRole.ToString(), [new StorageEndpointConfigurationDto("shadow-store", "fake-target", [])])
            ], [sourceSystem, targetSystem], [], "canonical-config", "config-hash");
        var sourceNode = new MigrationNode(new MigrationNodeId(Guid.NewGuid()), "source-members", MigrationNodeType.Source,
            "Pension.Member", sourceSystem.Id, sourceEndpoint.Id,
            new ArtifactSelector("table", [new KeyValuePair<string, string>("name", "dbo.MEMBER")], ["MEMBER_ID"]));
        var targets = split
            ? new[] { Target("target-a"), Target("target-b") }
            : exclusion ? Array.Empty<MigrationNode>() : [Target("target-a")];
        MigrationNode Target(string name) => new(new MigrationNodeId(Guid.NewGuid()), name,
            MigrationNodeType.Target, "Pension.Member", targetSystem.Id, targetEndpoint.Id,
            new ArtifactSelector("table", [new KeyValuePair<string, string>("name", "participant")], ["id"]));

        var fields = new[]
        {
            new TransformationFieldDefinition("id", "MEMBER_ID"),
            new TransformationFieldDefinition("name", "FIRST_NM", [new TransformationStep(TransformationStepType.Trim, "1"), new TransformationStep(TransformationStepType.NormalizeString, "1")]),
            new TransformationFieldDefinition("status", "MEMBER_STATUS", [new TransformationStep(TransformationStepType.CodeMap, "1", [new KeyValuePair<string, string>("A", "ACTIVE")])]),
            new TransformationFieldDefinition("amount", "AMOUNT"),
            new TransformationFieldDefinition("optional", "OPTIONAL"),
            new TransformationFieldDefinition("birth_date", "BIRTH_DATE"),
            new TransformationFieldDefinition("normalized_date", "DOB_TEXT", [new TransformationStep(TransformationStepType.NormalizeDate, "1", [new KeyValuePair<string, string>("format", "yyyy-MM-dd")])]),
            new TransformationFieldDefinition("name_parts", "FIRST_NM", [new TransformationStep(TransformationStepType.Trim, "1"), new TransformationStep(TransformationStepType.Split, "1", [new KeyValuePair<string, string>("separator", " ")])]),
            new TransformationFieldDefinition("display", "FIRST_NM", [new TransformationStep(TransformationStepType.Concatenate, "1", [new KeyValuePair<string, string>("fields", "FIRST_NM,MEMBER_STATUS"), new KeyValuePair<string, string>("separator", "|")])])
        };
        var operationType = exclusion ? MigrationOperationType.Exclude : split ? MigrationOperationType.Split : MigrationOperationType.Map;
        var edge = new MigrationEdge(new MigrationEdgeId(Guid.NewGuid()), "member-edge", [sourceNode.Id], targets.Select(node => node.Id),
            new MigrationOperation(operationType, fields: exclusion ? [] : fields, isDestructive: exclusion), "1",
            new RecoveryDefinition(RecoveryMode.Reverse));
        var graph = new MigrationGraph(new MigrationGraphId(Guid.NewGuid()), [sourceNode, .. targets], [edge], "graph-hash", "test-v1");
        return new ScenarioData(loaded, graph, sourceNode);
    }

    private static RecordEnvelope[] CreateSourceRecords(bool duplicateTargetId = false)
    {
        var system = new SystemId("source-system");
        var endpoint = new StorageEndpointId("source-store");
        var connector = new ConnectorId("fake-source");
        return Enumerable.Range(1, 2).Select(index =>
        {
            var id = duplicateTargetId ? 1 : index;
            var artifact = new ArtifactReference(new ArtifactId($"source-id-{index}"), system, endpoint, "row", $"MEMBER_ID={index}");
            return new RecordEnvelope(artifact, "Pension.Member", new Dictionary<string, ValueNode>
            {
                ["MEMBER_ID"] = new IntegerValue(id),
                ["FIRST_NM"] = new StringValue("  Ada Lovelace  "),
                ["MEMBER_STATUS"] = new StringValue("A"),
                ["AMOUNT"] = new DecimalValue(123.4500m),
                ["OPTIONAL"] = new NullValue(),
                ["BIRTH_DATE"] = new DateValue(new DateOnly(1990, 2, 3)),
                ["DOB_TEXT"] = new StringValue("1990-02-03")
            }, new ProvenanceMetadata(connector, endpoint, "dbo.MEMBER", DateTimeOffset.UnixEpoch));
        }).ToArray();
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"proofshift-projection-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed record ScenarioData(LoadedProjectConfiguration Configuration, MigrationGraph Graph, MigrationNode SourceNode);

    private sealed class FakeSourceConnector(string nodeName, IReadOnlyCollection<RecordEnvelope> records) : ISourceConnector
    {
        public ConnectorId Id { get; } = new("fake-source");
        public string Version => "test-v1";

        public Task<SourceInspection> InspectAsync(ConnectorContext context, ArtifactSelector selector, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(context.NodeKey, nodeName, StringComparison.Ordinal))
            {
                throw new ConnectorConfigurationException(ConnectorIssueCodes.MissingConfiguration, "Unexpected fake source node.");
            }

            return Task.FromResult(new SourceInspection(SourceInspectionStatus.Valid));
        }

        public async IAsyncEnumerable<RecordEnvelope> ReadAsync(ConnectorContext context, ArtifactSelector selector, ReadOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            foreach (var record in records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return record;
            }
        }
    }

    private sealed class FakeShadowTargetConnector : IShadowTargetConnector
    {
        private readonly Dictionary<(RunId Run, string Node), List<RecordEnvelope>> _records = [];
        public ConnectorId Id { get; } = new("fake-target");
        public string Version => "test-v1";
        public int PrepareCount { get; private set; }

        public Task PrepareAsync(ShadowTargetContext context, ArtifactSelector selector, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(SystemRole.ShadowTarget, context.Role);
            PrepareCount++;
            return Task.CompletedTask;
        }

        public Task WriteAsync(ShadowWriteRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = (request.Context.RunId, request.NodeKey);
            if (!_records.TryGetValue(key, out var records))
            {
                records = [];
                _records.Add(key, records);
            }

            if (records.Any(record => record.Artifact.Identity == request.Record.Artifact.Identity))
            {
                throw new ProjectionConnectorException("duplicate identity");
            }

            records.Add(request.Record);
            return Task.CompletedTask;
        }

        public Task CompleteAsync(ShadowTargetContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<RecordEnvelope> ReadAsync(ReadRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            var node = request.Context.ConnectorContext.NodeKey;
            if (_records.TryGetValue((request.Context.RunId, node), out var records))
            {
                foreach (var record in records.OrderBy(record => record.Artifact.Identity, StringComparer.Ordinal))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return record;
                }
            }
        }

        public List<RecordEnvelope> RecordsFor(RunId runId) =>
            _records.Where(pair => pair.Key.Run == runId).SelectMany(pair => pair.Value).ToList();
    }
}