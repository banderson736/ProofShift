using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ProofShift.Configuration;
using ProofShift.Connectors.Abstractions;
using ProofShift.Domain;
using ProofShift.Engine;
using ProofShift.Evidence;
using ProofShift.Graph;
using ProofShift.Projection;
using ProofShift.Snapshots;
using ProofShift.Verification;

namespace ProofShift.Recovery;

public sealed class RecoveryService
{
    private readonly IMaterializedSnapshotStore _sourceCheckpointStore;
    private readonly RecoveryCompensatorRegistry _compensators;

    public RecoveryService(IMaterializedSnapshotStore sourceCheckpointStore, RecoveryCompensatorRegistry compensators)
    {
        _sourceCheckpointStore = sourceCheckpointStore ?? throw new ArgumentNullException(nameof(sourceCheckpointStore));
        _compensators = compensators ?? throw new ArgumentNullException(nameof(compensators));
    }

    public Task<RecoveryRunResult> AssessAndRehearseAsync(LoadedProjectConfiguration configuration,
        MigrationGraph graph, ProjectionVerificationBinding projectionBinding, VerificationResult verification,
        EffectiveRecoveryPolicy policy, IEnumerable<VerificationTargetRuntime> targets, string projectDirectory,
        CancellationToken cancellationToken) =>
        AssessAndRehearseAsync(configuration, graph, projectionBinding, verification, policy, targets,
            projectDirectory, performanceRecorder: null, cancellationToken);

    public async Task<RecoveryRunResult> AssessAndRehearseAsync(LoadedProjectConfiguration configuration,
        MigrationGraph graph, ProjectionVerificationBinding projectionBinding, VerificationResult verification,
        EffectiveRecoveryPolicy policy, IEnumerable<VerificationTargetRuntime> targets, string projectDirectory,
        PerformanceRecorder? performanceRecorder, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(projectionBinding);
        ArgumentNullException.ThrowIfNull(verification);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(targets);
        using var recoveryStage = performanceRecorder?.StartStage(PerformanceStageKind.Recovery, "recovery analysis and rehearsal");
        var startedAt = DateTimeOffset.UtcNow;
        var dryRunId = new DryRunId(Guid.NewGuid());
        var binding = new RecoveryExecutionBinding(verification.Run);
        ValidateBinding(configuration, graph, projectionBinding, verification);
        await using var ledger = await SqliteVerificationLedgerStore.OpenAsync(projectDirectory, verification.Ledger,
            cancellationToken).ConfigureAwait(false);
        var ledgerSummary = await ledger.ReadSummaryAsync(cancellationToken).ConfigureAwait(false);

        var manifest = await ProjectionRunManifestStore.ReadAsync(projectDirectory, projectionBinding.ProjectionRunId,
            cancellationToken).ConfigureAwait(false);
        if (manifest.Status != ProjectionStatus.Succeeded || manifest.ManifestHash != projectionBinding.ProjectionManifestHash ||
            manifest.GraphHash != graph.GraphHash || manifest.ConfigurationHash != configuration.ConfigurationHash ||
            manifest.ProjectionFingerprint != verification.Run.ProjectionFingerprint ||
            manifest.SourceArtifactCount != projectionBinding.ProjectionSourceCount ||
            manifest.TargetArtifactCount != projectionBinding.ProjectionTargetCount ||
            manifest.CheckpointId != projectionBinding.CheckpointId.Value.ToString("N", CultureInfo.InvariantCulture) ||
            manifest.CheckpointManifestHash != projectionBinding.CheckpointManifestHash ||
            manifest.CheckpointSourceFingerprint != projectionBinding.SourceFingerprint)
            throw new RecoveryException(RecoveryIssueCodes.ContextMismatch, "Projection manifest no longer matches the verified run.");

        await using var sourceCheckpoint = await _sourceCheckpointStore.OpenCompleteAsync(
            verification.Run.CheckpointId.Value.ToString("N", CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
        ValidateSourceCheckpoint(configuration, graph, verification.Run, sourceCheckpoint.Manifest, policy,
            projectionBinding.ConnectorVersions);

        var graphNodes = graph.Nodes.ToDictionary(node => node.Name, StringComparer.Ordinal);
        var graphNodeIds = graph.Nodes.ToDictionary(node => node.Name, node => node.Id, StringComparer.Ordinal);
        var graphNodesById = graph.Nodes.ToDictionary(node => node.Id);
        var graphEdges = graph.Edges.ToDictionary(edge => edge.Id);
        var targetRuntimes = targets.ToArray();
        foreach (var target in targetRuntimes)
        {
            if (!projectionBinding.ConnectorVersions.TryGetValue($"target:{target.NodeKey}", out var expectedVersion) ||
                expectedVersion != target.Connector.Version)
                throw new RecoveryException(RecoveryIssueCodes.ContextMismatch, "Target connector version differs from the verified projection context.");
        }
        var targetGroups = BuildTargetGroups(configuration, graph, projectionBinding, targetRuntimes);
        var issues = new List<RecoveryAssessmentIssue>();

        async Task<TargetState> ReadTargetStateMeasuredAsync(string stageName, CancellationToken token)
        {
            using var stage = performanceRecorder?.StartStage(PerformanceStageKind.Recovery, stageName);
            var state = await ReadTargetStateAsync(graph, targetGroups, token).ConfigureAwait(false);
            stage?.AddArtifacts(state.RecordCount);
            return state;
        }

        if (!verification.Run.State.Equals(VerificationRunState.Complete) ||
            verification.Run.Outcome is not (VerificationOutcome.Passed or VerificationOutcome.PassedWithWarnings) ||
            verification.EvidenceGraph.Fingerprint != verification.Run.EvidenceFingerprint)
            issues.Add(new RecoveryAssessmentIssue(RecoveryIssueCodes.ContextMismatch,
                "Recovery requires a completed passing verification and its matching immutable Evidence Graph."));

        var ledgerCoverage = await ledger.ValidateCoverageAsync(cancellationToken).ConfigureAwait(false);
        if (ledgerSummary.SourceCount != sourceCheckpoint.Manifest.ArtifactCount ||
            ledgerSummary.DispositionCount != sourceCheckpoint.Manifest.ArtifactCount ||
            ledgerSummary.UnaccountedDispositionCount > 0 || !ledgerCoverage.IsValid)
            issues.Add(new RecoveryAssessmentIssue(QualificationIssueCodes.SourceAccountingFailed,
                "Verified source disposition coverage is incomplete or contains a failed/unaccounted artifact."));
        if (ledgerSummary.TargetCount != projectionBinding.ProjectionTargetCount ||
            ledgerSummary.LineageCount != projectionBinding.ProjectionTargetCount || !ledgerCoverage.IsValid)
            issues.Add(new RecoveryAssessmentIssue(QualificationIssueCodes.TargetLineageFailed,
                "Verified target lineage does not cover every projected graph-scoped target."));

        var targetState = await ReadTargetStateMeasuredAsync("recovery baseline target read-back", cancellationToken).ConfigureAwait(false);
        foreach (var group in targetGroups)
        {
            if (!targetState.GroupStates.TryGetValue(group.Key, out var groupState))
                throw new RecoveryException(RecoveryIssueCodes.ContextMismatch, "Recovery target group has no physical state fingerprint.");
            group.Fingerprint = groupState.Fingerprint;
            group.TargetCount = groupState.Count;
        }
        var targetMatchesVerification = targetState.Fingerprint == verification.Run.ProjectionFingerprint &&
            targetState.RecordCount == projectionBinding.ProjectionTargetCount;
        if (!targetMatchesVerification)
            issues.Add(new RecoveryAssessmentIssue(QualificationIssueCodes.StaleTarget,
                "Physical shadow target state changed after verification; re-verification is required before recovery qualification."));

        var needRecoveryCheckpoints = policy.RequireRecoveryRehearsal;
        if (!needRecoveryCheckpoints)
        {
            foreach (var edge in graph.Edges.Where(edge => edge.Recovery?.Mode == RecoveryMode.Restore))
            {
                if (await ledger.HasJournalEntryForEdgeAsync(edge.Id, "produced", cancellationToken).ConfigureAwait(false))
                {
                    needRecoveryCheckpoints = true;
                    break;
                }
            }
        }
        var checkpointBindings = new List<RecoveryShadowCheckpointBinding>();
        var groupIssues = new Dictionary<string, RecoveryAssessmentIssue>(StringComparer.Ordinal);
        var rehearsalStart = DateTimeOffset.UtcNow;
        var rehearsalState = RecoveryRehearsalState.Building;
        var rehearsalOutcome = RecoveryRehearsalOutcome.NotRequired;
        var mutatedArtifacts = 0L;
        string? restoredFingerprint = null;
        string? shadowCleanupFingerprint = null;
        string? rehearsalFailure = null;
        var rehearsalFailureCode = RecoveryIssueCodes.RehearsalFailed;
        var rehearsalStage = performanceRecorder?.StartStage(PerformanceStageKind.Recovery, "recovery rehearsal");
        try
        {
            if (needRecoveryCheckpoints && targetMatchesVerification)
            {
                foreach (var group in targetGroups)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (group.TargetCount == 0) continue;
                    if (group.Connector is not IShadowTargetRecoveryConnector recoveryConnector)
                    {
                        groupIssues[group.Key] = new RecoveryAssessmentIssue(RecoveryIssueCodes.MissingCapability,
                            $"Shadow connector '{group.Connector.Id.Value}' has no target recovery checkpoint capability.");
                        continue;
                    }

                    var checkpointId = new RecoveryCheckpointId(Guid.NewGuid());
                    var request = new ShadowRecoveryRequest(checkpointId, graph.GraphHash, group.Fingerprint,
                        group.TargetCount, group.Targets.Select(target => new ShadowRecoveryTarget(target.Context, graphNodes[target.NodeKey].Selector)));
                    using var checkpointStage = performanceRecorder?.StartStage(PerformanceStageKind.Recovery,
                        "target recovery checkpoint", group.Connector.Id.Value, nodeKey: group.Key);
                    var checkpoint = await recoveryConnector.CaptureRecoveryCheckpointAsync(request, cancellationToken).ConfigureAwait(false);
                    await recoveryConnector.ValidateRecoveryCheckpointAsync(request, checkpoint, cancellationToken).ConfigureAwait(false);
                    checkpointStage?.AddArtifacts(group.TargetCount);
                    checkpointBindings.Add(new RecoveryShadowCheckpointBinding(recoveryConnector, request, checkpoint));
                    group.Checkpoint = checkpoint;
                    group.Request = request;
                    group.CheckpointValidated = true;
                }
            }

            if (groupIssues.Count > 0)
            {
                rehearsalFailure = "One or more shadow endpoints do not provide a validated recovery checkpoint.";
                rehearsalOutcome = RecoveryRehearsalOutcome.Failed;
                rehearsalState = RecoveryRehearsalState.Failed;
            }
            else if (!needRecoveryCheckpoints)
            {
                rehearsalOutcome = RecoveryRehearsalOutcome.NotRequired;
                rehearsalState = RecoveryRehearsalState.Complete;
            }
            else if (!targetMatchesVerification)
            {
                rehearsalFailure = "Stale shadow state was not mutated or rehearsed.";
                rehearsalOutcome = RecoveryRehearsalOutcome.Failed;
                rehearsalState = RecoveryRehearsalState.Failed;
            }
            else
            {
                foreach (var checkpointBinding in checkpointBindings)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (checkpointBinding.Request.TargetArtifactCount == 0) continue;
                    var mutation = await checkpointBinding.Connector.ApplyControlledMutationAsync(
                        checkpointBinding.Request, cancellationToken).ConfigureAwait(false);
                    if (mutation.AffectedArtifacts <= 0)
                        throw new RecoveryException(RecoveryIssueCodes.RehearsalFailed, "Shadow recovery rehearsal did not change target state.");
                    mutatedArtifacts = checked(mutatedArtifacts + mutation.AffectedArtifacts);
                }

                if (mutatedArtifacts == 0 && targetState.RecordCount > 0)
                    throw new RecoveryException(RecoveryIssueCodes.RehearsalFailed, "Shadow recovery rehearsal did not mutate any material target artifact.");

                await RestoreShadowBaselineAsync(checkpointBindings, ledger, graphEdges,
                    cancellationToken).ConfigureAwait(false);
                var restoredState = await ReadTargetStateMeasuredAsync("recovery restored target read-back", cancellationToken).ConfigureAwait(false);
                restoredFingerprint = restoredState.Fingerprint;
                var compensatingEdges = new List<MigrationEdge>();
                foreach (var edge in graph.Edges.Where(edge => edge.Recovery?.Mode == RecoveryMode.Compensate))
                    if (await ledger.HasJournalEntryForEdgeAsync(edge.Id, "produced", cancellationToken).ConfigureAwait(false))
                        compensatingEdges.Add(edge);
                foreach (var edge in compensatingEdges)
                {
                    var compensator = _compensators.Resolve(edge.Recovery!.Strategy!);
                    var validationContext = new RecoveryCompensationValidationContext(checkpointBindings,
                        targetState.Fingerprint, restoredState.Fingerprint, targetState.RecordCount, restoredState.RecordCount);
                    if (await compensator.ValidateShadowOutcomeAsync(edge, validationContext, cancellationToken).ConfigureAwait(false) !=
                        RecoveryCheckResult.Pass)
                        throw new RecoveryException(RecoveryIssueCodes.RehearsalFailed,
                            $"Compensator '{compensator.StrategyId}' did not validate its declared recovery semantics.");
                }

                if (restoredFingerprint != targetState.Fingerprint || restoredState.RecordCount != targetState.RecordCount)
                {
                    if (compensatingEdges.Count == 0 || compensatingEdges.Any(edge =>
                        _compensators.Resolve(edge.Recovery!.Strategy!).ValidationMode != RecoveryValidationMode.SemanticCompensation))
                        throw new RecoveryException(RecoveryIssueCodes.RehearsalFailed, "Exact shadow restoration fingerprint does not match its pre-mutation baseline.");
                    await RestoreRecoveryCheckpointsAsync(checkpointBindings, cancellationToken).ConfigureAwait(false);
                    var cleanupState = await ReadTargetStateMeasuredAsync("recovery cleanup target read-back", cancellationToken).ConfigureAwait(false);
                    shadowCleanupFingerprint = cleanupState.Fingerprint;
                    if (shadowCleanupFingerprint != targetState.Fingerprint || cleanupState.RecordCount != targetState.RecordCount)
                        throw new RecoveryException(RecoveryIssueCodes.RehearsalFailed, "Shadow cleanup after semantic compensation did not restore the verified baseline.");
                }
                else
                {
                    shadowCleanupFingerprint = restoredFingerprint;
                }
                rehearsalOutcome = RecoveryRehearsalOutcome.Passed;
                rehearsalState = RecoveryRehearsalState.Complete;
            }
        }
        catch (OperationCanceledException)
        {
            await TryRestoreShadowBaselineAsync(checkpointBindings).ConfigureAwait(false);
            rehearsalFailure = "Recovery rehearsal was cancelled.";
            rehearsalFailureCode = RecoveryIssueCodes.RehearsalFailed;
            rehearsalOutcome = RecoveryRehearsalOutcome.Cancelled;
            rehearsalState = RecoveryRehearsalState.Cancelled;
        }
        catch (Exception exception)
        {
            rehearsalFailure = exception switch
            {
                RecoveryException recoveryException => recoveryException.Message,
                ShadowRecoveryConnectorException connectorException => connectorException.Message,
                _ => "Shadow recovery rehearsal failed."
            };
            rehearsalFailureCode = exception switch
            {
                RecoveryException recoveryException => recoveryException.Code,
                ShadowRecoveryConnectorException connectorException => connectorException.Code,
                _ => RecoveryIssueCodes.RehearsalFailed
            };
            rehearsalOutcome = RecoveryRehearsalOutcome.Failed;
            rehearsalState = RecoveryRehearsalState.Failed;
            await TryRestoreShadowBaselineAsync(checkpointBindings).ConfigureAwait(false);
        }
        rehearsalStage?.AddArtifacts(mutatedArtifacts);
        rehearsalStage?.Dispose();

        if (rehearsalOutcome == RecoveryRehearsalOutcome.Failed)
            issues.Add(new RecoveryAssessmentIssue(rehearsalFailureCode,
                rehearsalFailure ?? "Shadow recovery rehearsal failed."));
        else if (rehearsalOutcome == RecoveryRehearsalOutcome.Cancelled)
            issues.Add(new RecoveryAssessmentIssue(RecoveryIssueCodes.RehearsalFailed,
                rehearsalFailure ?? "Shadow recovery rehearsal was cancelled."));

        List<RecoveryEdgeAssessment> edgeAssessments;
        using (var edgeAnalysisStage = performanceRecorder?.StartStage(PerformanceStageKind.Recovery, "recovery ledger edge analysis"))
        {
            edgeAssessments = await AssessEdgesAsync(graph, ledger, policy, targetGroups, groupIssues,
                cancellationToken).ConfigureAwait(false);
            edgeAnalysisStage?.AddArtifacts(edgeAssessments.Count);
        }
        foreach (var edgeAssessment in edgeAssessments)
            issues.AddRange(edgeAssessment.Issues);

        RecoveryCoverageSummary artifactCoverage;
        using (var coverageStage = performanceRecorder?.StartStage(PerformanceStageKind.Recovery, "recovery ledger artifact coverage"))
        {
            artifactCoverage = await AssessArtifactCoverageAsync(graph, ledger, edgeAssessments, cancellationToken).ConfigureAwait(false);
            coverageStage?.AddArtifacts(artifactCoverage.AffectedArtifacts);
        }
        if (artifactCoverage.UnknownArtifacts > 0)
            issues.Add(new RecoveryAssessmentIssue(RecoveryIssueCodes.MissingCapability,
                $"{artifactCoverage.UnknownArtifacts.ToString(CultureInfo.InvariantCulture)} target lineage paths lack validated recovery coverage."));
        if (artifactCoverage.UnapprovedIrreversibleArtifacts > 0)
            issues.Add(new RecoveryAssessmentIssue(RecoveryIssueCodes.IrreversibleProhibited,
                $"{artifactCoverage.UnapprovedIrreversibleArtifacts.ToString(CultureInfo.InvariantCulture)} target artifacts use irreversible recovery without approval."));

        var outcome = rehearsalOutcome == RecoveryRehearsalOutcome.Cancelled
            ? RecoveryAssessmentOutcome.Cancelled
            : issues.Count == 0 && edgeAssessments.All(edge => edge.Result != RecoveryCheckResult.Fail) &&
                artifactCoverage.UnknownArtifacts == 0 && artifactCoverage.UnapprovedIrreversibleArtifacts == 0 &&
                rehearsalOutcome is RecoveryRehearsalOutcome.Passed or RecoveryRehearsalOutcome.NotRequired
                ? RecoveryAssessmentOutcome.Passed
                : RecoveryAssessmentOutcome.Failed;
        RecoveryPlan plan;
        using (var planStage = performanceRecorder?.StartStage(PerformanceStageKind.Recovery, "recovery plan construction"))
        {
            plan = await BuildRecoveryPlanAsync(graph, verification, ledger, edgeAssessments, policy, graphNodesById,
                checkpointBindings, cancellationToken).ConfigureAwait(false);
            planStage?.AddArtifacts(plan.Steps.Count);
        }
        var rehearsalFingerprint = FingerprintRehearsal(targetState.Fingerprint, restoredFingerprint, shadowCleanupFingerprint,
            rehearsalOutcome, mutatedArtifacts, checkpointBindings);
        var rehearsal = new RecoveryRehearsal(new RecoveryRehearsalId(Guid.NewGuid()), rehearsalState,
            rehearsalOutcome, verification.Run.Id, targetState.Fingerprint, restoredFingerprint,
            shadowCleanupFingerprint, rehearsalFingerprint, mutatedArtifacts, checkpointBindings.Select(item => item.Checkpoint),
            rehearsalFailure is null ? [] : [new RecoveryAssessmentIssue(RecoveryIssueCodes.RehearsalFailed, rehearsalFailure)],
            rehearsalStart, DateTimeOffset.UtcNow);

        string assessmentFingerprint;
        using (var assessmentFingerprintStage = performanceRecorder?.StartStage(PerformanceStageKind.Recovery,
            "recovery ledger fingerprint stream"))
        {
            assessmentFingerprint = await FingerprintAssessmentAsync(graph.GraphHash, verification.Run, projectionBinding, policy,
                ledger, edgeAssessments, rehearsal, issues, cancellationToken).ConfigureAwait(false);
            assessmentFingerprintStage?.AddArtifacts(ledgerSummary.LineageCount);
        }
        var assessment = new RecoveryAssessment(new RecoveryAssessmentId(Guid.NewGuid()),
            rehearsalOutcome == RecoveryRehearsalOutcome.Cancelled ? RecoveryAssessmentState.Cancelled : RecoveryAssessmentState.Complete,
            outcome, binding, policy.Fingerprint, assessmentFingerprint, edgeAssessments, artifactCoverage, verification.Ledger,
            issues, startedAt, DateTimeOffset.UtcNow);
        RecoveryEvidenceGraph recoveryEvidence;
        using (var evidenceStage = performanceRecorder?.StartStage(PerformanceStageKind.Recovery, "recovery evidence construction"))
        {
            recoveryEvidence = BuildRecoveryEvidence(verification.Run.Id, projectionBinding, assessment, plan, rehearsal);
            evidenceStage?.AddArtifacts(recoveryEvidence.Graph.Records.Count);
        }
        var qualificationStatus = DetermineQualification(verification, sourceCheckpoint.Manifest, policy,
            targetState, projectionBinding, assessment, rehearsal, issues);
        var dryRunFingerprint = FingerprintDryRun(verification.Run, policy, assessment, plan, rehearsal, recoveryEvidence);
        var qualification = new DryRunQualification(dryRunId, qualificationStatus, dryRunFingerprint,
            policy.Fingerprint, assessment.Fingerprint, plan.Fingerprint, rehearsal.Fingerprint,
            binding, recoveryEvidence.Fingerprint, issues);
        return new RecoveryRunResult(dryRunId, assessment, plan, rehearsal, recoveryEvidence, qualification);
    }

    private static void ValidateBinding(LoadedProjectConfiguration configuration, MigrationGraph graph,
        ProjectionVerificationBinding projection, VerificationResult verification)
    {
        var run = verification.Run;
        if (configuration.ConfigurationHash != run.ConfigurationHash || graph.GraphHash != run.GraphHash ||
            graph.GraphHash != projection.GraphHash || configuration.ConfigurationHash != projection.ConfigurationHash ||
            projection.ProjectionStatus != "succeeded" || run.ProjectionSourceCount != projection.ProjectionSourceCount ||
            run.ProjectionTargetCount != projection.ProjectionTargetCount ||
            run.CheckpointId != projection.CheckpointId || run.CheckpointManifestHash != projection.CheckpointManifestHash ||
            run.SourceFingerprint != projection.SourceFingerprint || run.ProjectionRunId != projection.ProjectionRunId ||
            run.ProjectionManifestHash != projection.ProjectionManifestHash || run.ProjectionFingerprint != projection.ProjectionFingerprint ||
            verification.EvidenceGraph.VerificationRunId != run.Id || verification.EvidenceGraph.Fingerprint != run.EvidenceFingerprint)
            throw new RecoveryException(RecoveryIssueCodes.ContextMismatch, "Recovery inputs do not identify the same configuration, graph, checkpoint, projection, verification, and evidence.");
    }

    private static void ValidateSourceCheckpoint(LoadedProjectConfiguration configuration, MigrationGraph graph,
        VerificationRunRecord verification, SourceCheckpoint checkpoint, EffectiveRecoveryPolicy policy,
        IReadOnlyDictionary<string, string> connectorVersions)
    {
        if (checkpoint.Status != CheckpointStatus.Complete || !checkpoint.Replayable ||
            checkpoint.Id != verification.CheckpointId || checkpoint.ManifestHash != verification.CheckpointManifestHash ||
            checkpoint.SourceFingerprint != verification.SourceFingerprint || checkpoint.ConfigurationHash != configuration.ConfigurationHash ||
            checkpoint.GraphHash != graph.GraphHash)
            throw new RecoveryException(RecoveryIssueCodes.ContextMismatch, "Source checkpoint is incomplete or mismatched; it is not a target recovery backup.");
        var sourceNodes = graph.Nodes.Where(node => node.Type == MigrationNodeType.Source)
            .ToDictionary(node => node.Name, StringComparer.Ordinal);
        if (!checkpoint.Endpoints.Select(endpoint => endpoint.SourceNodeKey).Order(StringComparer.Ordinal)
            .SequenceEqual(sourceNodes.Keys.Order(StringComparer.Ordinal), StringComparer.Ordinal))
            throw new RecoveryException(RecoveryIssueCodes.ContextMismatch, "Source checkpoint node coverage differs from the migration graph.");
        foreach (var endpoint in checkpoint.Endpoints)
        {
            var node = sourceNodes[endpoint.SourceNodeKey];
            if (endpoint.SystemId != node.SystemId || endpoint.EndpointId != node.EndpointId ||
                endpoint.SelectorHash != SnapshotFingerprints.SelectorHash(node.Selector) ||
                !endpoint.IdentityFields.SequenceEqual(node.Selector.IdentityFields, StringComparer.Ordinal) ||
                !connectorVersions.TryGetValue($"source:{node.Name}", out var connectorVersion) ||
                endpoint.ConnectorVersion != connectorVersion)
                throw new RecoveryException(RecoveryIssueCodes.ContextMismatch, "Source checkpoint selector, connector version, or endpoint binding differs from the graph.");
        }
        foreach (var requirement in policy.RequiredSourceCheckpoints)
        {
            var endpointAvailable = checkpoint.Endpoints.Any(endpoint => endpoint.SystemId.Value == requirement.SystemId &&
                endpoint.EndpointId.Value == requirement.EndpointId && endpoint.Replayable);
            if (!endpointAvailable)
                throw new RecoveryException(RecoveryIssueCodes.MissingCheckpoint,
                    $"Required source checkpoint endpoint '{requirement.SystemId}/{requirement.EndpointId}' is unavailable.");
        }
    }

    private static List<RecoveryTargetGroup> BuildTargetGroups(LoadedProjectConfiguration configuration,
        MigrationGraph graph, ProjectionVerificationBinding projectionBinding,
        IEnumerable<VerificationTargetRuntime> targets)
    {
        var materialized = targets.OrderBy(target => target.NodeKey, StringComparer.Ordinal).ToArray();
        var expected = graph.Nodes.Where(node => node.Type is MigrationNodeType.Target or MigrationNodeType.Archive)
            .Select(node => node.Name).Order(StringComparer.Ordinal).ToArray();
        if (!expected.SequenceEqual(materialized.Select(target => target.NodeKey).Order(StringComparer.Ordinal), StringComparer.Ordinal))
            throw new RecoveryException(RecoveryIssueCodes.ContextMismatch, "Recovery target runtime coverage does not exactly match the graph.");
        foreach (var target in materialized)
        {
            var node = graph.Nodes.Single(item => item.Name == target.NodeKey);
            var system = configuration.Systems.SingleOrDefault(item => item.Id == node.SystemId);
            var endpoint = system?.StorageEndpoints.SingleOrDefault(item => item.Id == node.EndpointId);
            if (system?.Role != SystemRole.ShadowTarget || endpoint is null ||
                target.Context.Role != SystemRole.ShadowTarget || target.Context.RunId != projectionBinding.ProjectionRunId ||
                target.Context.ConnectorContext.SystemKey != node.SystemId.Value ||
                target.Context.ConnectorContext.EndpointKey != node.EndpointId.Value ||
                target.Context.ConnectorContext.SemanticType != node.SemanticType ||
                target.Context.ConnectorContext.Connector != endpoint.Connector || target.Connector.Id != endpoint.Connector ||
                !projectionBinding.ConnectorVersions.TryGetValue($"target:{node.Name}", out var connectorVersion) ||
                target.Connector.Version != connectorVersion)
                throw new RecoveryException(RecoveryIssueCodes.ContextMismatch, "Recovery target runtime is not bound to its shadow graph node.");
        }
        return materialized.GroupBy(target => new
            {
                target.Context.RunId,
                SystemKey = target.Context.ConnectorContext.SystemKey,
                EndpointKey = target.Context.ConnectorContext.EndpointKey,
                ConnectorId = target.Context.ConnectorContext.Connector
            })
            .Select(group =>
            {
                var groupTargets = group.OrderBy(target => target.NodeKey, StringComparer.Ordinal).ToArray();
                var connector = groupTargets[0].Connector;
                if (groupTargets.Any(target => target.Connector.Id != connector.Id || target.Connector.Version != connector.Version))
                    throw new RecoveryException(RecoveryIssueCodes.ContextMismatch, "One target endpoint resolved inconsistent connector implementations or versions.");
                return new RecoveryTargetGroup(group.Key.SystemKey, group.Key.EndpointKey, connector,
                    connector as IShadowTargetRecoveryConnector, groupTargets);
            })
            .OrderBy(group => group.SystemKey, StringComparer.Ordinal).ThenBy(group => group.EndpointKey, StringComparer.Ordinal)
            .ToList();
    }

    private static async Task<TargetState> ReadTargetStateAsync(MigrationGraph graph,
        IReadOnlyCollection<RecoveryTargetGroup> groups, CancellationToken cancellationToken)
    {
        using var globalBuilder = MaterializedTargetFingerprint.CreateBuilder(graph.GraphHash);
        var groupByNode = groups.SelectMany(group => group.Targets.Select(target => (target.NodeKey, group)))
            .ToDictionary(item => item.NodeKey, item => item.group, StringComparer.Ordinal);
        var groupBuilders = groups.ToDictionary(group => group.Key,
            _ => MaterializedTargetFingerprint.CreateBuilder(graph.GraphHash), StringComparer.Ordinal);
        var groupStates = new Dictionary<string, (string Fingerprint, long Count)>(StringComparer.Ordinal);
        try
        {
            foreach (var target in groups.SelectMany(group => group.Targets).OrderBy(target => target.NodeKey, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var group = groupByNode[target.NodeKey];
                var node = graph.Nodes.Single(item => item.Name == target.NodeKey);
                await foreach (var record in target.Connector.ReadAsync(new ReadRequest(target.Context, node.Selector), cancellationToken)
                    .WithCancellation(cancellationToken).ConfigureAwait(false))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var artifactType = StableArtifactIdentity.ArtifactTypeFor(node.Selector);
                    if (record.SemanticType != node.SemanticType || record.Artifact.SystemId != node.SystemId ||
                        record.Artifact.EndpointId != node.EndpointId || record.Artifact.ArtifactType != artifactType ||
                        GraphTargetIdentity.Create(record, node.Selector) != record.Artifact.Identity ||
                        StableArtifactIdentity.CreateArtifactId(record.Artifact.SystemId.Value, record.Artifact.EndpointId.Value,
                            artifactType, record.Artifact.Identity) != record.Artifact.Id.Value)
                        throw new RecoveryException(RecoveryIssueCodes.ContextMismatch, "Physical shadow read-back does not match its configured graph node.");
                    groupBuilders[group.Key].Add(node.Name, record);
                    globalBuilder.Add(node.Name, record);
                }
            }
            var overall = globalBuilder.Finish();
            foreach (var group in groups)
            {
                var fingerprint = groupBuilders[group.Key].Finish();
                groupStates.Add(group.Key, (fingerprint.Fingerprint, fingerprint.RecordCount));
            }
            return new TargetState(overall.Fingerprint, overall.RecordCount, groupStates);
        }
        finally
        {
            foreach (var builder in groupBuilders.Values) builder.Dispose();
        }
    }

    private async Task<List<RecoveryEdgeAssessment>> AssessEdgesAsync(MigrationGraph graph, SqliteVerificationLedgerStore ledger,
        EffectiveRecoveryPolicy policy, IReadOnlyCollection<RecoveryTargetGroup> groups,
        Dictionary<string, RecoveryAssessmentIssue> groupIssues, CancellationToken cancellationToken)
    {
        var groupByEndpoint = groups.SelectMany(group => group.Targets.Select(target => (target.NodeKey, group)))
            .ToDictionary(item => item.NodeKey, item => item.group, StringComparer.Ordinal);
        var results = new List<RecoveryEdgeAssessment>();
        foreach (var edge in graph.Edges.OrderBy(edge => edge.Id.Value))
        {
            var hasProduced = await ledger.HasJournalEntryForEdgeAsync(edge.Id, "produced", cancellationToken).ConfigureAwait(false);
            var hasExcluded = await ledger.HasJournalEntryForEdgeAsync(edge.Id, "excluded", cancellationToken).ConfigureAwait(false);
            if (!hasProduced && !hasExcluded) continue;

            var scopeCounts = await ledger.ReadJournalScopeCountsAsync(edge.Id, cancellationToken).ConfigureAwait(false);
            var semanticTypes = new HashSet<string>(StringComparer.Ordinal);
            var targetGroupsByKey = new Dictionary<string, RecoveryTargetGroup>(StringComparer.Ordinal);
            await foreach (var source in ledger.ReadJournalScopeAsync(edge.Id, targets: false, cancellationToken)
                .WithCancellation(cancellationToken).ConfigureAwait(false))
                semanticTypes.Add(graph.Nodes.Single(node => node.Id == source.NodeId).SemanticType);
            await foreach (var target in ledger.ReadJournalScopeAsync(edge.Id, targets: true, cancellationToken)
                .WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                var targetNode = graph.Nodes.Single(node => node.Id == target.NodeId);
                semanticTypes.Add(targetNode.SemanticType);
                if (groupByEndpoint.TryGetValue(targetNode.Name, out var targetGroup))
                    targetGroupsByKey.TryAdd(targetGroup.Key, targetGroup);
            }

            var targetGroups = targetGroupsByKey.Values.ToArray();
            var sourceCount = scopeCounts.SourceCount;
            var targetCount = scopeCounts.TargetCount;
            var destructiveCount = checked(sourceCount + targetCount);
            var edgeIssues = new List<RecoveryAssessmentIssue>();
            var recovery = edge.Recovery;
            var result = RecoveryCheckResult.Pass;
            var lossy = false;
            var capabilityAvailable = false;
            var capabilityValidated = false;
            var risk = RecoveryRiskLevel.Low;
            var validationMode = RecoveryValidationMode.ExactRestoration;
            string? strategy = recovery?.Strategy;

            void Fail(string code, string message) =>
                edgeIssues.Add(new RecoveryAssessmentIssue(code, message, edge.Id));

            if (recovery is null)
            {
                result = RecoveryCheckResult.Fail;
                var message = edge.Operation.IsDestructive && policy.RequireRecoveryForDestructiveOperations
                    ? "Destructive migration edge has no recovery definition under the active recovery policy."
                    : "Executed material migration edge has no recovery classification.";
                Fail(RecoveryIssueCodes.MissingDefinition, message);
                risk = RecoveryRiskLevel.Critical;
            }
            else if (edge.Operation.Type == MigrationOperationType.Exclude && hasExcluded && !hasProduced)
            {
                var excludedSources = 0;
                await foreach (var source in ledger.ReadJournalScopeAsync(edge.Id, targets: false, cancellationToken)
                    .WithCancellation(cancellationToken).ConfigureAwait(false))
                {
                    var disposition = await ledger.FindDispositionAsync(source.NodeId, source.ArtifactId, cancellationToken).ConfigureAwait(false);
                    if (disposition?.Disposition == ArtifactDisposition.Excluded) excludedSources++;
                }
                if (excludedSources != sourceCount)
                {
                    result = RecoveryCheckResult.Fail;
                    Fail(RecoveryIssueCodes.ContextMismatch, "Explicit exclusion does not have matching graph-scoped source dispositions.");
                }
            }
            else
            {
                switch (recovery.Mode)
                {
                    case RecoveryMode.Reverse:
                    {
                        var reversibility = TransformationLossAnalyzer.Analyze(edge);
                        lossy = !reversibility.IsReversible;
                        capabilityAvailable = true;
                        capabilityValidated = reversibility.IsReversible;
                        if (lossy)
                        {
                            result = RecoveryCheckResult.Fail;
                            risk = RecoveryRiskLevel.High;
                            Fail(RecoveryIssueCodes.InvalidReverse, reversibility.Reason);
                        }
                        break;
                    }
                    case RecoveryMode.Restore:
                    {
                        risk = RecoveryRiskLevel.Medium;
                        capabilityAvailable = targetCount > 0 && targetGroups.Length > 0 &&
                            targetGroups.All(group => group.RecoveryConnector is not null && !groupIssues.ContainsKey(group.Key));
                        capabilityValidated = capabilityAvailable && targetGroups.All(group => group.CheckpointValidated);
                        if (!capabilityAvailable || policy.RequireValidatedRestore && !capabilityValidated)
                        {
                            result = RecoveryCheckResult.Fail;
                            Fail(RecoveryIssueCodes.MissingCheckpoint, "Restore mode has no complete, validated target recovery checkpoint for every affected endpoint.");
                        }
                        break;
                    }
                    case RecoveryMode.Compensate:
                    {
                        risk = RecoveryRiskLevel.Medium;
                        if (string.IsNullOrWhiteSpace(recovery.Strategy))
                        {
                            result = RecoveryCheckResult.Fail;
                            Fail(RecoveryIssueCodes.UnknownCompensator, "Compensate mode has no registered strategy identifier.");
                            break;
                        }
                        try
                        {
                            var compensator = _compensators.Resolve(recovery.Strategy);
                            result = compensator.Validate(edge);
                            capabilityAvailable = result == RecoveryCheckResult.Pass;
                            validationMode = compensator.ValidationMode;
                            if (!capabilityAvailable) Fail(RecoveryIssueCodes.UnknownCompensator, "Registered compensator rejected this edge configuration.");
                        }
                        catch (RecoveryException exception)
                        {
                            result = RecoveryCheckResult.Fail;
                            Fail(exception.Code, exception.Message);
                        }
                        capabilityValidated = capabilityAvailable && targetGroups.Length > 0 && targetGroups.All(group =>
                            group.RecoveryConnector is not null && group.CheckpointValidated && !groupIssues.ContainsKey(group.Key));
                        if (!capabilityValidated)
                        {
                            result = RecoveryCheckResult.Fail;
                            Fail(RecoveryIssueCodes.MissingCapability, "Compensation has no validated shadow recovery capability for every affected endpoint.");
                        }
                        break;
                    }
                    case RecoveryMode.Irreversible:
                    {
                        risk = RecoveryRiskLevel.Critical;
                        capabilityAvailable = false;
                        var approved = policy.AllowIrreversibleOperations &&
                            (!policy.RequireIrreversibleApproval || policy.AllowIrreversibleOperations) &&
                            recovery.Justification is not null && destructiveCount <= policy.MaximumIrreversibleArtifacts;
                        capabilityValidated = approved;
                        if (!approved)
                        {
                            result = RecoveryCheckResult.Fail;
                            Fail(RecoveryIssueCodes.IrreversibleProhibited,
                                "Irreversible operation is not explicitly allowed, justified, and within the configured artifact limit.");
                        }
                        break;
                    }
                    default:
                        result = RecoveryCheckResult.Fail;
                        Fail(RecoveryIssueCodes.MissingDefinition, "Executed edge has an unsupported recovery mode.");
                        break;
                }
            }

            if (result == RecoveryCheckResult.Fail && risk is RecoveryRiskLevel.Low or RecoveryRiskLevel.Medium)
                risk = RecoveryRiskLevel.High;

            results.Add(new RecoveryEdgeAssessment(edge.Id, edge.Name, edge.Operation.Type, edge.Operation.IsDestructive,
                semanticTypes.Order(StringComparer.Ordinal), recovery?.Mode,
                strategy, result, capabilityAvailable, capabilityValidated, lossy, risk, validationMode,
                sourceCount, targetCount, edgeIssues));
        }
        return results;
    }

    private static async Task<RecoveryCoverageSummary> AssessArtifactCoverageAsync(MigrationGraph graph,
        SqliteVerificationLedgerStore ledger, IReadOnlyCollection<RecoveryEdgeAssessment> assessments,
        CancellationToken cancellationToken)
    {
        var assessmentById = assessments.ToDictionary(item => item.EdgeId);
        var bySemanticType = new Dictionary<string, (long Affected, long Recoverable)>(StringComparer.Ordinal);
        long affected = 0;
        long recoverableCount = 0;
        long irrecoverable = 0;
        long unknown = 0;
        long unapprovedIrreversible = 0;
        await foreach (var lineage in ledger.ReadLineageAsync(cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            var item = AssessArtifactCoverage(graph, lineage, assessmentById);
            affected++;
            if (item.Recoverable) recoverableCount++;
            else irrecoverable++;
            if (!item.Covered) unknown++;
            if (item.Modes.Contains(RecoveryMode.Irreversible) && !item.ApprovedIrreversible) unapprovedIrreversible++;
            var current = bySemanticType.GetValueOrDefault(item.SemanticType);
            bySemanticType[item.SemanticType] = (current.Affected + 1, current.Recoverable + (item.Recoverable ? 1 : 0));
        }
        var semanticCoverage = bySemanticType.Select(pair => new RecoverySemanticTypeCoverage(pair.Key,
            pair.Value.Affected, pair.Value.Recoverable,
            pair.Value.Affected == 0 ? null : decimal.Round(100m * pair.Value.Recoverable / pair.Value.Affected, 4)));
        return new RecoveryCoverageSummary(assessments, affected, recoverableCount, irrecoverable, unknown,
            unapprovedIrreversible, semanticCoverage);
    }

    private static RecoveryArtifactCoverage AssessArtifactCoverage(MigrationGraph graph, LineageRecord lineage,
        Dictionary<MigrationEdgeId, RecoveryEdgeAssessment> assessmentById)
    {
        if (lineage.TargetNodeId is null)
            throw new RecoveryException(RecoveryIssueCodes.ContextMismatch, "Recovery requires graph-scoped target lineage.");
        var semanticType = graph.Nodes.Single(node => node.Id == lineage.TargetNodeId.Value).SemanticType;
        var results = lineage.Path.Select(edgeId => assessmentById.TryGetValue(edgeId, out var assessment)
            ? assessment : null).ToArray();
        var covered = results.Length > 0 && results.All(result => result is not null && result.Result != RecoveryCheckResult.Fail);
        var modes = results.Where(result => result is not null).Select(result => result!.ConfiguredMode)
            .Where(mode => mode is not null).Select(mode => mode!.Value).Distinct().ToArray();
        var approvedIrreversible = covered && modes.Contains(RecoveryMode.Irreversible);
        var recoverable = covered && !modes.Contains(RecoveryMode.Irreversible);
        var reason = !covered ? "At least one migration edge in the target lineage lacks validated recovery coverage."
            : approvedIrreversible ? "Irreversible target state is explicitly permitted by the recovery policy; it remains classified as irrecoverable."
            : "Every migration edge in the target lineage has a passing, recoverable assessment.";
        return new RecoveryArtifactCoverage(new GraphArtifactReference(lineage.TargetNodeId.Value, lineage.Target),
            semanticType, lineage.Path, modes, covered, recoverable, approvedIrreversible, reason);
    }

    public static async IAsyncEnumerable<RecoveryArtifactCoverage> ReadArtifactCoverageAsync(string projectDirectory,
        MigrationGraph graph, RecoveryAssessment assessment,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(assessment);
        await using var ledger = await SqliteVerificationLedgerStore.OpenAsync(projectDirectory,
            assessment.VerificationLedger, cancellationToken).ConfigureAwait(false);
        var assessments = assessment.Edges.ToDictionary(item => item.EdgeId);
        await foreach (var lineage in ledger.ReadLineageAsync(cancellationToken)
            .WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return AssessArtifactCoverage(graph, lineage, assessments);
        }
    }

    private static async Task<RecoveryPlan> BuildRecoveryPlanAsync(MigrationGraph graph, VerificationResult verification,
        SqliteVerificationLedgerStore ledger,
        IReadOnlyCollection<RecoveryEdgeAssessment> assessments, EffectiveRecoveryPolicy policy,
        Dictionary<MigrationNodeId, MigrationNode> nodes,
        IReadOnlyCollection<RecoveryShadowCheckpointBinding> checkpointBindings, CancellationToken cancellationToken)
    {
        var edgeIds = assessments.Select(item => item.EdgeId).ToHashSet();
        var edges = graph.Edges.Where(edge => edgeIds.Contains(edge.Id)).ToDictionary(edge => edge.Id);
        var dependencies = edges.Keys.ToDictionary(id => id, _ => new HashSet<MigrationEdgeId>());
        foreach (var upstream in edges.Values)
        foreach (var downstream in edges.Values)
        {
            if (upstream.Id == downstream.Id || !upstream.Targets.Intersect(downstream.Sources).Any()) continue;
            dependencies[upstream.Id].Add(downstream.Id);
        }

        var ordered = new List<MigrationEdge>();
        var remaining = dependencies.ToDictionary(pair => pair.Key, pair => pair.Value);
        while (remaining.Count > 0)
        {
            var next = remaining.Where(pair => pair.Value.Count == 0)
                .Select(pair => edges[pair.Key]).OrderBy(edge => edge.Name, StringComparer.Ordinal).FirstOrDefault();
            if (next is null)
                throw new RecoveryException(RecoveryIssueCodes.ContextMismatch, "Migration graph dependencies contain a recovery-order cycle.");
            ordered.Add(next);
            remaining.Remove(next.Id);
            foreach (var dependency in remaining.Values) dependency.Remove(next.Id);
        }

        var steps = new List<RecoveryPlanStep>(ordered.Count);
        for (var index = 0; index < ordered.Count; index++)
        {
            var edge = ordered[index];
            var node = edge.Targets.Count > 0 ? nodes[edge.Targets[0]] : nodes[edge.Sources[0]];
            var edgeAssessment = assessments.Single(item => item.EdgeId == edge.Id);
            steps.Add(new RecoveryPlanStep(index + 1, edge, node.SystemId, node.EndpointId,
                edge.Recovery?.Strategy ?? edge.Recovery?.Mode.ToString().ToLowerInvariant() ?? "unknown",
                edgeAssessment.AffectedSourceArtifacts, edgeAssessment.AffectedTargetArtifacts,
                "Verification, source checkpoint, projection manifest, and target recovery checkpoint bindings remain valid.",
                edge.Recovery?.Mode == RecoveryMode.Compensate ? "Compensated state satisfies its registered semantic validator." : "Exact shadow baseline is restored.",
                "Re-read the affected shadow target and validate its bound recovery fingerprint."));
        }

        var fingerprint = await FingerprintPlanAsync(graph.GraphHash, verification.Run.SourceFingerprint, policy.Fingerprint,
            checkpointBindings.Select(item => item.Checkpoint), steps, ledger, cancellationToken).ConfigureAwait(false);
        var risks = assessments.Where(item => item.ConfiguredMode == RecoveryMode.Irreversible && item.Result == RecoveryCheckResult.Pass)
            .Select(item => new RecoveryAssessmentIssue("PSREC-IRREVERSIBLE-ACKNOWLEDGED",
                $"Irreversible edge '{item.EdgeName}' was explicitly allowed for {item.AffectedSourceArtifacts.ToString(CultureInfo.InvariantCulture)} source and {item.AffectedTargetArtifacts.ToString(CultureInfo.InvariantCulture)} target artifacts.",
                item.EdgeId)).ToArray();
        var references = new List<EvidenceReference> { new(runId: verification.Run.Id) };
        references.AddRange(assessments.Select(item => new EvidenceReference(migrationEdgeId: item.EdgeId)));
        return new RecoveryPlan(new RecoveryPlanId(Guid.NewGuid()), verification.Run.Id, verification.Run.CheckpointId,
            checkpointBindings.Select(item => item.Checkpoint.Id), policy.Fingerprint,
            fingerprint, ["Source checkpoint remains complete and integrity-verified.", "Projection manifest matches the verification context.",
                "Physical shadow fingerprint is re-observed before qualification."], steps, risks, references);
    }

    private async Task RestoreShadowBaselineAsync(IReadOnlyCollection<RecoveryShadowCheckpointBinding> checkpoints,
        SqliteVerificationLedgerStore ledger, Dictionary<MigrationEdgeId, MigrationEdge> edges,
        CancellationToken cancellationToken)
    {
        var strategies = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var entry in ledger.ReadJournalEntriesAsync(cancellationToken)
            .WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (entry.Result == "produced" && edges.TryGetValue(entry.EdgeId, out var edge) &&
                edge.Recovery?.Mode == RecoveryMode.Compensate && edge.Recovery.Strategy is { } strategy)
                strategies.Add(strategy);
        }
        if (strategies.Count == 0)
        {
            foreach (var item in checkpoints.OrderBy(item => item.Request.SystemId.Value, StringComparer.Ordinal)
                .ThenBy(item => item.Request.EndpointId.Value, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await item.Connector.RestoreRecoveryCheckpointAsync(item.Request, item.Checkpoint, cancellationToken).ConfigureAwait(false);
            }
            return;
        }

        foreach (var strategy in strategies)
        {
            var compensator = _compensators.Resolve(strategy);
            await compensator.ExecuteShadowRehearsalAsync(checkpoints, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task RestoreRecoveryCheckpointsAsync(
        IReadOnlyCollection<RecoveryShadowCheckpointBinding> checkpoints, CancellationToken cancellationToken)
    {
        foreach (var item in checkpoints.OrderBy(item => item.Request.SystemId.Value, StringComparer.Ordinal)
            .ThenBy(item => item.Request.EndpointId.Value, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await item.Connector.RestoreRecoveryCheckpointAsync(item.Request, item.Checkpoint, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task TryRestoreShadowBaselineAsync(IReadOnlyCollection<RecoveryShadowCheckpointBinding> checkpoints)
    {
        var ordered = checkpoints.ToArray();
        Array.Reverse(ordered);
        foreach (var item in ordered)
        {
            try
            {
                await item.Connector.RestoreRecoveryCheckpointAsync(item.Request, item.Checkpoint, CancellationToken.None).ConfigureAwait(false);
            }
            catch { }
        }
    }

    private static RecoveryEvidenceGraph BuildRecoveryEvidence(RunId runId, ProjectionVerificationBinding binding,
        RecoveryAssessment assessment, RecoveryPlan plan, RecoveryRehearsal rehearsal)
    {
        var records = new List<EvidenceRecord>();
        var ruleId = new RuleId("proofshift.recovery.assessment");
        foreach (var edge in assessment.Edges)
        {
            var refs = new List<EvidenceReference>
            {
                new(runId: runId),
                new(checkpointId: binding.CheckpointId),
                new(projectionRunId: binding.ProjectionRunId),
                new(migrationEdgeId: edge.EdgeId)
            };
            records.Add(new EvidenceRecord(StableEvidenceId(runId, $"edge:{edge.EdgeId.Value:D}"), runId,
                EvidenceType.Recovery, ruleId, "1", edge.Result == RecoveryCheckResult.Fail ? EvidenceResult.Fail : EvidenceResult.Pass,
                refs, edge.Issues.Count == 0
                    ? $"Recovery mode {edge.ConfiguredMode} was assessed for {edge.AffectedTargetArtifacts.ToString(CultureInfo.InvariantCulture)} target artifacts."
                    : string.Join(" ", edge.Issues.Select(issue => issue.Message)), DateTimeOffset.UtcNow,
                new EvidenceValue(new StringValue(edge.ConfiguredMode?.ToString() ?? "missing")),
                new EvidenceValue(new StringValue(edge.Result.ToString())),
                edge.Result == RecoveryCheckResult.Fail ? EvidenceSeverity.Error : EvidenceSeverity.Info,
                edge.Issues.Count > 0 ? edge.Issues[0].Code : "RecoveryEdgeAssessment"));
        }

        var rehearsalId = StableEvidenceId(runId, "rehearsal");
        records.Add(new EvidenceRecord(rehearsalId, runId, EvidenceType.Recovery, ruleId, "1",
            rehearsal.Outcome == RecoveryRehearsalOutcome.Passed ? EvidenceResult.Pass :
            rehearsal.Outcome == RecoveryRehearsalOutcome.Failed ? EvidenceResult.Fail : EvidenceResult.NotApplicable,
            [new EvidenceReference(runId: runId), new EvidenceReference(checkpointId: binding.CheckpointId),
             new EvidenceReference(projectionRunId: binding.ProjectionRunId)],
            rehearsal.Outcome == RecoveryRehearsalOutcome.Passed
                ? rehearsal.RestoredFingerprint == rehearsal.BaselineFingerprint
                    ? "Shadow recovery rehearsal exactly restored the verified target baseline."
                    : "A registered semantic compensator validated its outcome; the shadow target was then cleaned back to the verified baseline."
                : "Shadow recovery rehearsal did not establish an exact restored baseline.", DateTimeOffset.UtcNow,
            new EvidenceValue(new StringValue($"sha256:{rehearsal.BaselineFingerprint}")),
            new EvidenceValue(new StringValue(rehearsal.RestoredFingerprint is null ? "unavailable" :
                $"restored=sha256:{rehearsal.RestoredFingerprint};cleanup=sha256:{rehearsal.ShadowCleanupFingerprint}")),
            rehearsal.Outcome == RecoveryRehearsalOutcome.Passed ? EvidenceSeverity.Info : EvidenceSeverity.Error,
            "RecoveryRehearsal"));
        var summaryId = StableEvidenceId(runId, "assessment-summary");
        records.Add(new EvidenceRecord(summaryId, runId, EvidenceType.Recovery, ruleId, "1",
            assessment.Outcome == RecoveryAssessmentOutcome.Passed && rehearsal.Outcome is RecoveryRehearsalOutcome.Passed or RecoveryRehearsalOutcome.NotRequired
                ? EvidenceResult.Pass : EvidenceResult.Fail,
            [new EvidenceReference(runId: runId), new EvidenceReference(evidenceId: rehearsalId),
             new EvidenceReference(ruleId: new RuleId("proofshift.recovery.plan"))],
            $"Recovery assessment covered {assessment.Coverage.ExecutedEdges.ToString(CultureInfo.InvariantCulture)} executed edges and {assessment.Coverage.AffectedArtifacts.ToString(CultureInfo.InvariantCulture)} target artifacts.",
            DateTimeOffset.UtcNow, new EvidenceValue(new StringValue("recovery-policy-and-artifact-coverage")),
            new EvidenceValue(new StringValue(assessment.Outcome.ToString())),
            assessment.Outcome == RecoveryAssessmentOutcome.Passed ? EvidenceSeverity.Info : EvidenceSeverity.Error,
            "RecoveryAssessmentSummary"));
        _ = plan;
        return new RecoveryEvidenceGraph(new EvidenceGraph(runId, records), assessment.Binding.EvidenceFingerprint);
    }

    private static DryRunQualificationStatus DetermineQualification(VerificationResult verification,
        SourceCheckpoint checkpoint, EffectiveRecoveryPolicy policy, TargetState targetState,
        ProjectionVerificationBinding projectionBinding, RecoveryAssessment assessment,
        RecoveryRehearsal rehearsal, List<RecoveryAssessmentIssue> issues)
    {
        if (rehearsal.Outcome == RecoveryRehearsalOutcome.Cancelled || assessment.State == RecoveryAssessmentState.Cancelled)
            return DryRunQualificationStatus.Cancelled;
        if (verification.Run.State != VerificationRunState.Complete) return DryRunQualificationStatus.Error;
        if (verification.Run.Outcome is not (VerificationOutcome.Passed or VerificationOutcome.PassedWithWarnings) ||
            checkpoint.Status != CheckpointStatus.Complete || checkpoint.ManifestHash != verification.Run.CheckpointManifestHash ||
            checkpoint.SourceFingerprint != verification.Run.SourceFingerprint ||
            targetState.Fingerprint != projectionBinding.ProjectionFingerprint ||
            targetState.RecordCount != projectionBinding.ProjectionTargetCount ||
            assessment.Outcome != RecoveryAssessmentOutcome.Passed ||
            rehearsal.Outcome is not (RecoveryRehearsalOutcome.Passed or RecoveryRehearsalOutcome.NotRequired) ||
            (rehearsal.Outcome == RecoveryRehearsalOutcome.NotRequired &&
                (policy.RequireRecoveryRehearsal || assessment.Edges.Any(edge => edge.ConfiguredMode is RecoveryMode.Restore or RecoveryMode.Compensate))) ||
            (rehearsal.Outcome == RecoveryRehearsalOutcome.Passed && rehearsal.ShadowCleanupFingerprint != targetState.Fingerprint) ||
            issues.Count > 0)
            return DryRunQualificationStatus.NotQualified;
        return DryRunQualificationStatus.Qualified;
    }

    private static async Task<string> FingerprintAssessmentAsync(string graphHash, VerificationRunRecord verification,
        ProjectionVerificationBinding projectionBinding,
        EffectiveRecoveryPolicy policy, SqliteVerificationLedgerStore ledger, IEnumerable<RecoveryEdgeAssessment> edges,
        RecoveryRehearsal rehearsal,
        IEnumerable<RecoveryAssessmentIssue> issues, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string part)
        {
            var canonical = $"{Encoding.UTF8.GetByteCount(part).ToString(CultureInfo.InvariantCulture)}:{part};";
            hash.AppendData(Encoding.UTF8.GetBytes(canonical));
        }
        Add(RecoveryFingerprintVersions.Assessment);
        Add(graphHash);
        Add(verification.ConfigurationHash);
        Add(verification.CheckpointManifestHash);
        Add(verification.SourceFingerprint);
        Add(verification.ProjectionManifestHash);
        Add(verification.ProjectionFingerprint);
        Add(verification.RuleSetFingerprint);
        Add(verification.EvidenceFingerprint);
        Add(projectionBinding.ProjectionSourceCount.ToString(CultureInfo.InvariantCulture));
        Add(projectionBinding.ProjectionTargetCount.ToString(CultureInfo.InvariantCulture));
        Add(policy.Fingerprint);
        Add(rehearsal.BaselineFingerprint);
        Add(rehearsal.RestoredFingerprint ?? "unavailable");
        Add(rehearsal.Outcome.ToString());
        foreach (var edge in edges.OrderBy(item => item.EdgeId.Value))
        {
            Add(edge.EdgeId.Value.ToString("D", CultureInfo.InvariantCulture));
            Add(edge.ConfiguredMode?.ToString() ?? "missing");
            Add(edge.Strategy ?? string.Empty);
            Add(edge.Result.ToString());
            Add(edge.IsDestructive.ToString());
            foreach (var semanticType in edge.AffectedSemanticTypes) Add(semanticType);
            Add(edge.IsLossy.ToString());
            Add(edge.Risk.ToString());
            Add(edge.CapabilityAvailable.ToString());
            Add(edge.CapabilityValidated.ToString());
            Add(edge.ValidationMode.ToString());
            Add(edge.AffectedSourceArtifacts.ToString(CultureInfo.InvariantCulture));
            Add(edge.AffectedTargetArtifacts.ToString(CultureInfo.InvariantCulture));
            await foreach (var source in ledger.ReadJournalScopeAsync(edge.EdgeId, targets: false, cancellationToken)
                .WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                Add(source.NodeId.Value.ToString("D", CultureInfo.InvariantCulture));
                Add(source.ArtifactId.Value);
            }
            await foreach (var target in ledger.ReadJournalScopeAsync(edge.EdgeId, targets: true, cancellationToken)
                .WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                Add(target.NodeId.Value.ToString("D", CultureInfo.InvariantCulture));
                Add(target.ArtifactId.Value);
            }
            foreach (var issue in edge.Issues.OrderBy(issue => issue.Code, StringComparer.Ordinal)) Add(issue.Code);
        }
        var assessmentById = edges.ToDictionary(item => item.EdgeId);
        await foreach (var lineage in ledger.ReadLineageAsync(cancellationToken)
            .WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (lineage.TargetNodeId is not { } targetNodeId)
                throw new RecoveryException(RecoveryIssueCodes.ContextMismatch, "Recovery fingerprint requires graph-scoped target lineage.");
            var pathAssessments = lineage.Path.Select(edgeId => assessmentById.GetValueOrDefault(edgeId)).ToArray();
            var covered = pathAssessments.Length > 0 && pathAssessments.All(item => item is not null && item.Result != RecoveryCheckResult.Fail);
            var modes = pathAssessments.Where(item => item is not null).Select(item => item!.ConfiguredMode)
                .Where(mode => mode is not null).Select(mode => mode!.Value).Distinct().ToArray();
            var recoverable = covered && !modes.Contains(RecoveryMode.Irreversible);
            Add(targetNodeId.Value.ToString("D", CultureInfo.InvariantCulture));
            Add(lineage.Target.Id.Value);
            Add(recoverable.ToString());
            Add(string.Join(',', lineage.Path.Select(id => id.Value.ToString("D", CultureInfo.InvariantCulture))));
        }
        foreach (var issue in issues.OrderBy(issue => issue.Code, StringComparer.Ordinal)
            .ThenBy(issue => issue.EdgeId?.Value))
        {
            Add(issue.Code);
            Add(issue.EdgeId?.Value.ToString("D", CultureInfo.InvariantCulture) ?? string.Empty);
            Add(issue.ArtifactId?.Value ?? string.Empty);
            Add(issue.GraphNodeId?.Value.ToString("D", CultureInfo.InvariantCulture) ?? string.Empty);
        }
        foreach (var connector in projectionBinding.ConnectorVersions.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            Add(connector.Key);
            Add(connector.Value);
        }
        foreach (var checkpoint in rehearsal.Checkpoints.OrderBy(item => item.SystemId.Value, StringComparer.Ordinal).ThenBy(item => item.EndpointId.Value, StringComparer.Ordinal))
        {
            Add(checkpoint.SystemId.Value);
            Add(checkpoint.EndpointId.Value);
            Add(checkpoint.ConnectorVersion);
            Add(checkpoint.ContentSha256);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static async Task<string> FingerprintPlanAsync(string graphHash, string sourceFingerprint, string policyFingerprint,
        IEnumerable<ShadowRecoveryCheckpoint> targetCheckpoints, IEnumerable<RecoveryPlanStep> steps,
        SqliteVerificationLedgerStore ledger, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string part)
        {
            var canonical = $"{Encoding.UTF8.GetByteCount(part).ToString(CultureInfo.InvariantCulture)}:{part};";
            hash.AppendData(Encoding.UTF8.GetBytes(canonical));
        }
        Add(RecoveryFingerprintVersions.Plan);
        Add(graphHash);
        Add(sourceFingerprint);
        Add(policyFingerprint);
        foreach (var checkpoint in targetCheckpoints.OrderBy(item => item.SystemId.Value, StringComparer.Ordinal)
            .ThenBy(item => item.EndpointId.Value, StringComparer.Ordinal))
        {
            Add(checkpoint.SystemId.Value);
            Add(checkpoint.EndpointId.Value);
            Add(checkpoint.ContentSha256);
        }
        foreach (var step in steps.OrderBy(step => step.Sequence))
        {
            Add(step.Sequence.ToString(CultureInfo.InvariantCulture));
            Add(step.EdgeId.Value.ToString("D", CultureInfo.InvariantCulture));
            Add(step.Mode?.ToString() ?? "missing");
            Add(step.Strategy);
            Add(step.Operation);
            Add(step.SystemId.Value);
            Add(step.EndpointId.Value);
            Add(step.Preconditions);
            Add(step.ExpectedOutcome);
            Add(step.ValidationMethod);
            await foreach (var artifact in ledger.ReadDistinctJournalScopeAsync(step.EdgeId, cancellationToken)
                .WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                Add(artifact.NodeId.Value.ToString("D", CultureInfo.InvariantCulture));
                Add(artifact.ArtifactId.Value);
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string FingerprintRehearsal(string baseline, string? restored, string? cleanup,
        RecoveryRehearsalOutcome outcome, long mutatedArtifacts,
        IEnumerable<RecoveryShadowCheckpointBinding> checkpoints)
    {
        var parts = new List<string>
        {
            RecoveryFingerprintVersions.Rehearsal, baseline, restored ?? "unavailable", cleanup ?? "unavailable", outcome.ToString(),
            mutatedArtifacts.ToString(CultureInfo.InvariantCulture)
        };
        foreach (var checkpoint in checkpoints.OrderBy(item => item.Request.SystemId.Value, StringComparer.Ordinal)
            .ThenBy(item => item.Request.EndpointId.Value, StringComparer.Ordinal))
        {
            parts.Add(checkpoint.Request.SystemId.Value);
            parts.Add(checkpoint.Request.EndpointId.Value);
            parts.Add(checkpoint.Connector.Version);
            parts.Add(checkpoint.Checkpoint.ContentSha256);
        }
        return HashParts(parts);
    }

    private static string FingerprintDryRun(VerificationRunRecord verification, EffectiveRecoveryPolicy policy,
        RecoveryAssessment assessment, RecoveryPlan plan, RecoveryRehearsal rehearsal,
        RecoveryEvidenceGraph recoveryEvidence)
    {
        return HashParts([
            RecoveryFingerprintVersions.DryRun, verification.SourceFingerprint, verification.GraphHash,
            verification.ProjectionFingerprint, verification.RuleSetFingerprint, verification.EvidenceFingerprint,
            policy.Fingerprint, assessment.Fingerprint, plan.Fingerprint, rehearsal.Fingerprint,
            recoveryEvidence.Fingerprint
        ]);
    }

    private static string HashParts(IEnumerable<string> parts)
    {
        var canonical = new StringBuilder();
        foreach (var part in parts)
            canonical.Append(Encoding.UTF8.GetByteCount(part).ToString(CultureInfo.InvariantCulture)).Append(':').Append(part).Append(';');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant();
    }

    private static EvidenceId StableEvidenceId(RunId runId, string key)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{runId.Value:D}\n{key}"));
        return new EvidenceId(new Guid(bytes.AsSpan(0, 16)));
    }

    private sealed record TargetState(string Fingerprint, long RecordCount,
        IReadOnlyDictionary<string, (string Fingerprint, long Count)> GroupStates);

    private sealed class RecoveryTargetGroup(string systemKey, string endpointKey, IShadowTargetConnector connector,
        IShadowTargetRecoveryConnector? recoveryConnector, VerificationTargetRuntime[] targets)
    {
        public string SystemKey { get; } = systemKey;
        public string EndpointKey { get; } = endpointKey;
        public IShadowTargetConnector Connector { get; } = connector;
        public IShadowTargetRecoveryConnector? RecoveryConnector { get; } = recoveryConnector;
        public VerificationTargetRuntime[] Targets { get; } = targets;
        public string Key { get; } = $"{systemKey}\0{endpointKey}\0{connector.Id.Value}";
        public string Fingerprint { get; set; } = string.Empty;
        public long TargetCount { get; set; }
        public ShadowRecoveryRequest? Request { get; set; }
        public ShadowRecoveryCheckpoint? Checkpoint { get; set; }
        public bool CheckpointValidated { get; set; }
    }
}
