using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ProofShift.Configuration;
using ProofShift.Connectors.Abstractions;
using ProofShift.Domain;
using ProofShift.Engine;
using ProofShift.Evidence;
using ProofShift.Graph;
using ProofShift.Snapshots;

namespace ProofShift.Verification;

public sealed record VerificationTargetRuntime(
    string NodeKey,
    IShadowTargetConnector Connector,
    ShadowTargetContext Context);

public sealed class VerificationService
{
    private const string SystemRuleId = "proofshift.verification.materialized-state";
    private readonly IMaterializedSnapshotStore _snapshotStore;

    public VerificationService(IMaterializedSnapshotStore snapshotStore) =>
        _snapshotStore = snapshotStore ?? throw new ArgumentNullException(nameof(snapshotStore));

    public async Task<VerificationResult> VerifyAsync(
        LoadedProjectConfiguration configuration,
        MigrationGraph graph,
        ProjectionVerificationBinding binding,
        VerificationRuleSet ruleSet,
        string projectDirectory,
        string temporaryDirectory,
        string proofShiftVersion,
        IEnumerable<VerificationTargetRuntime> targets,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(ruleSet);
        ArgumentNullException.ThrowIfNull(targets);
        var startedAt = DateTimeOffset.UtcNow;
        var targetRuntimes = targets.OrderBy(target => target.NodeKey, StringComparer.Ordinal).ToArray();
        var graphNodes = graph.Nodes.ToDictionary(node => node.Name, StringComparer.Ordinal);
        var nodeIds = graph.Nodes.ToDictionary(node => node.Name, node => node.Id, StringComparer.Ordinal);
        var edges = graph.Edges.ToDictionary(edge => edge.Id);

        ValidateConfigurationBinding(configuration, graph, binding);
        var journalPath = await ValidateProjectionManifestAsync(projectDirectory, binding, cancellationToken).ConfigureAwait(false);
        ValidateTargetRuntimes(configuration, graph, binding, targetRuntimes);

        await using var checkpoint = await _snapshotStore.OpenCompleteAsync(
            binding.CheckpointId.Value.ToString("N", CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
        ValidateCheckpoint(checkpoint.Manifest, configuration, graph, binding);

        var verificationRunId = new RunId(Guid.NewGuid());
        await using var workspace = await SqliteVerificationWorkspace.CreateAsync(temporaryDirectory, cancellationToken).ConfigureAwait(false);
        var sourceNodes = graph.Nodes.Where(node => node.Type == MigrationNodeType.Source)
            .OrderBy(node => node.Name, StringComparer.Ordinal).ToArray();

        foreach (var sourceNode in sourceNodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await foreach (var source in checkpoint.ReadAsync(sourceNode.Name, sourceNode.Selector, cancellationToken)
                .WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                ValidateRecordForNode(source, sourceNode);
                await workspace.AddSourceArtifactAsync(sourceNode.Name, source, cancellationToken).ConfigureAwait(false);

                foreach (var edge in graph.Edges.Where(edge => edge.Sources.Contains(sourceNode.Id))
                    .OrderBy(edge => edge.Id.Value))
                {
                    if (edge.Operation.Type is MigrationOperationType.Exclude or MigrationOperationType.Relationship) continue;
                    if (edge.Sources.Count != 1)
                        throw ContextMismatch("Verification cannot replay an edge with multiple source nodes under PS-0.5 projection semantics.");
                    foreach (var targetNode in edge.Targets.Select(id => graph.Nodes.Single(node => node.Id == id))
                        .OrderBy(node => node.Name, StringComparer.Ordinal))
                    {
                        var expected = GraphTransformationRuntime.Transform(source, edge, targetNode);
                        await workspace.AddExpectedTargetAsync(targetNode.Name, expected, sourceNode.Name, source, edge,
                            cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }

        ValidateSourceRecordCoverage(checkpoint.Manifest, workspace.SourceArtifactCount);
        long producedEntries = 0;
        var producedKeys = new HashSet<(string NodeKey, string TargetId, string SourceNodeKey, string SourceId, MigrationEdgeId EdgeId)>();
        var terminalJournalEntries = new List<VerificationJournalEntry>();
        await foreach (var journalEntry in ReadJournalAsync(journalPath, binding.ProjectionRunId, cancellationToken)
            .ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!edges.TryGetValue(journalEntry.EdgeId, out var edge) || edge.Name != journalEntry.EdgeName || edge.Version != journalEntry.EdgeVersion)
                throw ContextMismatch("Projection journal references an edge that does not match the configured graph.");

            var scopedSources = new List<VerificationGraphArtifact>();
            foreach (var source in journalEntry.Sources)
            {
                var candidates = new List<MigrationNode>();
                foreach (var node in edge.Sources.Select(id => graph.Nodes.Single(candidate => candidate.Id == id)))
                    if (await workspace.ContainsSourceArtifactAsync(node.Name, source, cancellationToken).ConfigureAwait(false))
                        candidates.Add(node);
                if (candidates.Count != 1)
                    throw ContextMismatch("Projection journal source cannot be resolved to exactly one checkpoint graph node.");
                scopedSources.Add(new VerificationGraphArtifact(candidates[0].Name, source));
            }

            if (journalEntry.Result == "pending") continue;
            if (journalEntry.Result == "failed")
                throw ContextMismatch("A successful projection journal contains a failed terminal entry.");
            if (journalEntry.Result == "produced")
            {
                if (journalEntry.Target is null || journalEntry.TargetNode is null || scopedSources.Count == 0)
                    throw ContextMismatch("A produced journal entry is missing its target or source ancestry.");
                if (!graphNodes.TryGetValue(journalEntry.TargetNode, out var targetNode) || !edge.Targets.Contains(targetNode.Id))
                    throw ContextMismatch("Projection journal target node is not declared by its migration edge.");
                if (scopedSources.Count != 1 || !await workspace.ContainsExpectedTargetAsync(targetNode.Name,
                    journalEntry.Target, scopedSources[0].NodeKey, scopedSources[0].Artifact, edge.Id, cancellationToken).ConfigureAwait(false))
                    throw ContextMismatch("Projection journal target does not match deterministic graph transformation output.");
                if (!producedKeys.Add((targetNode.Name, journalEntry.Target.Id.Value, scopedSources[0].NodeKey,
                    scopedSources[0].Artifact.Id.Value, edge.Id)))
                    throw ContextMismatch("Projection journal contains duplicate terminal materialization ancestry.");
                producedEntries++;
            }
            else if (journalEntry.Result == "excluded" && (edge.Operation.Type != MigrationOperationType.Exclude ||
                scopedSources.Count != 1 || journalEntry.Target is not null || journalEntry.TargetNode is not null))
            {
                throw ContextMismatch("Projection journal exclusion does not match an explicit graph exclusion edge.");
            }
            else if (journalEntry.Result == "metadataonly" && (edge.Operation.Type != MigrationOperationType.Relationship ||
                scopedSources.Count != 0 || journalEntry.Target is not null || journalEntry.TargetNode is not null))
            {
                throw ContextMismatch("Projection journal metadata-only entry does not match a relationship edge.");
            }
            else if (journalEntry.Result is not ("produced" or "excluded" or "metadataonly"))
            {
                throw ContextMismatch("Projection journal contains an unsupported terminal result.");
            }

            var verifiedJournalEntry = new VerificationJournalEntry(journalEntry.Result,
                journalEntry.TargetNode, journalEntry.Target, scopedSources, journalEntry.EdgeId,
                journalEntry.EdgeName, journalEntry.EdgeVersion, journalEntry.FailureCode);
            await workspace.AddJournalEntryAsync(verifiedJournalEntry, cancellationToken).ConfigureAwait(false);
            terminalJournalEntries.Add(verifiedJournalEntry);
        }

        if (producedEntries != workspace.ExpectedTargetCount || producedEntries != binding.ProjectionTargetCount)
            throw ContextMismatch("Projection journal materialization coverage does not match graph-derived expectations and manifest counts.");

        using var targetFingerprint = MaterializedTargetFingerprint.CreateBuilder(graph.GraphHash);
        foreach (var targetRuntime in targetRuntimes)
        {
            var node = graphNodes[targetRuntime.NodeKey];
            var request = new ReadRequest(targetRuntime.Context, node.Selector);
            await foreach (var actual in targetRuntime.Connector.ReadAsync(request, cancellationToken)
                .WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                ValidateRecordForNode(actual, node);
                if (GraphTargetIdentity.Create(actual, node.Selector) != actual.Artifact.Identity ||
                    StableArtifactIdentity.CreateArtifactId(actual.Artifact.SystemId.Value, actual.Artifact.EndpointId.Value,
                        actual.Artifact.ArtifactType, actual.Artifact.Identity) != actual.Artifact.Id.Value)
                    throw ContextMismatch("Shadow target connector returned an artifact identity inconsistent with its selector.");
                targetFingerprint.Add(node.Name, actual);
                await workspace.AddTargetObservationAsync(node.Name, actual, cancellationToken).ConfigureAwait(false);
            }
        }
        var actualFingerprint = targetFingerprint.Finish();

        var findings = new List<VerificationFinding>();
        var systemRule = new RuleId(SystemRuleId);
        var fingerprintMatches = actualFingerprint.Fingerprint == binding.ProjectionFingerprint &&
            actualFingerprint.RecordCount == binding.ProjectionTargetCount;
        findings.Add(new VerificationFinding(EvidenceType.Observation,
            fingerprintMatches ? EvidenceResult.Pass : EvidenceResult.Fail, EvidenceSeverity.Critical,
            fingerprintMatches ? "MaterializedTargetFingerprint" : "MaterializedTargetChanged",
            fingerprintMatches
                ? "Physical shadow state matches the projection's materialized target fingerprint."
                : "Physical shadow state differs from the projection's materialized target fingerprint; values are represented only by hashes.",
            "materialized-target-fingerprint",
            [new EvidenceReference(checkpointId: binding.CheckpointId), new EvidenceReference(projectionRunId: binding.ProjectionRunId)],
            new EvidenceValue(new StringValue($"sha256:{binding.ProjectionFingerprint}:{binding.ProjectionTargetCount.ToString(CultureInfo.InvariantCulture)}")),
            new EvidenceValue(new StringValue($"sha256:{actualFingerprint.Fingerprint}:{actualFingerprint.RecordCount.ToString(CultureInfo.InvariantCulture)}"))));

        var context = new VerificationExecutionContext(configuration, graph, binding, verificationRunId, workspace);
        foreach (var rule in ruleSet.Rules)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await foreach (var finding in rule.EvaluateAsync(context, cancellationToken)
                    .WithCancellation(cancellationToken).ConfigureAwait(false))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    findings.Add(finding);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new VerificationRuleException(VerificationIssueCodes.RuleExecutionFailure,
                    $"Verification rule '{rule.Id.Value}' failed during execution: {exception.GetType().Name}.");
            }
        }

        var lineage = new List<LineageRecord>();
        await foreach (var item in workspace.ReadLineageAsync(graph.GraphHash, nodeIds, cancellationToken)
            .WithCancellation(cancellationToken).ConfigureAwait(false)) lineage.Add(item);
        var lineageBySource = BuildLineageBySource(lineage, graph.Nodes.ToDictionary(node => node.Id, node => node.Name));
        var dispositions = new List<ArtifactDispositionRecord>();
        await foreach (var source in workspace.ReadSourceFactsAsync(cancellationToken)
            .WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            var targetsForSource = lineageBySource.GetValueOrDefault((source.NodeKey, source.Artifact.Id.Value), []);
            targetsForSource = targetsForSource.DistinctBy(item => (item.TargetNodeId, item.Target.Id)).ToList();
            var targetArtifacts = targetsForSource.Select(item => item.Target).ToArray();
            var targetNodeIds = targetsForSource.Select(item => item.TargetNodeId!.Value).ToArray();
            var disposition = source.FailedEntries > 0 || source.ProducedEntries == 0 && source.ExcludedEntries == 0 ||
                source.ProducedEntries > 0 && (source.ExcludedEntries > 0 || targetArtifacts.Length == 0)
                ? ArtifactDisposition.Unaccounted
                : source.ExcludedEntries > 0 ? ArtifactDisposition.Excluded : ResolveDisposition(source.EdgeIds, edges);
            var reason = disposition switch
            {
                ArtifactDisposition.Excluded => "Projection journal records an explicit graph exclusion.",
                ArtifactDisposition.Unaccounted => "Projection journal does not establish one complete, successful graph disposition.",
                _ => null
            };
            dispositions.Add(new ArtifactDispositionRecord(source.Artifact, disposition, targetArtifacts, reason,
                nodeIds[source.NodeKey], targetNodeIds));
        }

        var sourceReferences = new List<GraphArtifactReference>();
        await foreach (var source in workspace.ReadSourceFactsAsync(cancellationToken)
            .WithCancellation(cancellationToken).ConfigureAwait(false))
            sourceReferences.Add(new GraphArtifactReference(nodeIds[source.NodeKey], source.Artifact));
        var dispositionIssues = new ArtifactDispositionLedger(dispositions).ValidateScopedCoverage(sourceReferences);
        foreach (var issue in dispositionIssues)
        {
            var source = sourceReferences.SingleOrDefault(item => item.Artifact.Id == issue.ArtifactId && item.NodeId == issue.NodeId);
            findings.Add(new VerificationFinding(EvidenceType.Accounting, EvidenceResult.Fail, EvidenceSeverity.Error,
                issue.Code.ToString(), issue.Message, $"disposition-coverage:{issue.NodeId}:{issue.ArtifactId.Value}",
                source is null ? [new EvidenceReference(checkpointId: binding.CheckpointId)] :
                    [new EvidenceReference(checkpointId: binding.CheckpointId), new EvidenceReference(artifactId: source.Artifact.Id, graphNodeId: source.NodeId)]));
        }

        var observedTargets = new List<GraphArtifactReference>();
        await foreach (var target in workspace.ReadActualTargetsAsync(cancellationToken)
            .WithCancellation(cancellationToken).ConfigureAwait(false))
            for (var count = 0; count < target.ActualCount; count++)
                observedTargets.Add(new GraphArtifactReference(nodeIds[target.NodeKey], target.Artifact));
        foreach (var issue in new LineageLedger(lineage).ValidateScopedCoverage(observedTargets))
        {
            var nodeId = issue.NodeId;
            findings.Add(new VerificationFinding(EvidenceType.Accounting, EvidenceResult.Fail, EvidenceSeverity.Error,
                issue.Code.ToString(), issue.Message, $"lineage-coverage:{nodeId}:{issue.ArtifactId.Value}",
                nodeId is null
                    ? [new EvidenceReference(checkpointId: binding.CheckpointId), new EvidenceReference(projectionRunId: binding.ProjectionRunId)]
                    : [new EvidenceReference(checkpointId: binding.CheckpointId), new EvidenceReference(projectionRunId: binding.ProjectionRunId),
                       new EvidenceReference(artifactId: issue.ArtifactId, graphNodeId: nodeId.Value)]));
        }

        var failedRuleIds = findings.Where(finding => finding.Result == EvidenceResult.Fail &&
                finding.Severity is EvidenceSeverity.Critical or EvidenceSeverity.Error)
            .Select(finding => finding.RuleId).ToHashSet();
        var warningRuleIds = findings.Where(finding => finding.Result == EvidenceResult.Warning ||
                finding.Result == EvidenceResult.Fail && finding.Severity == EvidenceSeverity.Warning)
            .Select(finding => finding.RuleId).ToHashSet();
        var failureCount = failedRuleIds.Count;
        var warningCount = warningRuleIds.Count;
        var outcome = failureCount > 0 ? VerificationOutcome.Failed : warningCount > 0
            ? VerificationOutcome.PassedWithWarnings : VerificationOutcome.Passed;
        var records = findings.Select(finding => ToEvidenceRecord(verificationRunId, finding)).ToList();
        var summaryRule = new RuleId(SystemRuleId);
        var summaryId = StableEvidenceId(verificationRunId, summaryRule, "verification-summary");
        records.Add(new EvidenceRecord(summaryId, verificationRunId, EvidenceType.Decision, summaryRule, "1",
            outcome == VerificationOutcome.Failed ? EvidenceResult.Fail : outcome == VerificationOutcome.PassedWithWarnings
                ? EvidenceResult.Warning : EvidenceResult.Pass,
            records.Select(record => new EvidenceReference(evidenceId: record.Id)),
            $"Verification completed with outcome {outcome}; {findings.Count.ToString(CultureInfo.InvariantCulture)} findings were evaluated.",
            DateTimeOffset.UtcNow, new EvidenceValue(new StringValue("all-configured-rules-evaluated")),
            new EvidenceValue(new StringValue(outcome.ToString())), EvidenceSeverity.Error, "VerificationSummary"));
        var evidenceGraph = new EvidenceGraph(verificationRunId, records);
        var runtime = new VerificationRuntimeFingerprint(proofShiftVersion, binding, ruleSet.ProviderVersions,
            ruleSet.Definitions.Select(definition => new KeyValuePair<string, string>(definition.Id.Value, definition.Version)));
        var completedAt = DateTimeOffset.UtcNow;
        var run = new VerificationRunRecord(verificationRunId, VerificationRunState.Complete, outcome, binding,
            ruleSet.Fingerprint, runtime, evidenceGraph.Fingerprint, startedAt, completedAt,
            ruleSet.Rules.Count + 1,
            Math.Max(0, ruleSet.Rules.Count + 1 - failureCount - warningCount),
            failureCount, warningCount, 0);
        return new VerificationResult(run, evidenceGraph, dispositions, lineage, findings, terminalJournalEntries);
    }

    public async Task<ExternalVerificationResult> VerifyExternalTargetAsync(
        LoadedProjectConfiguration configuration,
        MigrationGraph graph,
        ExternalMigrationObservation observation,
        VerificationRuleSet ruleSet,
        string temporaryDirectory,
        string proofShiftVersion,
        IEnumerable<VerificationTargetRuntime> targets,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(ruleSet);
        ArgumentNullException.ThrowIfNull(targets);
        var startedAt = DateTimeOffset.UtcNow;
        var targetRuntimes = targets.OrderBy(target => target.NodeKey, StringComparer.Ordinal).ToArray();
        ValidateExternalObservation(configuration, graph, observation);
        ValidateExternalTargetRuntimes(configuration, graph, observation, targetRuntimes);

        await using var checkpoint = await _snapshotStore.OpenCompleteAsync(
            observation.CheckpointId.Value.ToString("N", CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
        ValidateExternalCheckpoint(checkpoint.Manifest, configuration, graph, observation);

        var runId = new RunId(Guid.NewGuid());
        await using var workspace = await SqliteVerificationWorkspace.CreateAsync(temporaryDirectory, cancellationToken).ConfigureAwait(false);
        var sourceNodes = graph.Nodes.Where(node => node.Type == MigrationNodeType.Source)
            .OrderBy(node => node.Name, StringComparer.Ordinal).ToArray();
        foreach (var sourceNode in sourceNodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await foreach (var source in checkpoint.ReadAsync(sourceNode.Name, sourceNode.Selector, cancellationToken)
                .WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                ValidateRecordForNode(source, sourceNode);
                await workspace.AddSourceArtifactAsync(sourceNode.Name, source, cancellationToken).ConfigureAwait(false);
                foreach (var edge in graph.Edges.Where(edge => edge.Sources.Contains(sourceNode.Id))
                    .OrderBy(edge => edge.Id.Value))
                {
                    if (edge.Operation.Type is MigrationOperationType.Exclude or MigrationOperationType.Relationship) continue;
                    if (edge.Sources.Count != 1)
                        throw ContextMismatch("External verification cannot replay a multi-source edge under current graph semantics.");
                    foreach (var targetNode in edge.Targets.Select(id => graph.Nodes.Single(node => node.Id == id))
                        .OrderBy(node => node.Name, StringComparer.Ordinal))
                    {
                        var expected = GraphTransformationRuntime.Transform(source, edge, targetNode);
                        await workspace.AddExpectedTargetAsync(targetNode.Name, expected, sourceNode.Name, source, edge,
                            cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }
        ValidateExternalSourceCoverage(checkpoint.Manifest, workspace.SourceArtifactCount);

        using var targetFingerprint = MaterializedTargetFingerprint.CreateBuilder(graph.GraphHash);
        foreach (var targetRuntime in targetRuntimes)
        {
            var node = graph.Nodes.Single(item => item.Name == targetRuntime.NodeKey);
            await foreach (var actual in targetRuntime.Connector.ReadAsync(new ReadRequest(targetRuntime.Context, node.Selector), cancellationToken)
                .WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                ValidateRecordForNode(actual, node);
                if (GraphTargetIdentity.Create(actual, node.Selector) != actual.Artifact.Identity ||
                    StableArtifactIdentity.CreateArtifactId(actual.Artifact.SystemId.Value, actual.Artifact.EndpointId.Value,
                        actual.Artifact.ArtifactType, actual.Artifact.Identity) != actual.Artifact.Id.Value)
                    throw ContextMismatch("Externally populated target connector returned an identity inconsistent with its configured selector.");
                targetFingerprint.Add(node.Name, actual);
                await workspace.AddTargetObservationAsync(node.Name, actual, cancellationToken).ConfigureAwait(false);
            }
        }
        var observedTarget = targetFingerprint.Finish();
        var context = new VerificationExecutionContext(configuration, graph, observation, runId, workspace);
        var findings = new List<VerificationFinding>
        {
            new(EvidenceType.Observation, EvidenceResult.Pass, EvidenceSeverity.Info,
                "ExternalTargetObserved",
                "ProofShift independently observed externally populated target state; this observation does not claim ProofShift executed the migration.",
                "external-target-observation",
                context.BindingReferences,
                new EvidenceValue(new StringValue("externally-populated-target")),
                new EvidenceValue(new StringValue($"sha256:{observedTarget.Fingerprint}:{observedTarget.RecordCount.ToString(CultureInfo.InvariantCulture)}")),
                ruleId: new RuleId("proofshift.verification.external-target-observation"), ruleVersion: "1")
        };
        foreach (var rule in ruleSet.Rules)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await foreach (var finding in rule.EvaluateAsync(context, cancellationToken)
                    .WithCancellation(cancellationToken).ConfigureAwait(false))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    findings.Add(finding);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                throw new VerificationRuleException(VerificationIssueCodes.RuleExecutionFailure,
                    $"Verification rule '{rule.Id.Value}' failed during external target evaluation: {exception.GetType().Name}.");
            }
        }

        var nodeIds = graph.Nodes.ToDictionary(node => node.Name, node => node.Id, StringComparer.Ordinal);
        var lineage = new List<LineageRecord>();
        await foreach (var item in workspace.ReadGraphDerivedLineageAsync(graph.GraphHash, nodeIds, cancellationToken)
            .WithCancellation(cancellationToken).ConfigureAwait(false)) lineage.Add(item);
        var lineageBySource = BuildLineageBySource(lineage, graph.Nodes.ToDictionary(node => node.Id, node => node.Name));
        var edgeMap = graph.Edges.ToDictionary(edge => edge.Id);
        var sourceFacts = workspace.ReadGraphDerivedSourceFactsAsync(cancellationToken);
        var dispositions = new List<ArtifactDispositionRecord>();
        var sourceReferences = new List<GraphArtifactReference>();
        await foreach (var source in sourceFacts.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            var sourceNodeId = nodeIds[source.NodeKey];
            sourceReferences.Add(new GraphArtifactReference(sourceNodeId, source.Artifact));
            var mapped = lineageBySource.GetValueOrDefault((source.NodeKey, source.Artifact.Id.Value), [])
                .DistinctBy(item => (item.TargetNodeId, item.Target.Id)).ToArray();
            var disposition = source.ProducedEntries == 0 || mapped.Length == 0
                ? ArtifactDisposition.Unaccounted
                : ResolveDisposition(source.EdgeIds, edgeMap);
            var reason = disposition == ArtifactDisposition.Unaccounted
                ? "Checkpoint and migration graph do not establish an expected target mapping."
                : null;
            dispositions.Add(new ArtifactDispositionRecord(source.Artifact, disposition,
                mapped.Select(item => item.Target).ToArray(), reason, sourceNodeId,
                mapped.Select(item => item.TargetNodeId!.Value).ToArray()));
        }
        foreach (var issue in new ArtifactDispositionLedger(dispositions).ValidateScopedCoverage(sourceReferences))
        {
            var scopedSource = sourceReferences.SingleOrDefault(item => item.Artifact.Id == issue.ArtifactId && item.NodeId == issue.NodeId);
            findings.Add(new VerificationFinding(EvidenceType.Accounting, EvidenceResult.Fail, EvidenceSeverity.Error,
                issue.Code.ToString(), issue.Message, $"external-disposition:{issue.NodeId}:{issue.ArtifactId.Value}",
                scopedSource is null ? context.BindingReferences : [.. context.BindingReferences,
                    new EvidenceReference(artifactId: scopedSource.Artifact.Id, graphNodeId: scopedSource.NodeId)]));
        }

        var observedTargets = new List<GraphArtifactReference>();
        await foreach (var target in workspace.ReadActualTargetsAsync(cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
            for (var count = 0; count < target.ActualCount; count++)
                observedTargets.Add(new GraphArtifactReference(nodeIds[target.NodeKey], target.Artifact));
        foreach (var issue in new LineageLedger(lineage).ValidateScopedCoverage(observedTargets))
        {
            var inputs = issue.NodeId is { } nodeId
                ? context.BindingReferences.Append(new EvidenceReference(artifactId: issue.ArtifactId, graphNodeId: nodeId)).ToArray()
                : context.BindingReferences.ToArray();
            findings.Add(new VerificationFinding(EvidenceType.Accounting, EvidenceResult.Fail, EvidenceSeverity.Error,
                issue.Code.ToString(), issue.Message, $"external-lineage:{issue.NodeId}:{issue.ArtifactId.Value}", inputs));
        }

        var failedRuleIds = findings.Where(finding => finding.Result == EvidenceResult.Fail &&
                finding.Severity is EvidenceSeverity.Critical or EvidenceSeverity.Error)
            .Select(finding => finding.RuleId).ToHashSet();
        var warningRuleIds = findings.Where(finding => finding.Result == EvidenceResult.Warning ||
                finding.Result == EvidenceResult.Fail && finding.Severity == EvidenceSeverity.Warning)
            .Select(finding => finding.RuleId).ToHashSet();
        var outcome = failedRuleIds.Count > 0 ? VerificationOutcome.Failed : warningRuleIds.Count > 0
            ? VerificationOutcome.PassedWithWarnings : VerificationOutcome.Passed;
        var records = findings.Select(finding => ToEvidenceRecord(runId, finding)).ToList();
        var summaryRule = new RuleId(SystemRuleId);
        records.Add(new EvidenceRecord(StableEvidenceId(runId, summaryRule, "external-verification-summary"), runId,
            EvidenceType.Decision, summaryRule, "1", outcome == VerificationOutcome.Failed ? EvidenceResult.Fail :
                outcome == VerificationOutcome.PassedWithWarnings ? EvidenceResult.Warning : EvidenceResult.Pass,
            records.Select(record => new EvidenceReference(evidenceId: record.Id)),
            $"External target verification completed with outcome {outcome}; ProofShift execution was not observed.",
            DateTimeOffset.UtcNow, new EvidenceValue(new StringValue("all-configured-rules-evaluated")),
            new EvidenceValue(new StringValue(outcome.ToString())), EvidenceSeverity.Error, "ExternalVerificationSummary"));
        var evidenceGraph = new EvidenceGraph(runId, records);
        var runtimeFingerprint = ExternalRuntimeFingerprint(configuration, graph, observation, ruleSet, proofShiftVersion);
        var run = new ExternalVerificationRunRecord(runId, observation.ObservationId, observation.ObservationRunId,
            configuration.ConfigurationHash, graph.GraphHash, observation.CheckpointId, observation.CheckpointManifestHash,
            observation.SourceFingerprint, observedTarget.Fingerprint, workspace.SourceArtifactCount,
            observedTarget.RecordCount, ruleSet.Fingerprint, runtimeFingerprint, evidenceGraph.Fingerprint, outcome,
            ruleSet.Rules.Count + 1, failedRuleIds.Count, warningRuleIds.Count, startedAt, DateTimeOffset.UtcNow);
        return new ExternalVerificationResult(run, evidenceGraph, dispositions, lineage, findings);
    }

    private static void ValidateExternalObservation(LoadedProjectConfiguration configuration, MigrationGraph graph,
        ExternalMigrationObservation observation)
    {
        if (configuration.ConfigurationHash != observation.ConfigurationHash || graph.GraphHash != observation.GraphHash ||
            graph.Validate().Count > 0)
            throw ContextMismatch("External migration observation configuration or graph binding is invalid.");
        var expectedNodes = graph.Nodes.Where(node => node.Type is MigrationNodeType.Target or MigrationNodeType.Archive)
            .Select(node => node.Name).Order(StringComparer.Ordinal).ToArray();
        if (!expectedNodes.SequenceEqual(observation.Targets.Select(target => target.NodeKey).Order(StringComparer.Ordinal), StringComparer.Ordinal))
            throw ContextMismatch("External observation must identify every configured target graph node exactly once.");
    }

    private static void ValidateExternalTargetRuntimes(LoadedProjectConfiguration configuration, MigrationGraph graph,
        ExternalMigrationObservation observation, IReadOnlyCollection<VerificationTargetRuntime> targets)
    {
        if (!graph.Nodes.Where(node => node.Type is MigrationNodeType.Target or MigrationNodeType.Archive)
            .Select(node => node.Name).Order(StringComparer.Ordinal)
            .SequenceEqual(targets.Select(target => target.NodeKey).Order(StringComparer.Ordinal), StringComparer.Ordinal))
            throw ContextMismatch("External verification must observe every configured target graph node exactly once.");
        var endpoints = observation.Targets.ToDictionary(target => target.NodeKey, StringComparer.Ordinal);
        foreach (var target in targets)
        {
            var node = graph.Nodes.Single(candidate => candidate.Name == target.NodeKey);
            var system = configuration.Systems.SingleOrDefault(candidate => candidate.Id == node.SystemId)
                ?? throw ContextMismatch("External target graph node references an unknown system.");
            var endpoint = system.StorageEndpoints.SingleOrDefault(candidate => candidate.Id == node.EndpointId)
                ?? throw ContextMismatch("External target graph node references an unknown endpoint.");
            var observedEndpoint = endpoints[node.Name];
            if (system.Role != SystemRole.ShadowTarget || observedEndpoint.SystemId != node.SystemId ||
                observedEndpoint.EndpointId != node.EndpointId || observedEndpoint.ConnectorId != endpoint.Connector ||
                target.Connector.Id != endpoint.Connector || observedEndpoint.ConnectorVersion != target.Connector.Version ||
                target.Context.RunId != observation.ObservationRunId || target.Context.Role != system.Role ||
                target.Context.ConnectorContext.NodeKey != node.Name || target.Context.ConnectorContext.SystemKey != system.Id.Value ||
                target.Context.ConnectorContext.EndpointKey != endpoint.Id.Value ||
                target.Context.ConnectorContext.Connector != endpoint.Connector ||
                target.Context.ConnectorContext.SemanticType != node.SemanticType)
                throw ContextMismatch("External target runtime does not match the declared observation endpoint and graph node.");
        }
    }

    private static void ValidateExternalCheckpoint(SourceCheckpoint checkpoint, LoadedProjectConfiguration configuration,
        MigrationGraph graph, ExternalMigrationObservation observation)
    {
        if (checkpoint.Status != CheckpointStatus.Complete || !checkpoint.Replayable ||
            checkpoint.Id != observation.CheckpointId || checkpoint.ManifestHash != observation.CheckpointManifestHash ||
            checkpoint.SourceFingerprint != observation.SourceFingerprint ||
            checkpoint.ConfigurationHash != configuration.ConfigurationHash || checkpoint.ConfigurationHash != observation.ConfigurationHash ||
            checkpoint.GraphHash != graph.GraphHash || checkpoint.GraphHash != observation.GraphHash)
            throw ContextMismatch("External verification checkpoint is incomplete or does not match its source/configuration/graph binding.");
        var sourceNodes = graph.Nodes.Where(node => node.Type == MigrationNodeType.Source).ToDictionary(node => node.Name, StringComparer.Ordinal);
        if (!checkpoint.Endpoints.Select(endpoint => endpoint.SourceNodeKey).Order(StringComparer.Ordinal)
            .SequenceEqual(sourceNodes.Keys.Order(StringComparer.Ordinal), StringComparer.Ordinal))
            throw ContextMismatch("External verification checkpoint source-node coverage does not match the configured graph.");
        foreach (var endpoint in checkpoint.Endpoints)
        {
            var node = sourceNodes[endpoint.SourceNodeKey];
            if (endpoint.SystemId != node.SystemId || endpoint.EndpointId != node.EndpointId ||
                endpoint.SelectorHash != SnapshotFingerprints.SelectorHash(node.Selector) ||
                !endpoint.IdentityFields.SequenceEqual(node.Selector.IdentityFields, StringComparer.Ordinal))
                throw ContextMismatch("External verification checkpoint endpoint selector does not match the configured source node.");
        }
    }

    private static void ValidateExternalSourceCoverage(SourceCheckpoint checkpoint, long actualSourceCount)
    {
        if (checkpoint.ArtifactCount != actualSourceCount)
            throw ContextMismatch("External verification replay source coverage does not match the checkpoint manifest.");
    }

    private static string ExternalRuntimeFingerprint(LoadedProjectConfiguration configuration, MigrationGraph graph,
        ExternalMigrationObservation observation, VerificationRuleSet ruleSet, string proofShiftVersion)
    {
        var parts = new List<string>
        {
            "proofshift-verification-external-runtime-v1", proofShiftVersion, configuration.ConfigurationHash,
            graph.GraphHash, observation.CheckpointId.Value.ToString("D", CultureInfo.InvariantCulture),
            observation.CheckpointManifestHash, observation.SourceFingerprint, observation.ObservationId,
            observation.ObservationRunId.Value.ToString("D", CultureInfo.InvariantCulture), ruleSet.Fingerprint
        };
        foreach (var target in observation.Targets.OrderBy(item => item.NodeKey, StringComparer.Ordinal))
        {
            parts.Add(target.NodeKey);
            parts.Add(target.SystemId.Value);
            parts.Add(target.EndpointId.Value);
            parts.Add(target.ConnectorId.Value);
            parts.Add(target.ConnectorVersion);
        }
        foreach (var provider in ruleSet.ProviderVersions.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            parts.Add(provider.Key);
            parts.Add(provider.Value);
        }
        foreach (var definition in ruleSet.Definitions.OrderBy(item => item.Id.Value, StringComparer.Ordinal))
        {
            parts.Add(definition.Id.Value);
            parts.Add(definition.Version);
        }
        return Hash(Encoding.UTF8.GetBytes(string.Join('\n', parts)));
    }

    private static void ValidateConfigurationBinding(LoadedProjectConfiguration configuration, MigrationGraph graph,
        ProjectionVerificationBinding binding)
    {
        if (configuration.ConfigurationHash != binding.ConfigurationHash || graph.GraphHash != binding.GraphHash ||
            binding.ProjectionStatus != "succeeded")
            throw ContextMismatch("Verification configuration or graph differs from the projection binding.");
        if (graph.Validate().Count > 0) throw ContextMismatch("Verification graph is invalid.");
    }

    private static void ValidateTargetRuntimes(LoadedProjectConfiguration configuration, MigrationGraph graph,
        ProjectionVerificationBinding binding, IReadOnlyCollection<VerificationTargetRuntime> targets)
    {
        var expectedNodes = graph.Nodes.Where(node => node.Type is MigrationNodeType.Target or MigrationNodeType.Archive)
            .Select(node => node.Name).Order(StringComparer.Ordinal).ToArray();
        if (!expectedNodes.SequenceEqual(targets.Select(target => target.NodeKey).Order(StringComparer.Ordinal), StringComparer.Ordinal))
            throw ContextMismatch("Verification must read every configured target graph node exactly once.");
        foreach (var target in targets)
        {
            var node = graph.Nodes.Single(candidate => candidate.Name == target.NodeKey);
            var system = configuration.Systems.SingleOrDefault(candidate => candidate.Id == node.SystemId)
                ?? throw ContextMismatch("Target graph node references an unknown system.");
            var endpoint = system.StorageEndpoints.SingleOrDefault(candidate => candidate.Id == node.EndpointId)
                ?? throw ContextMismatch("Target graph node references an unknown endpoint.");
            if (system.Role != SystemRole.ShadowTarget || target.Connector.Id != endpoint.Connector ||
                target.Context.RunId != binding.ProjectionRunId || target.Context.ConnectorContext.NodeKey != node.Name ||
                target.Context.ConnectorContext.SystemKey != system.Id.Value || target.Context.ConnectorContext.EndpointKey != endpoint.Id.Value ||
                target.Context.ConnectorContext.Connector != endpoint.Connector ||
                target.Context.ConnectorContext.SemanticType != node.SemanticType)
                throw ContextMismatch("Shadow target runtime does not match the configured node and projection run.");
            if (!binding.ConnectorVersions.TryGetValue($"target:{node.Name}", out var version) || version != target.Connector.Version)
                throw ContextMismatch("Shadow target connector version differs from the projection binding.");
        }
    }

    private static void ValidateCheckpoint(SourceCheckpoint checkpoint, LoadedProjectConfiguration configuration,
        MigrationGraph graph, ProjectionVerificationBinding binding)
    {
        if (checkpoint.Status != CheckpointStatus.Complete || !checkpoint.Replayable || checkpoint.ManifestHash is null ||
            checkpoint.SourceFingerprint is null || checkpoint.Id != binding.CheckpointId ||
            checkpoint.ManifestHash != binding.CheckpointManifestHash || checkpoint.SourceFingerprint != binding.SourceFingerprint ||
            checkpoint.ConfigurationHash != configuration.ConfigurationHash || checkpoint.GraphHash != graph.GraphHash ||
            checkpoint.ConfigurationHash != binding.ConfigurationHash || checkpoint.GraphHash != binding.GraphHash)
            throw ContextMismatch("Checkpoint is incomplete or its manifest, source fingerprint, configuration, or graph binding differs.");

        var sourceNodes = graph.Nodes.Where(node => node.Type == MigrationNodeType.Source).ToDictionary(node => node.Name, StringComparer.Ordinal);
        if (!checkpoint.Endpoints.Select(endpoint => endpoint.SourceNodeKey).Order(StringComparer.Ordinal)
            .SequenceEqual(sourceNodes.Keys.Order(StringComparer.Ordinal), StringComparer.Ordinal))
            throw ContextMismatch("Checkpoint source-node coverage does not exactly match the configured graph.");
        foreach (var endpoint in checkpoint.Endpoints)
        {
            var node = sourceNodes[endpoint.SourceNodeKey];
            if (endpoint.SystemId != node.SystemId || endpoint.EndpointId != node.EndpointId ||
                endpoint.SelectorHash != SnapshotFingerprints.SelectorHash(node.Selector) ||
                !endpoint.IdentityFields.SequenceEqual(node.Selector.IdentityFields, StringComparer.Ordinal) ||
                !binding.ConnectorVersions.TryGetValue($"source:{node.Name}", out var version) || version != endpoint.ConnectorVersion)
                throw ContextMismatch("Checkpoint endpoint selector or graph-node binding differs from configuration.");
        }
    }

    private static void ValidateSourceRecordCoverage(SourceCheckpoint checkpoint, long actualSourceCount)
    {
        if (checkpoint.ArtifactCount != actualSourceCount)
            throw ContextMismatch("Checkpoint replay source coverage does not match the checkpoint manifest count.");
    }

    private static async Task<string> ValidateProjectionManifestAsync(string projectDirectory,
        ProjectionVerificationBinding binding, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(projectDirectory);
        var runDirectory = Path.Combine(root, ".proofshift", "projections", binding.ProjectionRunId.Value.ToString("N", CultureInfo.InvariantCulture));
        var manifestPath = Path.Combine(runDirectory, "run.json");
        ProjectionManifestDocument manifest;
        try
        {
            await using var stream = File.OpenRead(manifestPath);
            manifest = await JsonSerializer.DeserializeAsync<ProjectionManifestDocument>(stream, ProjectionJsonOptions(), cancellationToken)
                .ConfigureAwait(false) ?? throw ContextMismatch("Projection run manifest is empty.");
        }
        catch (VerificationRuleException) { throw; }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            throw ContextMismatch("Projection run manifest is missing or unreadable.");
        }

        var manifestHash = Hash(JsonSerializer.SerializeToUtf8Bytes(manifest with { ManifestHash = null }, ProjectionJsonOptions()));
        if (manifest.Format != "proofshift-projection-run-v1" || manifest.ManifestHash != binding.ProjectionManifestHash ||
            manifestHash != manifest.ManifestHash || manifest.RunId != binding.ProjectionRunId.Value.ToString("D", CultureInfo.InvariantCulture) ||
            manifest.Status != "succeeded" || manifest.FailureCount != 0 || manifest.ConfigurationHash != binding.ConfigurationHash ||
            manifest.GraphHash != binding.GraphHash || manifest.ProjectionFingerprint != binding.ProjectionFingerprint ||
            manifest.ProjectionFingerprintVersion != binding.ProjectionFingerprintVersion ||
            manifest.CheckpointId is null || !Guid.TryParse(manifest.CheckpointId, out var checkpointId) || checkpointId != binding.CheckpointId.Value ||
            manifest.CheckpointManifestHash != binding.CheckpointManifestHash || manifest.CheckpointSourceFingerprint != binding.SourceFingerprint ||
            manifest.SourceArtifactCount != binding.ProjectionSourceCount || manifest.TargetArtifactCount != binding.ProjectionTargetCount)
            throw ContextMismatch("Projection run manifest is incomplete or does not match the requested verification binding.");

        var journalPath = Path.GetFullPath(Path.Combine(root, manifest.JournalPath.Replace('/', Path.DirectorySeparatorChar)));
        if (!ConnectorPathUtilities.IsContained(runDirectory, journalPath) || !File.Exists(journalPath) ||
            !string.Equals(manifest.JournalPath, binding.JournalPath, StringComparison.Ordinal))
            throw ContextMismatch("Projection journal path is missing, unbound, or outside its run directory.");
        return journalPath;
    }

    private static async IAsyncEnumerable<ParsedJournalEntry> ReadJournalAsync(string path, RunId runId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line)) continue;
            JournalDocument item;
            try
            {
                item = JsonSerializer.Deserialize<JournalDocument>(line, ProjectionJsonOptions())
                    ?? throw ContextMismatch("Projection journal contains an empty entry.");
            }
            catch (JsonException) { throw ContextMismatch("Projection journal contains malformed JSON."); }
            if (!Guid.TryParse(item.RunId, out var parsedRun) || parsedRun != runId.Value ||
                !Guid.TryParse(item.EdgeId, out var edgeGuid))
                throw ContextMismatch("Projection journal entry identifies another run or an invalid edge.");
            ArtifactReference? target = item.Target is null ? null : ToArtifact(item.Target);
            yield return new ParsedJournalEntry(item.Result.ToLowerInvariant(), item.TargetNode, target,
                item.Sources.Select(ToArtifact).ToArray(), new MigrationEdgeId(edgeGuid), item.Edge, item.EdgeVersion, item.FailureCode);
        }
    }

    private static void ValidateRecordForNode(RecordEnvelope record, MigrationNode node)
    {
        if (record.SemanticType != node.SemanticType || record.Artifact.SystemId != node.SystemId ||
            record.Artifact.EndpointId != node.EndpointId)
            throw ContextMismatch("Checkpoint or shadow record does not match its graph node identity.");
    }

    private static bool ArtifactMatches(ArtifactReference left, ArtifactReference right) =>
        left.Id == right.Id && left.SystemId == right.SystemId && left.EndpointId == right.EndpointId &&
        left.ArtifactType == right.ArtifactType && left.Identity == right.Identity;

    private static Dictionary<(string NodeKey, string ArtifactId), List<LineageRecord>> BuildLineageBySource(
        IEnumerable<LineageRecord> lineage, Dictionary<MigrationNodeId, string> nodeKeys)
    {
        var result = new Dictionary<(string, string), List<LineageRecord>>();
        foreach (var item in lineage)
        {
            for (var index = 0; index < item.Sources.Count; index++)
            {
                var key = (nodeKeys[item.SourceNodeIds[index]], item.Sources[index].Id.Value);
                if (!result.TryGetValue(key, out var records)) result.Add(key, records = []);
                records.Add(item);
            }
        }
        return result;
    }

    private static ArtifactDisposition ResolveDisposition(IEnumerable<MigrationEdgeId> edgeIds,
        Dictionary<MigrationEdgeId, MigrationEdge> edges)
    {
        var operations = edgeIds.Select(id => edges[id].Operation.Type).ToArray();
        if (operations.Any(operation => operation == MigrationOperationType.Archive)) return ArtifactDisposition.Archived;
        if (operations.Any(operation => operation is MigrationOperationType.Derive or MigrationOperationType.Aggregate)) return ArtifactDisposition.Derived;
        if (operations.Any(operation => operation is MigrationOperationType.Transform or MigrationOperationType.Map or MigrationOperationType.Split)) return ArtifactDisposition.Transformed;
        return ArtifactDisposition.Migrated;
    }

    private static EvidenceRecord ToEvidenceRecord(RunId runId, VerificationFinding finding)
    {
        var ruleId = finding.RuleId;
        var inputs = finding.Inputs.ToList();
        if (inputs.All(input => input.RuleId != ruleId)) inputs.Add(new EvidenceReference(ruleId: ruleId));
        return new EvidenceRecord(StableEvidenceId(runId, ruleId, finding.StableKey), runId, finding.Type, ruleId,
            finding.RuleVersion, finding.Result, inputs, finding.Explanation, DateTimeOffset.UtcNow,
            finding.Expected, finding.Actual, finding.Severity, finding.Code);
    }

    private static EvidenceId StableEvidenceId(RunId runId, RuleId ruleId, string stableKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{runId.Value:D}\n{ruleId.Value}\n{stableKey}"));
        return new EvidenceId(new Guid(bytes.AsSpan(0, 16)));
    }

    private static ArtifactReference ToArtifact(ArtifactDocument artifact) => new(new ArtifactId(artifact.Id),
        new SystemId(artifact.System), new StorageEndpointId(artifact.Endpoint), artifact.Type, artifact.Identity);

    private static JsonSerializerOptions ProjectionJsonOptions() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static VerificationRuleException ContextMismatch(string message) =>
        new(VerificationIssueCodes.ContextMismatch, message);

    private sealed record ParsedJournalEntry(string Result, string? TargetNode, ArtifactReference? Target,
        IReadOnlyCollection<ArtifactReference> Sources, MigrationEdgeId EdgeId, string EdgeName, string EdgeVersion,
        string? FailureCode);

    private sealed record ArtifactDocument(string Id, string System, string Endpoint, string Type, string Identity);

    private sealed record JournalDocument(string RunId, ArtifactDocument? Target, string? TargetNode,
        ArtifactDocument[] Sources, string EdgeId, string Edge, string EdgeVersion,
        ProjectionTransformationDocument[] Transformations, string? RecoveryMode, bool RecoveryRequiresSnapshot,
        string? RecoveryStrategy, string Result, string? FailureCode);

    private sealed record ProjectionTransformationDocument(string Type, string Version, string? SourceField, string? TargetField);

    private sealed record ProjectionManifestDocument(string Format, string? ManifestHash, string RunId, string Status,
        string ProjectId, string ConfigurationHash, string GraphHash, string StartedAt, string? CompletedAt,
        long SourceArtifactCount, long TargetArtifactCount, long FailureCount, string? FailureCode,
        string? ProjectionFingerprint, string ProjectionFingerprintVersion, string JournalPath, string? CheckpointId,
        string? CheckpointSourceFingerprint, string? CheckpointManifestHash, SortedDictionary<string, string> ConnectorVersions,
        string[] ShadowDestinations);
}