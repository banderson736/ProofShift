using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ProofShift.Domain;
using ProofShift.Engine;
using ProofShift.Verification;
using Xunit;

namespace ProofShift.Verification.Tests;

public sealed class VerificationWorkspaceTests
{
    [Fact]
    public void EffectiveDatedIntervalPolicyUsesInclusiveBoundsAndConfiguredContinuity()
    {
        var start = new DateOnly(2024, 1, 1);
        var end = new DateOnly(2024, 1, 31);

        Assert.True(EffectiveDatedIntervalSemantics.IsValid(start, end));
        Assert.True(EffectiveDatedIntervalSemantics.IsValid(start, null));
        Assert.False(EffectiveDatedIntervalSemantics.IsValid(end, start));
        Assert.True(EffectiveDatedIntervalSemantics.Overlaps(false, end, end));
        Assert.True(EffectiveDatedIntervalSemantics.Overlaps(true, null, new DateOnly(2024, 2, 1)));
        Assert.False(EffectiveDatedIntervalSemantics.Overlaps(false, end, new DateOnly(2024, 2, 1)));
        Assert.False(EffectiveDatedIntervalSemantics.HasGap(false, end, new DateOnly(2024, 2, 15)));
        Assert.False(EffectiveDatedIntervalSemantics.HasGap(true, end, new DateOnly(2024, 2, 1)));
        Assert.True(EffectiveDatedIntervalSemantics.HasGap(true, end, new DateOnly(2024, 2, 2)));
    }

    [Fact]
    public void ExecutionPlanMergesDescriptorRequirementsAndExecutionProperties()
    {
        var groupingKey = new VerificationOrderingKey("Test.Person", "member-id", VerificationOrderingRole.Grouping);
        var orderingKey = new VerificationOrderingKey("Test.Person", "effective-date", VerificationOrderingRole.Ordering);
        var lookupKey = new VerificationOrderingKey("Test.Person", "lookup-id", VerificationOrderingRole.Lookup);
        var fieldOption = new RuleOptionDescriptor("memberField", RuleOptionKind.FieldReference, "Required grouping field.");
        var descriptor = new RuleDescriptor("target-presence", "1", "Execution plan test rule.", VerificationScope.Entity,
            [fieldOption], requiredSemanticTypes: ["Test.Person"], requiredSourceFields: ["source-code"],
            requiredTargetFields: ["target-code"], groupingKeys: [groupingKey], orderingKeys: [orderingKey],
            lookupKeys: [lookupKey], partitionExecution: VerificationPartitionExecution.PartitionLocal,
            partitionKey: new VerificationPartitionKeyDefinition(VerificationPartitionBasis.ArtifactIdentity,
                VerificationArtifactRole.ActualTarget),
            fieldRequirements: [new RuleFieldRequirement("memberField", VerificationFieldSide.Target,
                defaultSemanticType: "Test.Person", defaultFields: ["member-id"],
                keyRole: VerificationOrderingRole.Grouping)]);
        var firstProvider = new ExecutionPlanTestProvider(descriptor);
        var secondProvider = new SecondaryTestProvider();
        var providers = new IVerificationRuleProvider[] { firstProvider, secondProvider };
        var registry = new VerificationRuleRegistry(providers);
        var ruleSet = registry.Resolve([new VerificationRuleDefinition(new RuleId("presence"),
                "target-presence", "1", EvidenceSeverity.Error,
                structuredOptions: [new KeyValuePair<string, ValueNode>("memberField", new StringValue("member-id"))]),
            new VerificationRuleDefinition(new RuleId("accounting"), "test-accounting", "1", EvidenceSeverity.Error)]);

        var plan = VerificationExecutionPlan.Create(ruleSet);
        var workset = Assert.Single(plan.Rules, item => item.RuleType == "target-presence");

        Assert.Same(Assert.Single(ruleSet.Rules, rule => rule.Id.Value == "presence"), workset.Rule);
        Assert.Equal("target-presence", workset.RuleType);
        Assert.Equal(VerificationScope.Entity, workset.Scope);
        Assert.Equal(["Test.Person"], workset.RequiredSemanticTypes);
        Assert.Equal(["source-code"], workset.RequiredSourceFields);
        Assert.Equal(["effective-date", "lookup-id", "member-id", "target-code"], workset.RequiredTargetFields);
        Assert.Contains(groupingKey, workset.GroupingKeys);
        Assert.Contains(orderingKey, workset.OrderingKeys);
        Assert.Contains(lookupKey, workset.LookupKeys);
        Assert.Equal(3, plan.RequiredOrderingKeys.Count);
        Assert.Equal(["source-code"], plan.GetRequiredFields(VerificationArtifactRole.Source, "Test.Person"));
        Assert.Equal(["effective-date", "lookup-id", "member-id", "target-code"],
            plan.GetRequiredFields(VerificationArtifactRole.ExpectedTarget, "Test.Person"));
        Assert.Equal("artifact_order_key_idx", Assert.Single(plan.RequiredIndexes).Name);
        Assert.Equal(VerificationPartitionExecution.PartitionLocal, workset.PartitionExecution);
        Assert.Empty(workset.ResolvedPartitionKeyFields);

        var reversedRuleSet = new VerificationRuleRegistry(providers.Reverse()).Resolve(
        [
            new VerificationRuleDefinition(new RuleId("accounting"), "test-accounting", "1", EvidenceSeverity.Error),
            new VerificationRuleDefinition(new RuleId("presence"), "target-presence", "1", EvidenceSeverity.Error,
                structuredOptions: [new KeyValuePair<string, ValueNode>("memberField", new StringValue("member-id"))])
        ]);
        var reversedPlan = VerificationExecutionPlan.Create(reversedRuleSet);
        Assert.Equal(plan.Rules.Select(item => item.Rule.Id.Value), reversedPlan.Rules.Select(item => item.Rule.Id.Value));
        Assert.Equal(plan.RequiredOrderingKeys, reversedPlan.RequiredOrderingKeys);
        Assert.Equal(plan.GetRequiredFields(VerificationArtifactRole.ExpectedTarget, "Test.Person"),
            reversedPlan.GetRequiredFields(VerificationArtifactRole.ExpectedTarget, "Test.Person"));
        Assert.Equal(plan.RequiredIndexes, reversedPlan.RequiredIndexes);
    }

    [Fact]
    public void ExecutionPlanRejectsUnboundFieldReferenceOptions()
    {
        var descriptor = new RuleDescriptor("target-presence", "1", "Incomplete descriptor.", VerificationScope.Entity,
            [new RuleOptionDescriptor("attribute", RuleOptionKind.FieldReference, "Unbound field.")]);
        var ruleSet = new VerificationRuleRegistry([new ExecutionPlanTestProvider(descriptor)]).Resolve(
            [new VerificationRuleDefinition(new RuleId("presence"), "target-presence", "1", EvidenceSeverity.Error)]);

        var error = Assert.Throws<VerificationRuleException>(() => VerificationExecutionPlan.Create(ruleSet));

        Assert.Equal("PSRULE008", error.Code);
        Assert.Contains("attribute", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExecutionPlanRejectsUnprovenGroupingPartitionRouting()
    {
        var descriptor = new RuleDescriptor("target-presence", "1", "Grouping partition test.", VerificationScope.Entity,
            [new RuleOptionDescriptor("owner", RuleOptionKind.FieldReference, "Owner key.")],
            fieldRequirements: [new RuleFieldRequirement("owner", VerificationFieldSide.Target, defaultFields: ["owner_id"])],
            partitionExecution: VerificationPartitionExecution.PartitionLocal,
            partitionKey: new VerificationPartitionKeyDefinition(VerificationPartitionBasis.GroupingKey,
                VerificationArtifactRole.ActualTarget, ["owner"]));
        var ruleSet = new VerificationRuleRegistry([new ExecutionPlanTestProvider(descriptor)]).Resolve(
            [new VerificationRuleDefinition(new RuleId("grouped"), "target-presence", "1", EvidenceSeverity.Error)]);

        var workset = Assert.Single(VerificationExecutionPlan.Create(ruleSet).Rules);

        Assert.Equal(VerificationPartitionExecution.Global, workset.PartitionExecution);
        Assert.Contains("graph-proven single-field target identity", workset.PartitionFallbackReason, StringComparison.Ordinal);
    }

    [Fact]
    public void ExecutionPlanAcceptsGroupingKeyProvenEqualToTargetSelectorIdentity()
    {
        var target = new MigrationNode(new MigrationNodeId(Guid.NewGuid()), "target", MigrationNodeType.Target,
            "Test.Person", new SystemId("shadow"), new StorageEndpointId("postgres"),
            new ArtifactSelector("table", identityFields: ["member_id"]));
        var graph = new MigrationGraph(new MigrationGraphId(Guid.NewGuid()), [target], [], new string('a', 64), "test-v1");
        var descriptor = new RuleDescriptor("target-presence", "1", "Proven grouping locality.", VerificationScope.Entity,
            [new RuleOptionDescriptor("businessKey", RuleOptionKind.FieldReference, "Identity key."),
                new RuleOptionDescriptor("targetNode", RuleOptionKind.Text, "Target graph node.")],
            fieldRequirements: [new RuleFieldRequirement("businessKey", VerificationFieldSide.Target,
                defaultSemanticType: "Test.Person", defaultFields: ["member_id"], keyRole: VerificationOrderingRole.Grouping)],
            partitionExecution: VerificationPartitionExecution.PartitionLocal,
            partitionKey: new VerificationPartitionKeyDefinition(VerificationPartitionBasis.GroupingKey,
                VerificationArtifactRole.ActualTarget, ["businessKey"]));
        var ruleSet = new VerificationRuleRegistry([new ExecutionPlanTestProvider(descriptor)]).Resolve(
            [new VerificationRuleDefinition(new RuleId("presence"), "target-presence", "1", EvidenceSeverity.Error,
                structuredOptions: [new KeyValuePair<string, ValueNode>("targetNode", new StringValue("target"))])]);

        var executionPlan = VerificationExecutionPlan.Create(ruleSet, graph);
        var workset = Assert.Single(executionPlan.Rules);
        var partitionContractFingerprint = executionPlan.PartitionContractFingerprint;

        Assert.Equal(VerificationPartitionExecution.PartitionLocal, workset.PartitionExecution);
        Assert.Equal(VerificationPartitionBasis.GroupingKey, workset.PartitionKey!.Basis);
        Assert.Equal([new VerificationOrderingKey("Test.Person", "member_id", VerificationOrderingRole.Grouping)],
            workset.ResolvedPartitionKeyFields);

        var referenceWorkset = VerificationService.ApplyExecutionMode(workset,
            VerificationRuleExecutionMode.GlobalRuleReference);
        var experimentalWorkset = VerificationService.ApplyExecutionMode(workset,
            VerificationRuleExecutionMode.PartitionLocalExperimental);

        Assert.Equal(VerificationPartitionExecution.Global, referenceWorkset.PartitionExecution);
        Assert.Same(workset, experimentalWorkset);
        Assert.Equal(VerificationPartitionExecution.PartitionLocal, workset.PartitionExecution);
        Assert.Equal(partitionContractFingerprint, executionPlan.PartitionContractFingerprint);
    }

    [Fact]
    public void ExecutionPlanRejectsPartialModeWithoutFindingMerger()
    {
        var descriptor = new RuleDescriptor("test-accounting", "1", "Missing merge contract.", VerificationScope.Accounting,
            partitionExecution: VerificationPartitionExecution.PartitionPartialWithGlobalMerge,
            partitionKey: new VerificationPartitionKeyDefinition(VerificationPartitionBasis.ArtifactIdentity,
                VerificationArtifactRole.ActualTarget));
        var providers = new IVerificationRuleProvider[]
        {
            new SecondaryTestProvider(descriptor)
        };
        var ruleSet = new VerificationRuleRegistry(providers).Resolve(
            [new VerificationRuleDefinition(new RuleId("accounting"), "test-accounting", "1", EvidenceSeverity.Error)]);

        var error = Assert.Throws<VerificationRuleException>(() => VerificationExecutionPlan.Create(ruleSet));

        Assert.Equal("PSRULE012", error.Code);
        Assert.Contains("without a deterministic global finding merger", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExecutionPlanPreservesSelectorIdentityFieldsByArtifactRole()
    {
        var source = new MigrationNode(new MigrationNodeId(Guid.NewGuid()), "source", MigrationNodeType.Source,
            "Test.Person", new SystemId("source-system"), new StorageEndpointId("source-endpoint"),
            new ArtifactSelector("relational-table", [], ["source_member_id"]));
        var target = new MigrationNode(new MigrationNodeId(Guid.NewGuid()), "target", MigrationNodeType.Target,
            "Test.Person", new SystemId("target-system"), new StorageEndpointId("target-endpoint"),
            new ArtifactSelector("relational-table", [], ["target_member_id"]));
        var graph = new MigrationGraph(new MigrationGraphId(Guid.NewGuid()), [source, target], [], new string('a', 64), "test-v1");
        var descriptor = new RuleDescriptor("target-presence", "1", "Identity workset test.", VerificationScope.Entity);
        var ruleSet = new VerificationRuleRegistry([new ExecutionPlanTestProvider(descriptor)]).Resolve(
            [new VerificationRuleDefinition(new RuleId("presence"), "target-presence", "1", EvidenceSeverity.Error)]);

        var plan = VerificationExecutionPlan.Create(ruleSet, graph);

        Assert.Equal(["source_member_id"], plan.GetRequiredFields(VerificationArtifactRole.Source, "Test.Person"));
        Assert.Equal(["target_member_id"], plan.GetRequiredFields(VerificationArtifactRole.ExpectedTarget, "Test.Person"));
        Assert.Equal(["target_member_id"], plan.GetRequiredFields(VerificationArtifactRole.ActualTarget, "Test.Person"));
    }

    private sealed class ExecutionPlanTestProvider : IVerificationRuleProvider
    {
        public string Id => "test.verification.execution-plan";
        public string Version => "1";
        public IReadOnlyCollection<VerificationRuleFactory> RuleFactories { get; }

        public ExecutionPlanTestProvider(RuleDescriptor descriptor) =>
            RuleFactories = [new VerificationRuleFactory("target-presence",
                definition => new TargetPresenceRule(definition), descriptor)];

    }

    private sealed class SecondaryTestProvider : IVerificationRuleProvider
    {
        public string Id => "test.verification.secondary";
        public string Version => "1";
        public IReadOnlyCollection<VerificationRuleFactory> RuleFactories { get; }

        public SecondaryTestProvider() : this(
            new RuleDescriptor("test-accounting", "1", "Secondary plan rule.", VerificationScope.Accounting)) { }

        public SecondaryTestProvider(RuleDescriptor descriptor) =>
            RuleFactories = [new VerificationRuleFactory("test-accounting", definition => new SourceDispositionRule(definition), descriptor)];
    }

    [Fact]
    public async Task StablePartitionAssignmentPreservesTypedKeysAndConservativeCapabilities()
    {
        var vector = string.Join(',', Enum.GetValues<VerificationPartitionBasis>().Select(basis =>
            VerificationPartitioning.Assign(basis, "generic-scope", [new StringValue("owner-1"), new DecimalValue(123456789.0123456789m)], 8)));
        var childOutput = Environment.GetEnvironmentVariable("PROOFSHIFT_PARTITION_VECTOR_CHILD");
        if (!string.IsNullOrWhiteSpace(childOutput))
        {
            await File.WriteAllTextAsync(childOutput, vector, TestContext.Current.CancellationToken);
            return;
        }
        foreach (var basis in Enum.GetValues<VerificationPartitionBasis>())
        {
            var keys = new ValueNode[] { new StringValue("owner-1"), new DecimalValue(123456789.0123456789m), new DateValue(new DateOnly(2026, 1, 2)) };
            foreach (var count in new[] { 1, 4, 8 })
            {
                var first = VerificationPartitioning.Assign(basis, "generic-scope", keys, count);
                Assert.Equal(first, VerificationPartitioning.Assign(basis, "generic-scope", keys.ToArray(), count));
                Assert.InRange(first, 0, count - 1);
            }
        }
        Assert.Equal(0, VerificationPartitioning.Assign(VerificationPartitionBasis.ArtifactIdentity, "scope", [new StringValue("id")], 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => VerificationPartitioning.Assign(VerificationPartitionBasis.GroupingKey, "scope", [], 0));
        var start = new System.Diagnostics.ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ProofShift.sln"))) directory = directory.Parent;
        Assert.NotNull(directory);
        start.WorkingDirectory = directory.FullName;
        start.ArgumentList.Add("test");
        start.ArgumentList.Add("--project");
        start.ArgumentList.Add(Path.Combine(directory.FullName, "tests", "ProofShift.Verification.Tests", "ProofShift.Verification.Tests.csproj"));
        start.ArgumentList.Add("--no-build");
        start.ArgumentList.Add("--no-restore");
        start.ArgumentList.Add("--filter-method");
        start.ArgumentList.Add("*StablePartitionAssignmentPreservesTypedKeysAndConservativeCapabilities*");
        var vectorPath = Path.Combine(Path.GetTempPath(), $"proofshift-partition-vector-{Guid.NewGuid():N}.txt");
        start.Environment["PROOFSHIFT_PARTITION_VECTOR_CHILD"] = vectorPath;
        try
        {
            using var process = System.Diagnostics.Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            var errors = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.True(process.ExitCode == 0, await output + await errors);
            Assert.Equal(vector, await File.ReadAllTextAsync(vectorPath, TestContext.Current.CancellationToken));
        }
        finally { if (File.Exists(vectorPath)) File.Delete(vectorPath); }
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(4, 8)]
    [InlineData(8, 3)]
    public async Task PartitionWorkerCountMustBeBoundedByConfiguredPartitions(int partitionCount, int workerCount)
    {
        var root = Path.Combine(Path.GetTempPath(), $"proofshift-partition-workers-{Guid.NewGuid():N}");
        try
        {
            await using var workspace = await SqliteVerificationWorkspace.CreateAsync(root, TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => workspace.ConfigurePartitionsAsync(
                partitionCount, TestContext.Current.CancellationToken, workerCount));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(8)]
    public async Task PartitionWorkerCountCanBeConfiguredBeforePopulation(int workerCount)
    {
        var root = Path.Combine(Path.GetTempPath(), $"proofshift-partition-workers-valid-{Guid.NewGuid():N}");
        try
        {
            await using var workspace = await SqliteVerificationWorkspace.CreateAsync(root, TestContext.Current.CancellationToken);
            await workspace.ConfigurePartitionsAsync(8, TestContext.Current.CancellationToken, workerCount);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(6)]
    [InlineData(8)]
    public async Task QueuedPartitionWritesDrainAndRespectWorkerBound(int workerCount)
    {
        var root = Path.Combine(Path.GetTempPath(), $"proofshift-partition-worker-drain-{Guid.NewGuid():N}");
        const int recordCount = 512;
        var recorder = new PerformanceRecorder("partition worker drain");
        try
        {
            await using var workspace = await SqliteVerificationWorkspace.CreateAsync(root, recorder,
                TestContext.Current.CancellationToken);
            await workspace.ConfigurePartitionsAsync(8, TestContext.Current.CancellationToken, maxPartitionWorkers: workerCount);
            for (var index = 0; index < recordCount; index++)
            {
                var identity = $"actual-{index:D4}";
                await workspace.QueueTargetObservationAsync("target", Record(identity, "target", "members", identity,
                    "Generic.Member", "status", "ACTIVE"), TestContext.Current.CancellationToken);
            }

            await workspace.FlushPartitionWorkAsync(TestContext.Current.CancellationToken);
            Assert.Equal(recordCount, workspace.ActualTargetCount);
            var actual = await CollectAsync(workspace.ReadArtifactRecordsAsync(VerificationArtifactRole.ActualTarget,
                "target", "Generic.Member", TestContext.Current.CancellationToken));
            Assert.Equal(recordCount, actual.Count);
            await workspace.DisposeAsync();

            var run = recorder.Complete();
            var phase = Assert.Single(run.Stages, stage => stage.Name == "partition worker phase actual-target ingestion");
            double Metric(string name) => Assert.Single(phase.Measurements, measurement => measurement.Name == name).Value;
            Assert.Equal(workerCount, Metric("configuredWorkers"));
            Assert.InRange(Metric("maximumSimultaneousWorkers"), 1, workerCount);
            Assert.Equal(recordCount, Metric("queuedOperations"));
            Assert.Equal(recordCount, Metric("completedOperations"));
            var partitions = run.Stages.Where(stage => stage.Name.StartsWith("partition worker partition-", StringComparison.Ordinal) &&
                stage.Name.EndsWith("actual-target ingestion", StringComparison.Ordinal)).ToArray();
            Assert.NotEmpty(partitions);
            Assert.Equal(recordCount, partitions.Sum(stage => (long)stage.ArtifactCount));
            Assert.All(partitions, stage => Assert.True(Assert.Single(stage.Measurements,
                measurement => measurement.Name == "partitionIndex").Value is >= 0 and < 8));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(8)]
    public async Task PartitionLocalScratchPreservesGlobalDuplicatesGroupsAndMultiSourceAncestry(int partitions)
    {
        var root = Path.Combine(Path.GetTempPath(), $"proofshift-partition-{Guid.NewGuid():N}");
        try
        {
            var sourceOne = Record("source-one", "source", "members", "source-one", "Generic.Member", "status", "ACTIVE");
            var sourceTwo = Record("source-two", "source", "members", "source-two", "Generic.Member", "status", "ACTIVE");
            var first = RecordWithValues("target-one", "target", "members", "target-one", "Generic.Member",
                [new("owner", new StringValue("same-owner")), new("amount", new DecimalValue(123456789.0123456789m)),
                 new("event", new DateValue(new DateOnly(2026, 1, 2)))]);
            var secondId = "target-two";
            if (partitions > 1)
            {
                var firstPartition = VerificationPartitioning.AssignArtifactIdentity("target", first.Artifact.Identity, partitions);
                secondId = Enumerable.Range(0, 100).Select(index => $"target-other-{index}").First(id =>
                    VerificationPartitioning.AssignArtifactIdentity("target", id, partitions) != firstPartition);
            }
            var second = RecordWithValues(secondId, "target", "members", secondId, "Generic.Member",
                [new("owner", new StringValue("same-owner")), new("amount", new DecimalValue(0.0000000001m)),
                 new("event", new DateValue(new DateOnly(2026, 1, 3)))]);
            if (partitions > 1)
                Assert.NotEqual(VerificationPartitioning.AssignArtifactIdentity("target", first.Artifact.Identity, partitions),
                    VerificationPartitioning.AssignArtifactIdentity("target", second.Artifact.Identity, partitions));
            var edge = new MigrationEdge(new MigrationEdgeId(Guid.NewGuid()), "generic-edge", [new MigrationNodeId(Guid.NewGuid())],
                [new MigrationNodeId(Guid.NewGuid())], new MigrationOperation(MigrationOperationType.Copy), "1", new RecoveryDefinition(RecoveryMode.Reverse));
            await using var workspace = await SqliteVerificationWorkspace.CreateAsync(root, TestContext.Current.CancellationToken);
            await workspace.ConfigurePartitionsAsync(partitions, TestContext.Current.CancellationToken);
            await workspace.AddSourceArtifactAsync("source", sourceOne, TestContext.Current.CancellationToken);
            await workspace.AddSourceArtifactAsync("source", sourceTwo, TestContext.Current.CancellationToken);
            await workspace.AddExpectedTargetAsync("target", first, "source", sourceOne, edge, TestContext.Current.CancellationToken);
            await workspace.AddExpectedTargetAsync("target", first, "source", sourceTwo, edge, TestContext.Current.CancellationToken);
            await workspace.AddExpectedTargetAsync("target", second, "source", sourceTwo, edge, TestContext.Current.CancellationToken);
            await workspace.AddTargetObservationAsync("target", first, TestContext.Current.CancellationToken);
            await workspace.AddTargetObservationAsync("target", second, TestContext.Current.CancellationToken);
            await workspace.AddJournalEntryAsync(new VerificationJournalEntry("produced", "target", first.Artifact,
                [new VerificationGraphArtifact("source", sourceOne.Artifact), new VerificationGraphArtifact("source", sourceTwo.Artifact)],
                edge.Id, edge.Name, edge.Version, null), TestContext.Current.CancellationToken);
            await workspace.AddJournalEntryAsync(new VerificationJournalEntry("produced", "target", second.Artifact,
                [new VerificationGraphArtifact("source", sourceTwo.Artifact)], edge.Id, edge.Name, edge.Version, null), TestContext.Current.CancellationToken);
            var partitionedComparisonCount = 0;
            var partitionedPresenceCount = 0;
            for (var partitionIndex = 0; partitionIndex < partitions; partitionIndex++)
            {
                workspace.SetRuleEvaluationPartition(partitionIndex);
                var expectedPartition = await CollectAsync(workspace.ReadArtifactRecordsAsync(VerificationArtifactRole.ExpectedTarget,
                    "target", "Generic.Member", TestContext.Current.CancellationToken));
                var actualPartition = await CollectAsync(workspace.ReadArtifactRecordsAsync(VerificationArtifactRole.ActualTarget,
                    "target", "Generic.Member", TestContext.Current.CancellationToken));
                Assert.Equal(expectedPartition.Select(record => record.Artifact.Identity).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal),
                    actualPartition.Select(record => record.Artifact.Identity).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
                partitionedComparisonCount += (await CollectAsync(workspace.ReadAttributeComparisonsAsync(TestContext.Current.CancellationToken))).Count;
                partitionedPresenceCount += (await CollectAsync(workspace.ReadMaterializedJournalTargetsAsync(TestContext.Current.CancellationToken))).Count;
            }
            workspace.SetRuleEvaluationPartition(null);
            var comparisons = await CollectAsync(workspace.ReadAttributeComparisonsAsync(TestContext.Current.CancellationToken));
            Assert.Equal(9, comparisons.Count);
            Assert.Equal(comparisons.Count, partitionedComparisonCount);
            Assert.Equal(2, partitionedPresenceCount);
            Assert.All(comparisons, comparison => Assert.True(comparison.Matches));
            var validation = await workspace.ValidateJournalEntriesAsync(TestContext.Current.CancellationToken);
            Assert.Equal(2, validation.ProducedEntryCount);
            Assert.Equal(new VerificationJournalValidationResult(2, true, true, true), validation);
            Assert.Empty(await CollectAsync(workspace.ReadTargetsWithoutLineageAsync(TestContext.Current.CancellationToken)));
            var records = await CollectAsync(workspace.ReadArtifactRecordsAsync(VerificationArtifactRole.ActualTarget,
                "target", "Generic.Member", TestContext.Current.CancellationToken, ["owner", "event"]));
            Assert.Equal(2, records.Count);
            Assert.Equal([first.Artifact.Id, second.Artifact.Id], records.Select(record => record.Artifact.Id));
            Assert.Equal(123456789.0123456790m, records.Sum(record => Assert.IsType<DecimalValue>(record.Values["amount"]).Value));
            Assert.All(records, record => Assert.Equal(new StringValue("same-owner"), record.Values["owner"]));
            var duplicateArtifactId = Enumerable.Range(0, 1000).Select(index => $"duplicate-artifact-{index}").First(id =>
                partitions == 1 || VerificationPartitioning.AssignArtifactIdentity("target", first.Artifact.Id.Value, partitions) !=
                VerificationPartitioning.AssignArtifactIdentity("target", id, partitions));
            var duplicateIdRecord = RecordWithValues(duplicateArtifactId, "target", "members", first.Artifact.Identity,
                "Generic.Member", [new("owner", new StringValue("same-owner")), new("amount", new DecimalValue(1m)),
                    new("event", new DateValue(new DateOnly(2026, 1, 4)))]);
            var identityPartition = VerificationPartitioning.AssignArtifactIdentity("target", first.Artifact.Identity, partitions);
            Assert.Equal(identityPartition, VerificationPartitioning.AssignArtifactIdentity("target", duplicateIdRecord.Artifact.Identity, partitions));
            if (partitions > 1)
                Assert.NotEqual(VerificationPartitioning.AssignArtifactIdentity("target", first.Artifact.Id.Value, partitions),
                    VerificationPartitioning.AssignArtifactIdentity("target", duplicateIdRecord.Artifact.Id.Value, partitions));
            await workspace.AddTargetObservationAsync("target", duplicateIdRecord, TestContext.Current.CancellationToken);
            workspace.SetRuleEvaluationPartition(identityPartition);
            var duplicate = Assert.Single(await CollectAsync(workspace.ReadDuplicateTargetFactsAsync(TestContext.Current.CancellationToken)));
            workspace.SetRuleEvaluationPartition(null);
            Assert.Equal(duplicateIdRecord.Artifact.Id, duplicate.Artifact.Id);
            Assert.NotEqual(first.Artifact.Id, duplicate.Artifact.Id);
            Assert.Equal(2, duplicate.ActualCount);
            Assert.Equal(3, workspace.ActualTargetCount);
            Assert.True(await workspace.ContainsFieldValueAsync(VerificationArtifactRole.ActualTarget, "target", "Generic.Member",
                "owner", new StringValue("same-owner"), TestContext.Current.CancellationToken));
            Assert.False(await workspace.ContainsFieldValueAsync(VerificationArtifactRole.ActualTarget, "target", "Generic.Member",
                "owner", new StringValue("missing-owner"), TestContext.Current.CancellationToken));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    public async Task PartitionCancellationAndFailureCleanScratchWithoutCompletedState(int partitions)
    {
        var root = Path.Combine(Path.GetTempPath(), $"proofshift-partition-failure-{Guid.NewGuid():N}");
        try
        {
            var workspace = await SqliteVerificationWorkspace.CreateAsync(root, TestContext.Current.CancellationToken);
            await workspace.ConfigurePartitionsAsync(partitions, TestContext.Current.CancellationToken, maxPartitionWorkers: 2);
            var record = Record("actual", "target", "members", "actual", "Generic.Member", "status", "ACTIVE");
            await workspace.QueueTargetObservationAsync("target", record, TestContext.Current.CancellationToken);
            await workspace.FlushPartitionWorkAsync(TestContext.Current.CancellationToken);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => workspace.QueueTargetObservationAsync("target", record, cancelled.Token));
            await workspace.DisposeAsync();
            Assert.Empty(Directory.GetDirectories(root));
            await using var interrupted = await SqliteVerificationWorkspace.CreateAsync(root, TestContext.Current.CancellationToken);
            await interrupted.ConfigurePartitionsAsync(partitions, TestContext.Current.CancellationToken, maxPartitionWorkers: 2);
            var partitionPath = Directory.GetFiles(root, "partition-000.db", SearchOption.AllDirectories).Single();
            await using (var breakPartition = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = partitionPath, Pooling = false }.ToString()))
            {
                await breakPartition.OpenAsync(TestContext.Current.CancellationToken);
                await using var command = breakPartition.CreateCommand();
                command.CommandText = "DROP TABLE actual_targets";
                await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }
            var failingId = Enumerable.Range(0, 1000).Select(index => $"broken-{index}").First(id =>
                VerificationPartitioning.AssignArtifactIdentityHash("target",
                    Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(id))).ToLowerInvariant(), partitions) == 0);
            await interrupted.QueueTargetObservationAsync("target",
                Record(failingId, "target", "members", failingId, "Generic.Member", "status", "ACTIVE"), TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<SqliteException>(() => interrupted.FlushPartitionWorkAsync(TestContext.Current.CancellationToken));
            await interrupted.DisposeAsync();
            Assert.Empty(Directory.GetDirectories(root));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task PartitionExecutionManifestIsDeterministicAndContainsNoArtifactValues()
    {
        var root = Path.Combine(Path.GetTempPath(), $"proofshift-partition-manifest-{Guid.NewGuid():N}");
        try
        {
            async Task<(string Fingerprint, string Json)> RunOnceAsync()
            {
                var recorder = new PerformanceRecorder("partition manifest determinism");
                await using var workspace = await SqliteVerificationWorkspace.CreateAsync(root, recorder, TestContext.Current.CancellationToken);
                await workspace.ConfigurePartitionsAsync(4, TestContext.Current.CancellationToken);
                await workspace.AddTargetObservationAsync("target", Record("private-artifact-id", "target", "members",
                    "private-identity", "Generic.Member", "status", "ACTIVE"), TestContext.Current.CancellationToken);
                Assert.Single(await CollectAsync(workspace.ReadArtifactRecordsAsync(VerificationArtifactRole.ActualTarget,
                    "target", "Generic.Member", TestContext.Current.CancellationToken)));
                await workspace.DisposeAsync();
                var path = Directory.GetFiles(root, "*-partition-manifest.json").Order(StringComparer.Ordinal).Last();
                var json = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
                using var manifest = JsonDocument.Parse(json);
                Assert.Equal(4, manifest.RootElement.GetProperty("partitionCount").GetInt32());
                Assert.Equal(VerificationPartitioning.Version, manifest.RootElement.GetProperty("hashVersion").GetString());
                Assert.Equal(4, manifest.RootElement.GetProperty("partitions").GetArrayLength());
                Assert.Equal(1, manifest.RootElement.GetProperty("partitions").EnumerateArray()
                    .Sum(partition => partition.GetProperty("actualArtifacts").GetInt64()));
                Assert.DoesNotContain("private-artifact-id", json, StringComparison.Ordinal);
                Assert.DoesNotContain("private-identity", json, StringComparison.Ordinal);
                return (manifest.RootElement.GetProperty("fingerprint").GetString()!, json);
            }

            var first = await RunOnceAsync();
            var second = await RunOnceAsync();
            Assert.Equal(first.Fingerprint, second.Fingerprint);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task RuleScanTelemetryCountsPartialReadsAndTypedDecodesWithoutChangingRows()
    {
        var root = Path.Combine(Path.GetTempPath(), $"proofshift-scan-counts-{Guid.NewGuid():N}");
        try
        {
            var recorder = new PerformanceRecorder("partial scan counts");
            await using var workspace = await SqliteVerificationWorkspace.CreateAsync(root, recorder, TestContext.Current.CancellationToken);
            await workspace.AddSourceArtifactAsync("source", Record("one", "source", "members", "one", "Generic.Member", "status", "ACTIVE"), TestContext.Current.CancellationToken);
            await workspace.AddSourceArtifactAsync("source", Record("two", "source", "members", "two", "Generic.Member", "status", "ACTIVE"), TestContext.Current.CancellationToken);
            workspace.SetRuleScanContext("test-rule");
            await using (var scan = workspace.ReadArtifactRecordsAsync(VerificationArtifactRole.Source, "source", "Generic.Member",
                TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken))
            {
                Assert.True(await scan.MoveNextAsync());
                Assert.Equal(new StringValue("ACTIVE"), scan.Current.Values["status"]);
            }
            Assert.True(await workspace.ContainsFieldValueAsync(VerificationArtifactRole.Source, "source", "Generic.Member",
                "status", new StringValue("ACTIVE"), TestContext.Current.CancellationToken));
            Assert.False(await workspace.ContainsFieldValueAsync(VerificationArtifactRole.Source, "source", "Generic.Member",
                "status", new StringValue("MISSING"), TestContext.Current.CancellationToken));
            workspace.SetRuleScanContext(null);
            await workspace.DisposeAsync();
            var performance = recorder.Complete();
            var stage = Assert.Single(performance.Stages, item => item.Name == "verification rule scan test-rule Source Generic.Member");
            Assert.Equal(1, stage.ArtifactCount);
            foreach (var name in new[] { "scanExecutions", "rowsReturned", "typedRecordDecodes", "fieldValueDecodes", "partialScans" })
                Assert.Equal(1, Assert.Single(stage.Measurements, measurement => measurement.Name == name).Value);
            Assert.Equal(0, Assert.Single(stage.Measurements, measurement => measurement.Name == "completedScans").Value);
            var lookup = Assert.Single(performance.Stages, item => item.Name == "verification rule lookup test-rule");
            Assert.Equal(1, lookup.ArtifactCount);
            Assert.Equal(2, Assert.Single(lookup.Measurements, measurement => measurement.Name == "lookupQueryExecutions").Value);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ExpectedWorksetReuseIsImmutableAndActualOverlaysStayIndependent()
    {
        var root = Path.Combine(Path.GetTempPath(), $"proofshift-expected-reuse-{Guid.NewGuid():N}");
        try
        {
            await using var scope = new VerificationExpectedWorksetScope(root);
            var descriptor = new RuleDescriptor("target-presence", "1", "Expected reuse fixture.", VerificationScope.Entity,
                requiredSourceFields: ["status"], requiredTargetFields: ["status"],
                groupingKeys: [new VerificationOrderingKey("Generic.Member", "status", VerificationOrderingRole.Grouping)]);
            var rules = new VerificationRuleRegistry([new ExecutionPlanTestProvider(descriptor)]).Resolve(
                [new VerificationRuleDefinition(new RuleId("presence"), "target-presence", "1", EvidenceSeverity.Error)]);
            var plan = VerificationExecutionPlan.Create(rules);
            var key = new VerificationExpectedWorksetKey("checkpoint", "manifest", "source", "config", "graph", plan.ExpectedRequirementsFingerprint, "test-runtime");
            var source = Record("source", "source", "members", "source-identity", "Generic.Member", "status", "ACTIVE");
            var expected = Record("target", "target", "members", "target-identity", "Generic.Member", "status", "ACTIVE");
            var defective = Record("target", "target", "members", "target-identity", "Generic.Member", "status", "INACTIVE");
            var edge = new MigrationEdge(new MigrationEdgeId(Guid.NewGuid()), "expected-map", [new MigrationNodeId(Guid.NewGuid())],
                [new MigrationNodeId(Guid.NewGuid())], new MigrationOperation(MigrationOperationType.Copy), "1", new RecoveryDefinition(RecoveryMode.Reverse));
            var cache = await scope.GetOrBuildAsync(key, plan, async (workspace, token) =>
            {
                await workspace.AddSourceArtifactAsync("source", source, token);
                await workspace.AddExpectedTargetAsync("target", expected, "source", source, edge, token);
            }, null, TestContext.Current.CancellationToken);
            var reused = await scope.GetOrBuildAsync(key, plan, (_, _) => throw new InvalidOperationException("Must not rebuild."), null, TestContext.Current.CancellationToken);
            Assert.Same(cache, reused);
            Assert.Equal(1, scope.BuildCount);
            Assert.Equal(1, scope.ReuseCount);
            Assert.Equal(VerificationExpectedWorksetState.Complete, cache.State);
            await using var first = await SqliteVerificationWorkspace.CreateAsync(root, null, plan, TestContext.Current.CancellationToken);
            await using var second = await SqliteVerificationWorkspace.CreateAsync(root, null, plan, TestContext.Current.CancellationToken);
            await first.AttachExpectedAsync(cache, key, TestContext.Current.CancellationToken);
            await second.AttachExpectedAsync(cache, key, TestContext.Current.CancellationToken);
            await first.AddTargetObservationAsync("target", expected, TestContext.Current.CancellationToken);
            await second.AddTargetObservationAsync("target", defective, TestContext.Current.CancellationToken);
            foreach (var workspace in new[] { first, second })
            {
                var record = Assert.Single(await CollectAsync(workspace.ReadArtifactRecordsByKeysAsync(VerificationArtifactRole.ExpectedTarget,
                    "target", "Generic.Member", [new VerificationOrderingKey("Generic.Member", "status", VerificationOrderingRole.Grouping)], TestContext.Current.CancellationToken)));
                Assert.Equal(new StringValue("ACTIVE"), record.Values["status"]);
                Assert.Empty(await CollectAsync(workspace.ReadTargetsWithoutGraphDerivedLineageAsync(TestContext.Current.CancellationToken)));
                Assert.Single(await CollectAsync(workspace.ReadGraphDerivedSourceFactsAsync(TestContext.Current.CancellationToken)));
                await Assert.ThrowsAsync<InvalidOperationException>(() => workspace.AddSourceArtifactAsync("source", source, TestContext.Current.CancellationToken));
                await Assert.ThrowsAsync<InvalidOperationException>(() => workspace.AddExpectedTargetAsync("target", expected, "source", source, edge, TestContext.Current.CancellationToken));
            }
            Assert.Equal(new StringValue("ACTIVE"), Assert.Single(await CollectAsync(first.ReadArtifactRecordsAsync(VerificationArtifactRole.ActualTarget,
                "target", "Generic.Member", TestContext.Current.CancellationToken))).Values["status"]);
            Assert.Equal(new StringValue("INACTIVE"), Assert.Single(await CollectAsync(second.ReadArtifactRecordsAsync(VerificationArtifactRole.ActualTarget,
                "target", "Generic.Member", TestContext.Current.CancellationToken))).Values["status"]);
            await cache.ValidateAsync(key, TestContext.Current.CancellationToken);
            await using var mismatch = await SqliteVerificationWorkspace.CreateAsync(root, null, plan, TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<InvalidDataException>(() => mismatch.AttachExpectedAsync(cache, key with { GraphHash = "changed-graph" }, TestContext.Current.CancellationToken));
            foreach (var changedKey in new[]
            {
                key with { CheckpointId = "changed-checkpoint" }, key with { CheckpointManifestHash = "changed-manifest" },
                key with { SourceFingerprint = "changed-source" }, key with { ConfigurationHash = "changed-config" },
                key with { RuntimeVersion = "changed-normalization" }, key with { RequirementsFingerprint = "changed-fields" }
            })
                await Assert.ThrowsAsync<InvalidDataException>(() => mismatch.AttachExpectedAsync(cache, changedKey, TestContext.Current.CancellationToken));
            Assert.Equal(VerificationExpectedWorksetState.Complete, cache.State);
            await first.DisposeAsync();
            await second.DisposeAsync();
            File.SetAttributes(cache.DatabasePath, FileAttributes.Normal);
            await using (var corrupt = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = cache.DatabasePath, Pooling = false }.ToString()))
            {
                await corrupt.OpenAsync(TestContext.Current.CancellationToken);
                await using var command = corrupt.CreateCommand();
                command.CommandText = "DELETE FROM expected_targets";
                await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }
            await Assert.ThrowsAsync<InvalidDataException>(() => scope.GetOrBuildAsync(key, plan,
                (_, _) => throw new InvalidOperationException("Corrupt reuse must not rebuild."), null, TestContext.Current.CancellationToken));
            Assert.Equal(VerificationExpectedWorksetState.Failed, cache.State);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ExpectedWorksetCancelledBuildIsNotPublishedAndCompletedReuseSurvivesTargetCancellation()
    {
        var root = Path.Combine(Path.GetTempPath(), $"proofshift-expected-cancel-{Guid.NewGuid():N}");
        try
        {
            await using var scope = new VerificationExpectedWorksetScope(root);
            var descriptor = new RuleDescriptor("target-presence", "1", "Cancellation fixture.", VerificationScope.Entity, requiredSourceFields: ["status"]);
            var rules = new VerificationRuleRegistry([new ExecutionPlanTestProvider(descriptor)]).Resolve(
                [new VerificationRuleDefinition(new RuleId("presence"), "target-presence", "1", EvidenceSeverity.Error)]);
            var plan = VerificationExecutionPlan.Create(rules);
            var key = new VerificationExpectedWorksetKey("checkpoint", "manifest", "source", "config", "graph", plan.ExpectedRequirementsFingerprint, "test-runtime");
            using var cancelled = new CancellationTokenSource();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scope.GetOrBuildAsync(key, plan, (_, token) =>
            {
                cancelled.Cancel();
                token.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }, null, cancelled.Token));
            Assert.Equal(0, scope.BuildCount);
            Assert.Empty(Directory.GetFiles(root, "manifest.json", SearchOption.AllDirectories));
            var completed = await scope.GetOrBuildAsync(key, plan, (_, _) => Task.CompletedTask, null, TestContext.Current.CancellationToken);
            await using var overlay = await SqliteVerificationWorkspace.CreateAsync(root, null, plan, TestContext.Current.CancellationToken);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => overlay.AttachExpectedAsync(completed, key, cancelled.Token));
            await completed.ValidateAsync(key, TestContext.Current.CancellationToken);
            Assert.Same(completed, await scope.GetOrBuildAsync(key, plan, (_, _) => throw new InvalidOperationException("Must remain reusable."),
                null, TestContext.Current.CancellationToken));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task PlanDrivenWorkspaceRetainsOnlyRequiredTypedValuesAndArtifactIdentity()
    {
        var root = Path.Combine(Path.GetTempPath(), $"proofshift-workset-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var retainedTargetFields = new[] { "status", "amount", "effective-date", "instant", "offset", "local", "binary", "nullable" };
            var memberKey = new VerificationOrderingKey("Generic.Member", "member_id", VerificationOrderingRole.Grouping);
            var descriptor = new RuleDescriptor("target-presence", "1", "Projected workset test rule.", VerificationScope.Entity,
                requiredSourceFields: ["member_id"], requiredTargetFields: [.. retainedTargetFields, "member_id"],
                groupingKeys: [memberKey]);
            var ruleSet = new VerificationRuleRegistry([new ExecutionPlanTestProvider(descriptor)]).Resolve(
                [new VerificationRuleDefinition(new RuleId("presence"), "target-presence", "1", EvidenceSeverity.Error)]);
            var plan = VerificationExecutionPlan.Create(ruleSet);
            var source = RecordWithValues("source", "source", "members", "member-1", "Generic.Member",
                [new("member_id", new StringValue("member-1")), new("irrelevant-source", new StringValue("source-only"))]);
            var values = new KeyValuePair<string, ValueNode>[]
            {
                new("status", new StringValue("ACTIVE")),
                new("amount", new DecimalValue(1234567890.0123456789m)),
                new("effective-date", new DateValue(new DateOnly(2025, 2, 3))),
                new("instant", new InstantValue(new DateTimeOffset(2025, 2, 3, 4, 5, 6, TimeSpan.Zero))),
                new("offset", new OffsetDateTimeValue(new DateTimeOffset(2025, 2, 3, 4, 5, 6, TimeSpan.FromHours(2)))),
                new("local", new LocalDateTimeValue(new DateTime(2025, 2, 3, 4, 5, 6, DateTimeKind.Unspecified))),
                new("binary", new BinaryReferenceValue("blob:synthetic", 4, new string('a', 64))),
                new("nullable", new NullValue()),
                new("irrelevant-target", new StringValue("must-not-be-materialized"))
            };
            var expected = RecordWithValues("expected", "shadow", "members", "member-1", "Generic.Member", values);
            var actual = RecordWithValues("actual", "shadow", "members", "member-1", "Generic.Member", values);
            var edge = new MigrationEdge(new MigrationEdgeId(Guid.NewGuid()), "workset-test",
                [new MigrationNodeId(Guid.NewGuid())], [new MigrationNodeId(Guid.NewGuid())],
                new MigrationOperation(MigrationOperationType.Transform), "1", new RecoveryDefinition(RecoveryMode.Reverse));

            await using var workspace = await SqliteVerificationWorkspace.CreateAsync(root, null, plan,
                TestContext.Current.CancellationToken);
            await workspace.AddSourceArtifactAsync("source-node", source, TestContext.Current.CancellationToken);
            await workspace.AddExpectedTargetAsync("target-node", expected, "source-node", source, edge,
                TestContext.Current.CancellationToken);
            await workspace.AddTargetObservationAsync("target-node", actual, TestContext.Current.CancellationToken);

            var projectedSource = Assert.Single(await CollectAsync(workspace.ReadArtifactRecordsAsync(
                VerificationArtifactRole.Source, "source-node", "Generic.Member", TestContext.Current.CancellationToken)));
            var projectedExpected = Assert.Single(await CollectAsync(workspace.ReadArtifactRecordsAsync(
                VerificationArtifactRole.ExpectedTarget, "target-node", "Generic.Member", TestContext.Current.CancellationToken)));
            var projectedActual = Assert.Single(await CollectAsync(workspace.ReadArtifactRecordsAsync(
                VerificationArtifactRole.ActualTarget, "target-node", "Generic.Member", TestContext.Current.CancellationToken)));

            Assert.Equal(expected.Artifact, projectedExpected.Artifact);
            Assert.Equal(actual.Artifact, projectedActual.Artifact);
            Assert.True(projectedSource.HasDeclaredFieldSet);
            Assert.Equal(["member_id"], projectedSource.Values.Keys);
            Assert.DoesNotContain("irrelevant-target", projectedExpected.Values.Keys);
            Assert.DoesNotContain("irrelevant-target", projectedActual.Values.Keys);
            Assert.Equal(new DecimalValue(1234567890.0123456789m), projectedActual.Values["amount"]);
            Assert.Equal(new DateValue(new DateOnly(2025, 2, 3)), projectedActual.Values["effective-date"]);
            Assert.Equal(new InstantValue(new DateTimeOffset(2025, 2, 3, 4, 5, 6, TimeSpan.Zero)), projectedActual.Values["instant"]);
            Assert.Equal(new OffsetDateTimeValue(new DateTimeOffset(2025, 2, 3, 4, 5, 6, TimeSpan.FromHours(2))), projectedActual.Values["offset"]);
            Assert.Equal(new LocalDateTimeValue(new DateTime(2025, 2, 3, 4, 5, 6, DateTimeKind.Unspecified)), projectedActual.Values["local"]);
            Assert.Equal(new BinaryReferenceValue("blob:synthetic", 4, new string('a', 64)), projectedActual.Values["binary"]);
            Assert.IsType<NullValue>(projectedActual.Values["nullable"]);
            var error = Assert.Throws<VerificationRuleException>(() => projectedActual.RequireDeclaredField("irrelevant-target"));
            Assert.Equal("PSRULE008", error.Code);
            var orderedActual = await CollectAsync(workspace.ReadArtifactRecordsByKeysAsync(VerificationArtifactRole.ActualTarget,
                "target-node", "Generic.Member", [memberKey], TestContext.Current.CancellationToken));
            Assert.Equal("member-1", Assert.Single(orderedActual).Artifact.Identity);
            await Assert.ThrowsAsync<VerificationRuleException>(async () => await CollectAsync(
                workspace.ReadArtifactRecordsByKeysAsync(VerificationArtifactRole.ActualTarget, "target-node",
                    "Generic.Member", [new VerificationOrderingKey("Generic.Member", "irrelevant-target",
                        VerificationOrderingRole.Grouping)], TestContext.Current.CancellationToken)));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task GraphDerivedCoverageSeeksIdentityAndRejectsUnboundOrWrongScopeWithoutFlaggingBoundDuplicates()
    {
        var root = Path.Combine(Path.GetTempPath(), $"proofshift-coverage-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var recorder = new PerformanceRecorder("synthetic graph coverage");
            long finalScratchBytes;
            await using (var workspace = await SqliteVerificationWorkspace.CreateAsync(root, recorder,
                TestContext.Current.CancellationToken))
            {
                var source = Record("source-bound", "source", "members", "source-1", "Generic.Member", "status", "A");
                var missingSource = Record("source-missing", "source", "members", "missing", "Generic.Member", "status", "A");
                var bound = Record("target-bound", "shadow", "members", "target-1", "Generic.Member", "status", "A");
                var unbound = Record("target-unbound", "shadow", "members", "target-2", "Generic.Member", "status", "A");
                var wrongSourceScope = Record("target-wrong-scope", "shadow", "members", "target-3", "Generic.Member", "status", "A");
                var edge = new MigrationEdge(new MigrationEdgeId(Guid.NewGuid()), "synthetic-map",
                    [new MigrationNodeId(Guid.NewGuid())], [new MigrationNodeId(Guid.NewGuid())],
                    new MigrationOperation(MigrationOperationType.Transform), "1", new RecoveryDefinition(RecoveryMode.Reverse));
                await workspace.AddSourceArtifactAsync("source-node", source, TestContext.Current.CancellationToken);
                await workspace.AddExpectedTargetAsync("target-node", bound, "source-node", source, edge, TestContext.Current.CancellationToken);
                await workspace.AddExpectedTargetAsync("target-node", unbound, "source-node", missingSource, edge, TestContext.Current.CancellationToken);
                await workspace.AddExpectedTargetAsync("target-node", wrongSourceScope, "wrong-source-node", source, edge, TestContext.Current.CancellationToken);
                await workspace.AddTargetObservationAsync("target-node", bound, TestContext.Current.CancellationToken);
                await workspace.AddTargetObservationAsync("target-node", bound, TestContext.Current.CancellationToken);
                await workspace.AddTargetObservationAsync("target-node", unbound, TestContext.Current.CancellationToken);
                await workspace.AddTargetObservationAsync("target-node", wrongSourceScope, TestContext.Current.CancellationToken);
                await workspace.AddTargetObservationAsync("other-target-node", bound, TestContext.Current.CancellationToken);
                var missing = new List<VerificationTargetFact>();
                await foreach (var fact in workspace.ReadTargetsWithoutGraphDerivedLineageAsync(TestContext.Current.CancellationToken))
                    missing.Add(fact);
                Assert.Equal(3, missing.Count);
                Assert.Contains(missing, fact => fact.NodeKey == "other-target-node" && fact.Artifact.Id == bound.Artifact.Id);
                Assert.Contains(missing, fact => fact.Artifact.Id == unbound.Artifact.Id);
                Assert.Contains(missing, fact => fact.Artifact.Id == wrongSourceScope.Artifact.Id);
                Assert.DoesNotContain(missing, fact => fact.NodeKey == "target-node" && fact.Artifact.Id == bound.Artifact.Id);
                Assert.True(await workspace.ContainsFieldValueAsync(VerificationArtifactRole.Source,
                    "source-node", "Generic.Member", "status", new StringValue("A"), TestContext.Current.CancellationToken));
                var orderedArtifacts = new List<VerificationArtifactRecord>();
                var orderingKeys = new[]
                {
                    new VerificationOrderingKey("Generic.Member", "status", VerificationOrderingRole.Ordering)
                };
                await foreach (var artifact in workspace.ReadArtifactRecordsByKeysAsync(
                    VerificationArtifactRole.ExpectedTarget, "target-node", "Generic.Member", orderingKeys,
                    TestContext.Current.CancellationToken))
                    orderedArtifacts.Add(artifact);
                Assert.Equal(3, orderedArtifacts.Count);
                finalScratchBytes = Directory.EnumerateFiles(Directory.GetDirectories(root, "proofshift-verification-*").Single())
                    .Sum(path => new FileInfo(path).Length);
            }
            var performanceRun = recorder.Complete();
            Assert.True(performanceRun.TemporaryWorkspacePeakBytes >= finalScratchBytes);
            var orderedQuery = Assert.Single(performanceRun.Stages,
                stage => stage.Name == "SQLite ordered ExpectedTarget artifact query");
            Assert.Equal(3, orderedQuery.ArtifactCount);
            Assert.Equal(1, Assert.Single(orderedQuery.Measurements,
                measurement => measurement.Name == "queryExecutions").Value);
            Assert.Equal(3, Assert.Single(orderedQuery.Measurements,
                measurement => measurement.Name == "rowsReturned").Value);
            using var diagnostics = JsonDocument.Parse(await File.ReadAllTextAsync(
                Directory.GetFiles(root, "*-diagnostics.json").Single(), TestContext.Current.CancellationToken));
            var queryPlans = diagnostics.RootElement.GetProperty("queryPlans").EnumerateObject().ToArray();
            var coveragePlan = queryPlans.Single(plan => plan.Name.StartsWith("actual-target coverage:",
                    StringComparison.Ordinal)).Value
                .EnumerateArray().Select(item => item.GetString()!).ToArray();
            var orderedPlan = queryPlans.Single(plan => plan.Name == "ordered-ExpectedTarget-status").Value
                .EnumerateArray().Select(item => item.GetString()!).ToArray();
            Assert.Contains(coveragePlan, step => step.Contains("expected_identity_idx", StringComparison.Ordinal) &&
                step.Contains("node_key=? AND identity_hash=?", StringComparison.Ordinal));
            Assert.DoesNotContain(coveragePlan, step => step.Contains("expected_journal_binding_idx", StringComparison.Ordinal));
            Assert.Contains(orderedPlan, step => step.Contains("expected_identity_idx", StringComparison.Ordinal));
            Assert.Contains(orderedPlan, step => step.Contains("sqlite_autoindex_artifact_order_keys_1", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task JournalValidationUsesIndexedBindingsAndRejectsMissingAndDuplicateAncestry()
    {
        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"proofshift-verification-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryRoot);
        try
        {
            var performanceRecorder = new PerformanceRecorder("synthetic journal validation");
            await using var workspace = await SqliteVerificationWorkspace.CreateAsync(temporaryRoot,
                performanceRecorder, TestContext.Current.CancellationToken);
            var source = Record("source-1", "source", "members", "member-1", "Pension.Member", "status", "A");
            var target = Record("target-1", "shadow", "participant", "member-1", "Pension.Member", "status", "ACTIVE");
            var edgeId = new MigrationEdgeId(Guid.NewGuid());
            var edge = new MigrationEdge(edgeId, "member-map", [new MigrationNodeId(Guid.NewGuid())],
                [new MigrationNodeId(Guid.NewGuid())], new MigrationOperation(MigrationOperationType.Transform),
                "1", new RecoveryDefinition(RecoveryMode.Reverse));

            await workspace.AddSourceArtifactAsync("source-members", source, TestContext.Current.CancellationToken);
            await workspace.AddExpectedTargetAsync("participant", target, "source-members", source, edge,
                TestContext.Current.CancellationToken);
            var validEntry = new VerificationJournalEntry("produced", "participant", target.Artifact,
                [new VerificationGraphArtifact("source-members", source.Artifact)], edgeId, "member-map", "1", null);
            await workspace.AddJournalEntryAsync(validEntry, TestContext.Current.CancellationToken);

            var valid = await workspace.ValidateJournalEntriesAsync(TestContext.Current.CancellationToken);
            Assert.Equal(1, valid.ProducedEntryCount);
            Assert.True(valid.SourceArtifactsMatch);
            Assert.True(valid.ProducedTargetsMatchExpectations);
            Assert.True(valid.ProducedAncestryIsUnique);

            var missingSource = Record("missing-source", "source", "members", "missing", "Pension.Member", "status", "A");
            await workspace.AddJournalEntryAsync(new VerificationJournalEntry("excluded", null, null,
                [new VerificationGraphArtifact("source-members", missingSource.Artifact)], edgeId, "member-map", "1", null),
                TestContext.Current.CancellationToken);
            var missingBinding = await workspace.ValidateJournalEntriesAsync(TestContext.Current.CancellationToken);
            Assert.False(missingBinding.SourceArtifactsMatch);

            await workspace.AddJournalEntryAsync(validEntry, TestContext.Current.CancellationToken);
            var duplicate = await workspace.ValidateJournalEntriesAsync(TestContext.Current.CancellationToken);
            Assert.False(duplicate.ProducedAncestryIsUnique);

            var mismatchedTarget = Record("target-other", "shadow", "participant", "member-other", "Pension.Member", "status", "ACTIVE");
            await workspace.AddJournalEntryAsync(new VerificationJournalEntry("produced", "participant", mismatchedTarget.Artifact,
                [new VerificationGraphArtifact("source-members", source.Artifact)], edgeId, "member-map", "1", null),
                TestContext.Current.CancellationToken);
            var mismatch = await workspace.ValidateJournalEntriesAsync(TestContext.Current.CancellationToken);
            Assert.False(mismatch.ProducedTargetsMatchExpectations);

            var sourceFacts = new List<VerificationSourceFact>();
            await foreach (var fact in workspace.ReadSourceFactsAsync(TestContext.Current.CancellationToken))
                sourceFacts.Add(fact);
            Assert.Single(sourceFacts);
            var graphDerivedSourceFacts = new List<VerificationSourceFact>();
            await foreach (var fact in workspace.ReadGraphDerivedSourceFactsAsync(TestContext.Current.CancellationToken))
                graphDerivedSourceFacts.Add(fact);
            Assert.Single(graphDerivedSourceFacts);
            var graphNodeIds = new Dictionary<string, MigrationNodeId>(StringComparer.Ordinal)
            {
                ["participant"] = edge.Targets[0],
                ["source-members"] = edge.Sources[0]
            };
            var lineages = new List<LineageRecord>();
            await foreach (var lineage in workspace.ReadLineageAsync(new string('a', 64), graphNodeIds,
                TestContext.Current.CancellationToken))
                lineages.Add(lineage);
            Assert.Equal(2, lineages.Count);
            var comparisons = new List<VerificationAttributeComparison>();
            await foreach (var comparison in workspace.ReadAttributeComparisonsAsync(TestContext.Current.CancellationToken))
                comparisons.Add(comparison);
            Assert.Empty(comparisons);

            await workspace.DisposeAsync();
            var performanceRun = performanceRecorder.Complete();
            var queryStages = performanceRun.Stages.Where(stage =>
                stage.Kind == PerformanceStageKind.Verification && stage.Name == "SQLite journal validation query").ToArray();
            Assert.Equal(4, queryStages.Length);
            Assert.All(queryStages, stage =>
            {
                Assert.True(stage.ElapsedMicroseconds >= 0);
                Assert.Equal(1, Assert.Single(stage.Measurements,
                    measurement => measurement.Name == "queryExecutions").Value);
                Assert.Equal(1, Assert.Single(stage.Measurements,
                    measurement => measurement.Name == "rowsReturned").Value);
            });
            var journalPreparation = Assert.Single(performanceRun.Stages,
                stage => stage.Name == "workspace journal normalization and parameter binding");
            Assert.Equal(4, journalPreparation.ArtifactCount);
            Assert.Equal(4, Assert.Single(journalPreparation.Measurements,
                measurement => measurement.Name == "journalEntries").Value);
            var journalExecution = Assert.Single(performanceRun.Stages,
                stage => stage.Name == "workspace journal SQLite execution");
            Assert.Equal(8, journalExecution.ArtifactCount);
            Assert.Equal(8, Assert.Single(journalExecution.Measurements,
                measurement => measurement.Name == "sqlStatementExecutions").Value);
            var journalFlush = Assert.Single(performanceRun.Stages,
                stage => stage.Name == "workspace journal transaction flush");
            Assert.Equal(4, journalFlush.ArtifactCount);
            Assert.Equal(4, Assert.Single(journalFlush.Measurements,
                measurement => measurement.Name == "journalContainingTransactionFlushes").Value);
            var journalFlushComponents = journalFlush.Measurements
                .Where(measurement => measurement.Name.EndsWith("ElapsedMicroseconds", StringComparison.Ordinal))
                .Sum(measurement => measurement.Value);
            Assert.InRange(journalFlushComponents, 0, journalFlush.ElapsedMicroseconds);

            using var diagnostics = JsonDocument.Parse(await File.ReadAllTextAsync(
                Directory.GetFiles(temporaryRoot, "*-diagnostics.json").Single(), TestContext.Current.CancellationToken));
            var queryPlan = diagnostics.RootElement.GetProperty("queryPlans").GetProperty("journal validation")
                .EnumerateArray().Select(step => step.GetString()!).ToArray();
            Assert.Contains(queryPlan, step => step.Contains("expected_journal_binding_idx", StringComparison.Ordinal));
            Assert.Contains(queryPlan, step => step.Contains("journal_produced_ancestry_idx", StringComparison.Ordinal));
            var plans = diagnostics.RootElement.GetProperty("queryPlans");
            var sourceFactsPlan = plans.GetProperty("source disposition facts").EnumerateArray()
                .Select(step => step.GetString()!).ToArray();
            var graphSourceFactsPlan = plans.GetProperty("graph-derived source disposition facts").EnumerateArray()
                .Select(step => step.GetString()!).ToArray();
            var lineagePlan = plans.GetProperty("projection lineage").EnumerateArray()
                .Select(step => step.GetString()!).ToArray();
            var comparisonPlan = plans.GetProperty("attribute comparisons").EnumerateArray()
                .Select(step => step.GetString()!).ToArray();
            Assert.Contains(sourceFactsPlan, step => step.Contains("journal_source_binding_idx", StringComparison.Ordinal));
            Assert.Contains(graphSourceFactsPlan, step => step.Contains("sqlite_autoindex_source_artifacts_1", StringComparison.Ordinal));
            Assert.Contains(lineagePlan, step => step.Contains("journal_produced_ancestry_idx", StringComparison.Ordinal));
            Assert.Contains(lineagePlan, step => step.Contains("sqlite_autoindex_journal_sources_1", StringComparison.Ordinal));
            Assert.Contains(comparisonPlan, step => step.Contains("expected_identity_idx", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(temporaryRoot)) Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    [Fact]
    public async Task PersistedVerificationLedgerStreamsOpaqueRecordsAndFingerprintsIndependentOfAppendOrder()
    {
        var firstRoot = Path.Combine(Path.GetTempPath(), $"proofshift-ledger-test-{Guid.NewGuid():N}");
        var secondRoot = Path.Combine(Path.GetTempPath(), $"proofshift-ledger-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(firstRoot);
        Directory.CreateDirectory(secondRoot);
        try
        {
            var sourceNodeId = new MigrationNodeId(Guid.Parse("10000000-0000-0000-0000-000000000001"));
            var targetNodeId = new MigrationNodeId(Guid.Parse("20000000-0000-0000-0000-000000000002"));
            var edgeId = new MigrationEdgeId(Guid.Parse("30000000-0000-0000-0000-000000000003"));
            var sourceOne = Record("source-ledger-1", "source", "members", "private-source-identity-1", "Pension.Member", "status", "A");
            var sourceTwo = Record("source-ledger-2", "source", "members", "private-source-identity-2", "Pension.Member", "status", "A");
            var targetOne = Record("target-ledger-1", "shadow", "participant", "private-target-identity-1", "Pension.Member", "status", "ACTIVE");
            var targetTwo = Record("target-ledger-2", "shadow", "participant", "private-target-identity-2", "Pension.Member", "status", "ACTIVE");
            var edge = new MigrationEdge(edgeId, "member-map", [sourceNodeId], [targetNodeId],
                new MigrationOperation(MigrationOperationType.Transform), "1", new RecoveryDefinition(RecoveryMode.Reverse));
            var dispositions = new[]
            {
                new ArtifactDispositionRecord(sourceOne.Artifact, ArtifactDisposition.Transformed, [targetOne.Artifact],
                    sourceNodeId: sourceNodeId, targetNodeIds: [targetNodeId]),
                new ArtifactDispositionRecord(sourceTwo.Artifact, ArtifactDisposition.Transformed, [targetTwo.Artifact],
                    sourceNodeId: sourceNodeId, targetNodeIds: [targetNodeId])
            };
            var lineages = new[]
            {
                new LineageRecord(targetOne.Artifact, [sourceTwo.Artifact, sourceOne.Artifact], [edgeId], "graph-hash", targetNodeId,
                    [sourceNodeId, sourceNodeId], LineageBasis.ExecutionObserved),
                new LineageRecord(targetTwo.Artifact, [sourceTwo.Artifact], [edgeId], "graph-hash", targetNodeId,
                    [sourceNodeId], LineageBasis.ExecutionObserved)
            };
            var journal = new[]
            {
                new VerificationJournalEntry("produced", "participant", targetOne.Artifact,
                    [new VerificationGraphArtifact("source-members", sourceOne.Artifact),
                     new VerificationGraphArtifact("source-members", sourceOne.Artifact)], edgeId, edge.Name, edge.Version, null),
                new VerificationJournalEntry("produced", "participant", targetTwo.Artifact,
                    [new VerificationGraphArtifact("source-members", sourceTwo.Artifact)], edgeId, edge.Name, edge.Version, null)
            };

            var firstRecorder = new PerformanceRecorder("synthetic ledger write counts");
            await using var first = await SqliteVerificationLedgerStore.CreateAsync(firstRoot,
                new RunId(Guid.NewGuid()), TestContext.Current.CancellationToken, firstRecorder);
            await using var second = await SqliteVerificationLedgerStore.CreateAsync(secondRoot,
                new RunId(Guid.NewGuid()), TestContext.Current.CancellationToken);
            foreach (var store in new[] { first, second })
            {
                await store.RegisterGraphNodeAsync("source-members", sourceNodeId, TestContext.Current.CancellationToken);
                await store.RegisterGraphNodeAsync("participant", targetNodeId, TestContext.Current.CancellationToken);
                await store.RegisterSourceAsync("source-members", sourceNodeId, sourceOne.Artifact, TestContext.Current.CancellationToken);
                await store.RegisterSourceAsync("source-members", sourceNodeId, sourceTwo.Artifact, TestContext.Current.CancellationToken);
                await store.RegisterTargetAsync("participant", targetNodeId, targetOne.Artifact, TestContext.Current.CancellationToken);
                await store.RegisterTargetAsync("participant", targetNodeId, targetTwo.Artifact, TestContext.Current.CancellationToken);
                await store.RegisterSourceAsync("source-members", sourceNodeId, sourceOne.Artifact, TestContext.Current.CancellationToken);
                await store.RegisterTargetAsync("participant", targetNodeId, targetOne.Artifact, TestContext.Current.CancellationToken);
            }

            foreach (var item in dispositions)
            {
                await first.AppendDispositionAsync(item, TestContext.Current.CancellationToken);
                await first.AppendLineageAsync(lineages.Single(lineage => lineage.Target.Id == item.Targets[0].Id), TestContext.Current.CancellationToken);
                await first.AppendJournalEntryAsync(journal.Single(entry => entry.Target!.Id == item.Targets[0].Id), TestContext.Current.CancellationToken);
            }
            foreach (var item in dispositions.Reverse())
            {
                await second.AppendDispositionAsync(item, TestContext.Current.CancellationToken);
                await second.AppendLineageAsync(lineages.Single(lineage => lineage.Target.Id == item.Targets[0].Id), TestContext.Current.CancellationToken);
                await second.AppendJournalEntryAsync(journal.Single(entry => entry.Target!.Id == item.Targets[0].Id), TestContext.Current.CancellationToken);
            }

            var firstSummary = await first.CompleteAsync(TestContext.Current.CancellationToken);
            var secondSummary = await second.CompleteAsync(TestContext.Current.CancellationToken);
            Assert.Equal(firstSummary.Fingerprint, secondSummary.Fingerprint);
            Assert.Equal(2, firstSummary.SourceCount);
            Assert.Equal(2, firstSummary.TargetCount);
            Assert.Equal(2, firstSummary.DispositionCount);
            Assert.Equal(2, firstSummary.LineageCount);
            Assert.Equal(2, firstSummary.JournalEntryCount);
            var performance = firstRecorder.Complete();
            var writeStage = Assert.Single(performance.Stages, stage => stage.Name == "verification ledger staging writes");
            double Measured(string name) => Assert.Single(writeStage.Measurements, measurement => measurement.Name == name).Value;
            Assert.Equal(14, Measured("ledgerApiWriteCalls"));
            Assert.Equal(2, Measured("successfulInsertCommands.graph_nodes"));
            Assert.Equal(2, Measured("insertedRows.graph_nodes"));
            Assert.Equal(3, Measured("successfulInsertCommands.sources"));
            Assert.Equal(2, Measured("insertedRows.sources"));
            Assert.Equal(3, Measured("successfulInsertCommands.targets"));
            Assert.Equal(2, Measured("insertedRows.targets"));
            Assert.Equal(2, Measured("successfulInsertCommands.dispositions"));
            Assert.Equal(2, Measured("insertedRows.dispositions"));
            Assert.True(Measured("serializedPayloadBytes.dispositions") > 0);
            Assert.Equal(2, Measured("successfulInsertCommands.lineage"));
            Assert.Equal(2, Measured("insertedRows.lineage"));
            Assert.True(Measured("serializedPayloadBytes.lineage") > 0);
            Assert.Equal(1, Measured("successfulInsertCommands.lineage_sources"));
            Assert.Equal(3, Measured("insertedRows.lineage_sources"));
            Assert.Equal(2, Measured("successfulInsertCommands.journal"));
            Assert.Equal(2, Measured("insertedRows.journal"));
            Assert.True(Measured("serializedPayloadBytes.journal") > 0);
            Assert.Equal(1, Measured("successfulInsertCommands.journal_scope_sources"));
            Assert.Equal(2, Measured("insertedRows.journal_scope_sources"));
            Assert.Equal(1, Measured("successfulInsertCommands.journal_scope_targets"));
            Assert.Equal(2, Measured("insertedRows.journal_scope_targets"));
            Assert.Equal(1, Measured("successfulInsertCommands.metadata"));
            Assert.Equal(9, Measured("insertedRows.metadata"));
            Assert.Equal(VerificationLedgerState.Complete, first.State);
            Assert.Equal(7, Assert.Single(performance.Stages,
                stage => stage.Name == "verification ledger destination secondary index build").Measurements
                .Single(measurement => measurement.Name == "secondaryIndexBuilds").Value);
            Assert.Equal(7, performance.Stages.Where(stage => stage.Name == "verification ledger staging secondary index build")
                .SelectMany(stage => stage.Measurements).Where(measurement => measurement.Name == "secondaryIndexBuilds").Sum(measurement => measurement.Value));
            Assert.Equal(9, performance.Stages.Count(stage => stage.Name.StartsWith("verification ledger finalize ", StringComparison.Ordinal)));
            foreach (var stage in performance.Stages.Where(stage => stage.Name.StartsWith("verification ledger finalize ", StringComparison.Ordinal) &&
                stage.Name != "verification ledger finalize metadata"))
            {
                Assert.Equal(1, Assert.Single(stage.Measurements, measurement => measurement.Name == "sqlExecutionOperations").Value);
                Assert.Equal(Assert.Single(stage.Measurements, measurement => measurement.Name == "stagedRows").Value,
                    Assert.Single(stage.Measurements, measurement => measurement.Name == "finalizedRows").Value);
            }
            Assert.True((await first.ValidateCoverageAsync(TestContext.Current.CancellationToken)).IsValid);
            Assert.True(await first.HasJournalEntryForEdgeAsync(edgeId, "produced", TestContext.Current.CancellationToken));
            var edgeScopeCounts = await first.ReadJournalScopeCountsAsync(edgeId, TestContext.Current.CancellationToken);
            Assert.Equal(2, edgeScopeCounts.SourceCount);
            Assert.Equal(2, edgeScopeCounts.TargetCount);
            Assert.Equal(4, (await CollectAsync(first.ReadDistinctJournalScopeAsync(edgeId,
                TestContext.Current.CancellationToken))).Count);
            var groupedLineage = await CollectAsync(first.ReadLineageTargetsBySourceAsync(TestContext.Current.CancellationToken));
            Assert.Equal(2, groupedLineage.Count);
            Assert.Single(groupedLineage.Single(item => item.SourceId == sourceOne.Artifact.Id).Targets);
            Assert.Equal(2, groupedLineage.Single(item => item.SourceId == sourceTwo.Artifact.Id).Targets.Count);

            var disposition = Assert.IsType<ArtifactDispositionRecord>(await first.FindDispositionAsync(sourceNodeId,
                sourceOne.Artifact.Id, TestContext.Current.CancellationToken));
            Assert.Equal($"opaque:{sourceOne.Artifact.Id.Value}", disposition.Source.Identity);
            var lineage = Assert.Single(await CollectAsync(first.FindLineageBySourceAsync(sourceNodeId,
                sourceOne.Artifact.Id, TestContext.Current.CancellationToken)));
            Assert.Equal(targetOne.Artifact.Id, lineage.Target.Id);
            Assert.Equal(2, (await CollectAsync(first.ReadJournalEntriesAsync(TestContext.Current.CancellationToken))).Count);
            await first.DisposeAsync();
            await second.DisposeAsync();
            await AssertLegacyLedgerFactsAsync(firstRoot, first.Receipt, sourceNodeId, targetNodeId,
                [sourceOne.Artifact, sourceTwo.Artifact], [targetOne.Artifact, targetTwo.Artifact], dispositions, lineages, journal);
            await using (var reopened = await SqliteVerificationLedgerStore.OpenAsync(firstRoot, first.Receipt,
                TestContext.Current.CancellationToken))
            {
                Assert.Equal(firstSummary, await reopened.ReadSummaryAsync(TestContext.Current.CancellationToken));
                Assert.True((await reopened.ValidateCoverageAsync(TestContext.Current.CancellationToken)).IsValid);
            }
            foreach (var table in new[] { "graph_nodes", "lineage_sources", "journal_scope", "lifecycle" })
            {
                var tamperRoot = Path.Combine(firstRoot, "tamper", table);
                var tamperPath = Path.Combine(tamperRoot, ".proofshift", "verification-ledgers", first.Receipt.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(tamperPath)!);
                File.Copy(Path.Combine(firstRoot, ".proofshift", "verification-ledgers", first.Receipt.RelativePath), tamperPath);
                await using (var tamper = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = tamperPath, Pooling = false }.ToString()))
                {
                    await tamper.OpenAsync(TestContext.Current.CancellationToken);
                    await using var command = tamper.CreateCommand();
                    command.CommandText = table == "lifecycle" ? "DELETE FROM metadata WHERE key IN ('state','auxiliaryFingerprint')" : $"DELETE FROM {table}";
                    await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
                }
                await Assert.ThrowsAsync<InvalidDataException>(() => SqliteVerificationLedgerStore.OpenAsync(tamperRoot, first.Receipt,
                    TestContext.Current.CancellationToken));
            }

            var files = Directory.GetFiles(Path.Combine(firstRoot, ".proofshift", "verification-ledgers"), "*", SearchOption.AllDirectories);
            var serializedFiles = string.Join("\n", files.Select(path => Encoding.UTF8.GetString(File.ReadAllBytes(path))));
            Assert.DoesNotContain("private-source-identity", serializedFiles, StringComparison.Ordinal);
            Assert.DoesNotContain("private-target-identity", serializedFiles, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(firstRoot)) Directory.Delete(firstRoot, recursive: true);
            if (Directory.Exists(secondRoot)) Directory.Delete(secondRoot, recursive: true);
        }
    }

    private static async Task AssertLegacyLedgerFactsAsync(string root, VerificationLedgerStoreReceipt receipt,
        MigrationNodeId sourceNode, MigrationNodeId targetNode, ArtifactReference[] sources, ArtifactReference[] targets,
        ArtifactDispositionRecord[] dispositions, LineageRecord[] lineages, VerificationJournalEntry[] journal)
    {
        var path = Path.Combine(root, ".proofshift", "verification-ledgers", receipt.RelativePath);
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var schemas = new List<string>();
        await using (var schema = connection.CreateCommand())
        {
            schema.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' ORDER BY name";
            await using var reader = await schema.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            while (await reader.ReadAsync(TestContext.Current.CancellationToken)) schemas.Add(reader.GetString(0));
        }
        await ExecuteAsync("ATTACH DATABASE ':memory:' AS legacy");
        foreach (var schema in schemas) await ExecuteAsync(schema.Replace("CREATE TABLE ", "CREATE TABLE legacy.", StringComparison.Ordinal));
        await ExecuteAsync("INSERT INTO legacy.graph_nodes VALUES($key,$id)", "source-members", sourceNode.Value.ToString("D"));
        await ExecuteAsync("INSERT INTO legacy.graph_nodes VALUES($key,$id)", "participant", targetNode.Value.ToString("D"));
        foreach (var artifact in sources)
            await ExecuteAsync("INSERT OR IGNORE INTO legacy.sources VALUES($node,$key,$id,$system,$endpoint,$type)",
                sourceNode.Value.ToString("D"), "source-members", artifact.Id.Value, artifact.SystemId.Value, artifact.EndpointId.Value, artifact.ArtifactType);
        foreach (var artifact in targets)
            await ExecuteAsync("INSERT OR IGNORE INTO legacy.targets VALUES($node,$key,$id,$system,$endpoint,$type)",
                targetNode.Value.ToString("D"), "participant", artifact.Id.Value, artifact.SystemId.Value, artifact.EndpointId.Value, artifact.ArtifactType);
        foreach (var disposition in dispositions)
        {
            var payload = JsonSerializer.Serialize(new
            {
                source = Opaque(disposition.Source), sourceNodeId = sourceNode.Value.ToString("D"),
                disposition = disposition.Disposition.ToString(),
                targets = disposition.Targets.Select((artifact, index) => new { nodeId = disposition.TargetNodeIds[index].Value.ToString("D"), artifact = Opaque(artifact) }),
                reason = disposition.Reason
            });
            await ExecuteAsync("INSERT INTO legacy.dispositions VALUES($node,$id,$kind,$payload)",
                sourceNode.Value.ToString("D"), disposition.Source.Id.Value, disposition.Disposition.ToString(), payload);
        }
        foreach (var lineage in lineages)
        {
            var payload = JsonSerializer.Serialize(new
            {
                target = Opaque(lineage.Target), targetNodeId = targetNode.Value.ToString("D"),
                sources = lineage.Sources.Select((artifact, index) => new { nodeId = lineage.SourceNodeIds[index].Value.ToString("D"), artifact = Opaque(artifact) })
                    .OrderBy(binding => binding.nodeId, StringComparer.Ordinal).ThenBy(binding => binding.artifact.id, StringComparer.Ordinal),
                path = lineage.Path.Select(edge => edge.Value.ToString("D")), planHash = lineage.PlanHash, basis = lineage.Basis.ToString()
            });
            await ExecuteAsync("INSERT INTO legacy.lineage VALUES($node,$id,$system,$endpoint,$type,$payload)",
                targetNode.Value.ToString("D"), lineage.Target.Id.Value, lineage.Target.SystemId.Value, lineage.Target.EndpointId.Value, lineage.Target.ArtifactType, payload);
            foreach (var artifact in lineage.Sources)
                await ExecuteAsync("INSERT INTO legacy.lineage_sources VALUES($targetNode,$targetId,$sourceNode,$sourceId)",
                    targetNode.Value.ToString("D"), lineage.Target.Id.Value, sourceNode.Value.ToString("D"), artifact.Id.Value);
        }
        foreach (var entry in journal)
        {
            var payload = JsonSerializer.Serialize(new
            {
                result = entry.Result, targetNode = entry.TargetNode, target = entry.Target is null ? null : Opaque(entry.Target),
                sources = entry.Sources.Select(binding => new { nodeId = binding.NodeKey, artifact = Opaque(binding.Artifact) })
                    .OrderBy(binding => binding.nodeId, StringComparer.Ordinal).ThenBy(binding => binding.artifact.id, StringComparer.Ordinal),
                edgeId = entry.EdgeId.Value.ToString("D"), edgeName = entry.EdgeName, edgeVersion = entry.EdgeVersion, failureCode = entry.FailureCode
            });
            await ExecuteAsync("INSERT INTO legacy.journal VALUES($edge,$result,$node,$target,$payload)",
                entry.EdgeId.Value.ToString("D"), entry.Result, entry.TargetNode, entry.Target?.Id.Value, payload);
            foreach (var binding in entry.Sources)
                await ExecuteAsync("INSERT OR IGNORE INTO legacy.journal_scope VALUES($edge,$side,$key,$node,$artifact)",
                    entry.EdgeId.Value.ToString("D"), "0", binding.NodeKey, sourceNode.Value.ToString("D"), binding.Artifact.Id.Value);
            if (entry.Target is not null)
                await ExecuteAsync("INSERT OR IGNORE INTO legacy.journal_scope VALUES($edge,$side,$key,$node,$artifact)",
                    entry.EdgeId.Value.ToString("D"), "1", entry.TargetNode, targetNode.Value.ToString("D"), entry.Target.Id.Value);
        }
        await ExecuteAsync("INSERT INTO legacy.metadata SELECT key,value FROM main.metadata WHERE key NOT IN ('state','auxiliaryFingerprint')");
        foreach (var table in new[] { "graph_nodes", "sources", "targets", "dispositions", "lineage", "lineage_sources", "journal", "journal_scope", "metadata" })
        {
            var columns = table == "metadata" ? "key,value" : "*";
            var filter = table == "metadata" ? " WHERE key NOT IN ('state','auxiliaryFingerprint')" : "";
            await using var difference = connection.CreateCommand();
            difference.CommandText = $"SELECT COUNT(*) FROM (SELECT {columns} FROM main.{table}{filter} EXCEPT SELECT {columns} FROM legacy.{table})";
            Assert.Equal(0L, await difference.ExecuteScalarAsync(TestContext.Current.CancellationToken));
            difference.CommandText = $"SELECT COUNT(*) FROM (SELECT {columns} FROM legacy.{table} EXCEPT SELECT {columns} FROM main.{table}{filter})";
            Assert.Equal(0L, await difference.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        }

        static LegacyOpaqueArtifact Opaque(ArtifactReference artifact) =>
            new(artifact.Id.Value, artifact.SystemId.Value, artifact.EndpointId.Value, artifact.ArtifactType);

        async Task ExecuteAsync(string sql, params string?[] values)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            var names = System.Text.RegularExpressions.Regex.Matches(sql, @"\$[a-zA-Z]+")
                .Select(match => match.Value).Distinct(StringComparer.Ordinal).ToArray();
            for (var index = 0; index < names.Length; index++) command.Parameters.AddWithValue(names[index], (object?)values[index] ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
    }

    private sealed record LegacyOpaqueArtifact(string id, string system, string endpoint, string type);

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public async Task LedgerFailureOrCancellationNeverPublishesCompleteEvidence(int phase, bool cancel)
    {
        var root = Path.Combine(Path.GetTempPath(), $"proofshift-ledger-failure-{Guid.NewGuid():N}");
        var runId = new RunId(Guid.NewGuid());
        var directory = Path.Combine(root, ".proofshift", "verification-ledgers", runId.Value.ToString("N"));
        var finalPath = Path.Combine(directory, "ledger.sqlite");
        try
        {
            using var cancellation = new CancellationTokenSource();
            await using var store = await SqliteVerificationLedgerStore.CreateAsync(root, runId, TestContext.Current.CancellationToken);
            var sourceNode = new MigrationNodeId(Guid.NewGuid());
            var source = Record("failure-source", "source", "members", "private-identity", "Test.Member", "status", "A");
            await store.RegisterGraphNodeAsync("source", sourceNode, TestContext.Current.CancellationToken);
            await store.RegisterSourceAsync("source", sourceNode, source.Artifact, TestContext.Current.CancellationToken);
            await store.AppendDispositionAsync(new ArtifactDispositionRecord(source.Artifact, ArtifactDisposition.Excluded, [],
                "Synthetic explicit exclusion.", sourceNodeId: sourceNode),
                TestContext.Current.CancellationToken);
            Assert.Equal(VerificationLedgerState.Pending, store.State);
            Assert.False(File.Exists(finalPath));
            Assert.Throws<InvalidOperationException>(() => store.Receipt);
            if (phase == 0 && !cancel)
                await Assert.ThrowsAsync<SqliteException>(() => store.RegisterGraphNodeAsync("source", sourceNode, TestContext.Current.CancellationToken));
            else
            {
                store.FinalizationObserver = checkpoint =>
                {
                    if ((phase == 1 && checkpoint == LedgerFinalizationPhase.TableCopied) ||
                        (phase == 2 && checkpoint == LedgerFinalizationPhase.IntegrityValidated))
                    {
                        if (cancel) cancellation.Cancel();
                        else throw new IOException("Synthetic finalization interruption.");
                    }
                };
                if (phase == 0) cancellation.Cancel();
            }
            if (cancel)
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.CompleteAsync(cancellation.Token));
            else if (phase == 0)
                await Assert.ThrowsAsync<InvalidOperationException>(() => store.CompleteAsync(TestContext.Current.CancellationToken));
            else
                await Assert.ThrowsAsync<IOException>(() => store.CompleteAsync(TestContext.Current.CancellationToken));
            Assert.Equal(cancel ? VerificationLedgerState.Cancelled : VerificationLedgerState.Failed, store.State);
            Assert.Throws<InvalidOperationException>(() => store.Receipt);
            await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadSummaryAsync(TestContext.Current.CancellationToken));
            if (File.Exists(finalPath))
            {
                var forged = new VerificationLedgerStoreReceipt(runId.Value, $"{runId.Value:N}/ledger.sqlite", new string('a', 64), 1, 0, 1, 0, 0);
                await Assert.ThrowsAsync<InvalidDataException>(() => SqliteVerificationLedgerStore.OpenAsync(root, forged, TestContext.Current.CancellationToken));
            }
            await store.DisposeAsync();
            Assert.False(Directory.Exists(directory));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task LedgerPublicationFailureAfterMarkerRevokesReceiptAndDeletesIncompleteOutput()
    {
        var root = Path.Combine(Path.GetTempPath(), $"proofshift-ledger-publication-failure-{Guid.NewGuid():N}");
        var runId = new RunId(Guid.NewGuid());
        var directory = Path.Combine(root, ".proofshift", "verification-ledgers", runId.Value.ToString("N"));
        try
        {
            var recorder = new PerformanceRecorder("synthetic post-marker telemetry failure");
            await using var store = await SqliteVerificationLedgerStore.CreateAsync(root, runId, TestContext.Current.CancellationToken, recorder);
            store.FinalizationObserver = phase =>
            {
                if (phase == LedgerFinalizationPhase.IntegrityValidated) recorder.Complete();
            };
            await Assert.ThrowsAsync<ObjectDisposedException>(() => store.CompleteAsync(TestContext.Current.CancellationToken));
            Assert.Equal(VerificationLedgerState.Failed, store.State);
            Assert.Throws<InvalidOperationException>(() => store.Receipt);
            await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadSummaryAsync(TestContext.Current.CancellationToken));
            var forged = new VerificationLedgerStoreReceipt(runId.Value, $"{runId.Value:N}/ledger.sqlite", new string('a', 64), 0, 0, 0, 0, 0);
            await Assert.ThrowsAsync<InvalidDataException>(() => SqliteVerificationLedgerStore.OpenAsync(root, forged, TestContext.Current.CancellationToken));
            await store.DisposeAsync();
            Assert.False(Directory.Exists(directory));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task LedgerFinalizationUsesBoundedSetCopiesAndDuplicateBindingsFailClosed()
    {
        var root = Path.Combine(Path.GetTempPath(), $"proofshift-ledger-bounded-{Guid.NewGuid():N}");
        try
        {
            var recorder = new PerformanceRecorder("bounded ledger publication");
            var nodeId = new MigrationNodeId(Guid.NewGuid());
            await using var store = await SqliteVerificationLedgerStore.CreateAsync(root, new RunId(Guid.NewGuid()),
                TestContext.Current.CancellationToken, recorder);
            await store.RegisterGraphNodeAsync("source", nodeId, TestContext.Current.CancellationToken);
            for (var index = 0; index < 16385; index++)
            {
                var artifact = new ArtifactReference(new ArtifactId($"bounded-{index:D6}"), new SystemId("source"),
                    new StorageEndpointId("members"), "record", $"private-{index}");
                await store.RegisterSourceAsync("source", nodeId, artifact, TestContext.Current.CancellationToken);
                await store.AppendDispositionAsync(new ArtifactDispositionRecord(artifact, ArtifactDisposition.Excluded, [],
                    "Explicit synthetic exclusion.", nodeId), TestContext.Current.CancellationToken);
            }
            var summary = await store.CompleteAsync(TestContext.Current.CancellationToken);
            Assert.Equal(16385, summary.SourceCount);
            Assert.Equal(16385, summary.DispositionCount);
            var performance = recorder.Complete();
            foreach (var name in new[] { "sources", "dispositions" })
            {
                var stage = Assert.Single(performance.Stages, item => item.Name == $"verification ledger finalize {name}");
                Assert.Equal(2, Assert.Single(stage.Measurements, measurement => measurement.Name == "sqlExecutionOperations").Value);
                Assert.Equal(16384, Assert.Single(stage.Measurements, measurement => measurement.Name == "maximumRowsPerOperation").Value);
            }
            await using var duplicate = await SqliteVerificationLedgerStore.CreateAsync(root, new RunId(Guid.NewGuid()), TestContext.Current.CancellationToken);
            var source = new ArtifactReference(new ArtifactId("duplicate-source"), new SystemId("source"), new StorageEndpointId("members"), "record", "private-source");
            var target = new ArtifactReference(new ArtifactId("duplicate-target"), new SystemId("target"), new StorageEndpointId("members"), "record", "private-target");
            await Assert.ThrowsAsync<SqliteException>(() => duplicate.AppendLineageAsync(new LineageRecord(target,
                [source, source], [new MigrationEdgeId(Guid.NewGuid())], "graph-hash", nodeId, [nodeId, nodeId]), TestContext.Current.CancellationToken));
            Assert.Equal(VerificationLedgerState.Failed, duplicate.State);
            await Assert.ThrowsAsync<InvalidOperationException>(() => duplicate.CompleteAsync(TestContext.Current.CancellationToken));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task TemporaryWorkspaceIndexesJournalAndTargetObservationsThenDeletesScratchDirectory()
    {
        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"proofshift-verification-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryRoot);
        try
        {
            string scratchDirectory;
            await using (var workspace = await SqliteVerificationWorkspace.CreateAsync(temporaryRoot, TestContext.Current.CancellationToken))
            {
                scratchDirectory = Directory.GetDirectories(temporaryRoot).Single();
                var source = Record("source-1", "source", "members", "member-1", "Pension.Member", "status", "A");
                var target = Record("target-1", "shadow", "participant", "member-1", "Pension.Member", "status", "ACTIVE");
                var edgeId = new MigrationEdgeId(Guid.NewGuid());
                var edge = new MigrationEdge(edgeId, "member-map", [new MigrationNodeId(Guid.NewGuid())],
                    [new MigrationNodeId(Guid.NewGuid())], new MigrationOperation(MigrationOperationType.Transform,
                        fields: [new TransformationFieldDefinition("status", "status",
                            [new TransformationStep(TransformationStepType.CodeMap, "1", [new KeyValuePair<string, string>("A", "ACTIVE")])])]),
                    "1", new RecoveryDefinition(RecoveryMode.Reverse));

                await workspace.AddSourceArtifactAsync("source-members", source, TestContext.Current.CancellationToken);
                await workspace.AddSourceArtifactAsync("source-archive", source, TestContext.Current.CancellationToken);
                await workspace.AddExpectedTargetAsync("participant", target, "source-members", source, edge, TestContext.Current.CancellationToken);
                await workspace.AddJournalEntryAsync(new VerificationJournalEntry("produced", "participant", target.Artifact,
                    [new VerificationGraphArtifact("source-members", source.Artifact)], edgeId, "member-map", "1", null), TestContext.Current.CancellationToken);
                await workspace.AddTargetObservationAsync("participant", target, TestContext.Current.CancellationToken);
                var duplicate = Record("target-duplicate", "shadow", "participant", "member-1", "Pension.Member", "status", "ACTIVE");
                await workspace.AddTargetObservationAsync("participant", duplicate, TestContext.Current.CancellationToken);

                Assert.Equal(2, workspace.SourceArtifactCount);
                Assert.Equal(1, workspace.ExpectedTargetCount);
                Assert.Equal(2, workspace.ActualTargetCount);
                var sourceFacts = await CollectAsync(workspace.ReadSourceFactsAsync(TestContext.Current.CancellationToken));
                Assert.Equal(2, sourceFacts.Count);
                var sourceFact = Assert.Single(sourceFacts, fact => fact.NodeKey == "source-members");
                Assert.Equal(1, sourceFact.ProducedEntries);
                var targetFact = Assert.Single(await CollectAsync(workspace.ReadMaterializedJournalTargetsAsync(TestContext.Current.CancellationToken)));
                Assert.Equal(2, targetFact.ActualCount);
                var duplicateFact = Assert.Single(await CollectAsync(workspace.ReadDuplicateTargetFactsAsync(TestContext.Current.CancellationToken)));
                Assert.Equal(2, duplicateFact.ActualCount);
                Assert.Empty(await CollectAsync(workspace.ReadMissingTargetFactsAsync(TestContext.Current.CancellationToken)));
                Assert.Empty(await CollectAsync(workspace.ReadUnexpectedTargetFactsAsync(TestContext.Current.CancellationToken)));
                var graphDerivedSource = Assert.Single(await CollectAsync(workspace.ReadGraphDerivedSourceFactsAsync(TestContext.Current.CancellationToken)),
                    fact => fact.NodeKey == "source-members");
                Assert.Equal(1, graphDerivedSource.ProducedEntries);
                var graphDerivedTarget = Assert.Single(await CollectAsync(workspace.ReadGraphDerivedTargetFactsAsync(TestContext.Current.CancellationToken)));
                Assert.Equal(2, graphDerivedTarget.ActualCount);
                Assert.Empty(await CollectAsync(workspace.ReadMissingGraphDerivedTargetFactsAsync(TestContext.Current.CancellationToken)));
                var graphNodes = new Dictionary<string, MigrationNodeId>
                {
                    ["source-members"] = new MigrationNodeId(Guid.NewGuid()),
                    ["participant"] = new MigrationNodeId(Guid.NewGuid())
                };
                var expectedLineage = Assert.Single(await CollectAsync(workspace.ReadGraphDerivedLineageAsync(
                    new string('a', 64), graphNodes, TestContext.Current.CancellationToken)));
                Assert.Equal(LineageBasis.GraphDerivedExpected, expectedLineage.Basis);
                Assert.Empty(await CollectAsync(workspace.ReadTargetsWithoutGraphDerivedLineageAsync(TestContext.Current.CancellationToken)));
            }

            Assert.False(Directory.Exists(scratchDirectory));
        }
        finally
        {
            if (Directory.Exists(temporaryRoot)) Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ArtifactRecordStreamPreservesTypedValuesRelationshipsAndTemporalMetadata()
    {
        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"proofshift-verification-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryRoot);
        try
        {
            await using var workspace = await SqliteVerificationWorkspace.CreateAsync(temporaryRoot, TestContext.Current.CancellationToken);
            var member = Record("member-1", "source", "members", "7", "Pension.Member", "status", "ACTIVE");
            var employmentArtifact = new ArtifactReference(new ArtifactId("employment-1"), new SystemId("source"),
                new StorageEndpointId("employment"), "row", "7|2020-01-01");
            var employment = new RecordEnvelope(employmentArtifact, "Pension.Employment",
                [new KeyValuePair<string, ValueNode>("member_id", new IntegerValue(7)),
                 new KeyValuePair<string, ValueNode>("credit", new DecimalValue(0.875m)),
                 new KeyValuePair<string, ValueNode>("effective_from", new DateValue(new DateOnly(2020, 1, 1)))],
                member.Provenance,
                [new RelationshipReference("HAS_EMPLOYMENT", member.Artifact, RelationshipDirection.Outgoing)],
                new TemporalMetadata(new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero), null));

            await workspace.AddSourceArtifactAsync("employment-history", employment, TestContext.Current.CancellationToken);

            var actual = Assert.Single(await CollectAsync(workspace.ReadArtifactRecordsAsync(
                VerificationArtifactRole.Source, "employment-history", "Pension.Employment", TestContext.Current.CancellationToken)));
            Assert.Equal(employment.Artifact, actual.Artifact);
            Assert.Equal(employment.Values, actual.Values);
            Assert.Equal(employment.Relationships, actual.Relationships);
            Assert.Equal(employment.Temporal, actual.Temporal);
            Assert.Empty(await CollectAsync(workspace.ReadArtifactRecordsAsync(
                VerificationArtifactRole.ActualTarget, null, null, TestContext.Current.CancellationToken)));

            await workspace.AddSourceArtifactAsync("ordered-members",
                Record("source-z", "source", "members", "z", "Pension.Member", "member_id", "z"), TestContext.Current.CancellationToken);
            await workspace.AddSourceArtifactAsync("ordered-members",
                Record("source-a", "source", "members", "a", "Pension.Member", "member_id", "a"), TestContext.Current.CancellationToken);
            var ordered = await CollectAsync(workspace.ReadArtifactRecordsAsync(VerificationArtifactRole.Source,
                "ordered-members", "Pension.Member", TestContext.Current.CancellationToken, ["member_id"]));
            Assert.Equal(["a", "z"], ordered.Select(record => record.Artifact.Identity));

            RecordEnvelope OrderedRecord(string id, string group, long sequence, DateOnly effectiveDate, decimal amount)
            {
                var artifact = new ArtifactReference(new ArtifactId($"order-{id}"), new SystemId("source"),
                    new StorageEndpointId("members"), "row", id);
                return new RecordEnvelope(artifact, "Generic.OrderedRecord",
                    [new KeyValuePair<string, ValueNode>("group", new StringValue(group)),
                     new KeyValuePair<string, ValueNode>("sequence", new IntegerValue(sequence)),
                     new KeyValuePair<string, ValueNode>("effective_date", new DateValue(effectiveDate)),
                     new KeyValuePair<string, ValueNode>("amount", new DecimalValue(amount))],
                    member.Provenance);
            }

            foreach (var record in new[]
            {
                OrderedRecord("a-sequence-1-low", "A", 1, new DateOnly(2025, 1, 1), 2.5m),
                OrderedRecord("a-sequence-1-high", "A", 1, new DateOnly(2025, 1, 1), 10m),
                OrderedRecord("a-sequence-2", "A", 2, new DateOnly(2025, 1, 1), 5m),
                OrderedRecord("a-next-day", "A", 1, new DateOnly(2025, 1, 2), 1m),
                OrderedRecord("b-first", "B", 1, new DateOnly(2025, 1, 1), 100m)
            })
                await workspace.AddSourceArtifactAsync("typed-order", record, TestContext.Current.CancellationToken);

            var typedOrder = await CollectAsync(workspace.ReadArtifactRecordsByKeysAsync(VerificationArtifactRole.Source,
                "typed-order", "Generic.OrderedRecord",
                [new VerificationOrderingKey("Generic.OrderedRecord", "group", VerificationOrderingRole.Grouping),
                 new VerificationOrderingKey("Generic.OrderedRecord", "effective_date", VerificationOrderingRole.Ordering),
                 new VerificationOrderingKey("Generic.OrderedRecord", "sequence", VerificationOrderingRole.Ordering),
                 new VerificationOrderingKey("Generic.OrderedRecord", "amount", VerificationOrderingRole.Ordering, Descending: true)],
                TestContext.Current.CancellationToken));
            Assert.Equal(["a-sequence-1-high", "a-sequence-1-low", "a-sequence-2", "a-next-day", "b-first"],
                typedOrder.Select(record => record.Artifact.Identity));
            Assert.True(await workspace.ContainsFieldValueAsync(VerificationArtifactRole.Source, "typed-order",
                "Generic.OrderedRecord", "group", new StringValue("A"), TestContext.Current.CancellationToken));
            Assert.False(await workspace.ContainsFieldValueAsync(VerificationArtifactRole.Source, "typed-order",
                "Generic.OrderedRecord", "group", new StringValue("missing"), TestContext.Current.CancellationToken));
        }
        finally
        {
            if (Directory.Exists(temporaryRoot)) Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    private static RecordEnvelope RecordWithValues(string artifactId, string systemId, string nodeEndpoint,
        string identity, string semanticType, IEnumerable<KeyValuePair<string, ValueNode>> values) => new(
        new ArtifactReference(new ArtifactId(artifactId), new SystemId(systemId), new StorageEndpointId(nodeEndpoint), "row", identity),
        semanticType, values,
        new ProvenanceMetadata(new ConnectorId("synthetic"), new StorageEndpointId(nodeEndpoint), identity, DateTimeOffset.UnixEpoch));

    private static RecordEnvelope Record(string artifactId, string systemId, string nodeEndpoint, string identity,
        string semanticType, string field, string value) => new(
        new ArtifactReference(new ArtifactId(artifactId), new SystemId(systemId), new StorageEndpointId(nodeEndpoint), "row", identity),
        semanticType,
        [new KeyValuePair<string, ValueNode>(field, new StringValue(value))],
        new ProvenanceMetadata(new ConnectorId("synthetic"), new StorageEndpointId(nodeEndpoint), identity, DateTimeOffset.UnixEpoch));

    private static async Task<List<T>> CollectAsync<T>(IAsyncEnumerable<T> values)
    {
        var result = new List<T>();
        await foreach (var value in values.WithCancellation(TestContext.Current.CancellationToken)) result.Add(value);
        return result;
    }
}
