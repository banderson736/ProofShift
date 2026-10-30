using System.Data.Common;
using System.Net.Sockets;
using ProofShift.Configuration;
using ProofShift.Connectors.Abstractions;
using ProofShift.Domain;
using ProofShift.Engine;
using ProofShift.Evidence;
using ProofShift.Graph;
using ProofShift.Projection;
using ProofShift.Recovery;
using ProofShift.Reporting;
using ProofShift.Snapshots;
using ProofShift.Verification;

namespace ProofShift.Cli;

internal static class WorkspaceRunService
{
    public static async Task<WorkspaceRunResult> RunAsync(string workspaceDirectory, string? checkpointReference,
        string? observationId, CancellationToken cancellationToken, Action<WorkspaceRunProgress>? progress = null,
        string baselineReference = "active", string? reviewReference = null)
    {
        var root = Path.GetFullPath(workspaceDirectory);
        var planned = await WorkspaceEvaluationService.PlanAsync(root, cancellationToken).ConfigureAwait(false);
        if (planned.Plan is null)
            return new(null, null, planned.Preflight.Checks);
        var plannedDrift = await WorkspaceSemanticDriftService.CompareAsync(root, planned, baselineReference,
            cancellationToken).ConfigureAwait(false);
        ValidateDriftCanRun(plannedDrift);

        var analysis = await WorkspaceEvaluationService.AnalyzeAsync(root, connect: true, cancellationToken,
            replayCheckpoint: checkpointReference is not null).ConfigureAwait(false);
        if (analysis.Manifest is null)
            return new(null, null, analysis.Preflight.Checks);

        var plan = WorkspaceEvaluationService.CreatePlan(analysis);
        if (plan.Plan is null)
            return new(null, null, analysis.Preflight.Checks);
        var executionDrift = await WorkspaceSemanticDriftService.CompareAsync(root, plan, baselineReference,
            cancellationToken).ConfigureAwait(false);
        ValidateDriftCanRun(executionDrift);
        var selectedBaseline = baselineReference == "none"
            ? null
            : await WorkspaceBaselineService.ReadPlanAsync(root, baselineReference, cancellationToken)
                .ConfigureAwait(false);
        if (selectedBaseline?.Baseline.BaselineId != executionDrift.BaselineId)
            throw new InvalidOperationException("The selected baseline changed while preparing the run; plan again before execution.");
        if (!string.Equals(WorkspacePlanReviewService.IdentityFingerprint(plannedDrift),
                WorkspacePlanReviewService.IdentityFingerprint(executionDrift), StringComparison.Ordinal))
            throw new InvalidOperationException("The selected baseline or effective plan changed after planning; review the current plan before execution.");

        WorkspacePlanReviewRecord? review = null;
        if (executionDrift.Status is WorkspaceSemanticDriftStatus.ReviewRequired or
            WorkspaceSemanticDriftStatus.NotComparable)
        {
            if (reviewReference is null)
                throw new InvalidOperationException(executionDrift.Status == WorkspaceSemanticDriftStatus.NotComparable
                    ? "Baseline comparison is unavailable; explicitly acknowledge this exact comparison with workspace review-plan before execution."
                    : "Semantic drift requires an exact plan review before execution.");
            review = await WorkspacePlanReviewService.ValidateForExecutionAsync(root, reviewReference,
                executionDrift, cancellationToken).ConfigureAwait(false);
        }
        else if (reviewReference is not null)
        {
            review = await WorkspacePlanReviewService.ValidateForExecutionAsync(root, reviewReference,
                executionDrift, cancellationToken).ConfigureAwait(false);
        }

        var manifest = analysis.Manifest;
        var configuration = analysis.Configuration
            ?? throw new InvalidOperationException("Validated workspace has no loaded project configuration.");
        var graph = analysis.Graph
            ?? throw new InvalidOperationException("Validated workspace has no compiled migration graph.");
        var now = DateTimeOffset.UtcNow;
        var runId = Guid.NewGuid();
        var driftArtifact = executionDrift.BaselineId is null
            ? default
            : await WorkspaceSemanticDriftArtifactStore.WriteAsync(root, manifest.WorkspaceId, runId,
                executionDrift, cancellationToken).ConfigureAwait(false);
        var runDirectory = Path.GetFullPath(Path.Combine(root,
            manifest.Paths.Runs.Replace('/', Path.DirectorySeparatorChar),
            runId.ToString("D")));
        if (!ConnectorPathUtilities.IsContained(root, runDirectory))
            throw new InvalidDataException("Workspace run directory is outside its configured run path.");

        var discoveryFingerprints = await ReadDiscoveryFingerprintsAsync(root, manifest, cancellationToken)
            .ConfigureAwait(false);
        var reconciliationRules = analysis.RuleConfiguration!
            .Where(rule => rule.Type.Contains("reconciliation", StringComparison.OrdinalIgnoreCase))
            .Select(rule => rule.Id.Value)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var record = new WorkspaceRunManifest
        {
            WorkspaceId = manifest.WorkspaceId,
            RunId = runId,
            ProductVersion = VersionCommands.GetProductVersion(),
            SourceRevision = VersionCommands.GetAssemblyMetadata("ProofShiftSourceRevision"),
            SourceTree = VersionCommands.GetAssemblyMetadata("ProofShiftSourceTree"),
            BaselineId = executionDrift.BaselineId,
            BaselineRunId = executionDrift.BaselineRunId,
            BaselineRunManifestIdentityFingerprint = selectedBaseline?.Baseline.RunManifestIdentityFingerprint,
            BaselinePlanFingerprint = executionDrift.BaselinePlanFingerprint,
            BaselineEvidencePackageIdentity = selectedBaseline?.Baseline.EvidencePackageIdentity,
            PreRunSemanticDriftIdentityFingerprint = WorkspacePlanReviewService.IdentityFingerprint(executionDrift),
            PreRunSemanticDriftStatus = executionDrift.Status.ToString(),
            SemanticDriftArtifactReference = driftArtifact.Reference,
            SemanticDriftArtifactIntegrityHash = driftArtifact.IntegrityHash,
            ReviewRecordId = review?.ReviewId,
            ReviewRecordIntegrityHash = review?.IntegrityHash,
            ReviewDecision = review?.Decision ?? "NotRequired",
            StartedAt = now,
            UpdatedAt = now,
            State = "Created",
            PlanFingerprint = plan.Plan.Fingerprint,
            ConfigurationHash = configuration.ConfigurationHash,
            GraphHash = graph.GraphHash,
            DiscoveryFingerprints = discoveryFingerprints,
            ReconciliationRuleIds = reconciliationRules,
            ReadinessChecks = analysis.Preflight.Checks
        };
        await WorkspaceRunManifestStore.WriteAsync(runDirectory, record, createNew: true, cancellationToken)
            .ConfigureAwait(false);

        async Task RecordProgressAsync(string stage, string status,
            Func<WorkspaceRunManifest, WorkspaceRunManifest>? update = null,
            CancellationToken? writeToken = null)
        {
            var item = new WorkspaceRunProgress(stage, status, DateTimeOffset.UtcNow);
            record = (update is null ? record : update(record)) with
            {
                UpdatedAt = item.At,
                Progress = [.. record.Progress, item]
            };
            await WorkspaceRunManifestStore.WriteAsync(runDirectory, record, createNew: false,
                writeToken ?? cancellationToken).ConfigureAwait(false);
            progress?.Invoke(item);
        }

        if (analysis.Preflight.Status != "READY")
        {
            await RecordProgressAsync("readiness", "blocked", value => value with
            {
                State = "PreflightBlocked",
                FailureCode = "PSWORKRUN_PREFLIGHT_BLOCKED"
            }).ConfigureAwait(false);
            return Result();
        }

        var targetNodes = graph.Nodes.Where(node => node.Type is MigrationNodeType.Target or MigrationNodeType.Archive)
            .OrderBy(node => node.Name, StringComparer.Ordinal).ToArray();
        var roles = targetNodes.Select(node => configuration.Systems.Single(system => system.Id == node.SystemId).Role)
            .Distinct().ToArray();
        var modeIssue = targetNodes.Length == 0
            ? "Guided execution requires at least one configured target node."
            : roles.Length != 1 || roles[0] is not (SystemRole.ShadowTarget or SystemRole.Target)
                ? "Guided execution requires either shadow-target nodes or externally observed target nodes, not a mixture."
                : roles[0] == SystemRole.ShadowTarget && !string.IsNullOrWhiteSpace(observationId)
                    ? "An observation label is only valid for external read-only Verification."
                    : roles[0] == SystemRole.Target && string.IsNullOrWhiteSpace(observationId)
                        ? "External read-only Verification requires --observation <label>."
                        : null;
        if (modeIssue is not null)
        {
            var modeCheck = new WorkspacePreflightCheck("PSPREF015", "execution-mode", "BLOCKED",
                modeIssue, null, "Correct the target roles or provide the required external observation label.");
            await RecordProgressAsync("readiness", "blocked", value => value with
            {
                State = "PreflightBlocked",
                FailureCode = "PSWORKRUN_UNSUPPORTED_MODE",
                ReadinessChecks = [.. value.ReadinessChecks, modeCheck]
            }).ConfigureAwait(false);
            return Result();
        }

        await RecordProgressAsync("readiness", "completed", value => value with { State = "Running" })
            .ConfigureAwait(false);

        try
        {
            var checkpointStore = CreateCheckpointStore(root, checkpointReference, out var checkpointKey,
                out var checkpointScope);
            if (checkpointReference is null)
            {
                await RecordProgressAsync("checkpoint", "started").ConfigureAwait(false);
                var sourceRegistry = SourceRegistry(analysis.Connectors!, configuration, graph);
                var capture = await new SnapshotCaptureService(sourceRegistry, checkpointStore,
                    new RuntimeConnectorContextFactory(baseDirectory: Path.GetDirectoryName(analysis.ConfigurationPath)!))
                    .CaptureAsync(configuration, graph, cancellationToken).ConfigureAwait(false);
                if (capture.Status != CheckpointStatus.Complete || capture.Checkpoint is null)
                {
                    var interrupted = capture.Status == CheckpointStatus.Cancelled;
                    await RecordProgressAsync("checkpoint", interrupted ? "interrupted" : "failed",
                        value => value with
                        {
                            State = interrupted ? "Interrupted" : "Failed",
                            CheckpointId = capture.Id.Value.ToString("D"),
                            CheckpointStoreScope = "workspace",
                            ReplayedCheckpoint = false,
                            FailureCode = capture.FailureCode ?? "PSWORKRUN_CHECKPOINT_INCOMPLETE"
                        }, CancellationToken.None).ConfigureAwait(false);
                    return Result();
                }

                checkpointKey = capture.Id.Value.ToString("N");
                checkpointScope = "workspace";
                await RecordProgressAsync("checkpoint", "captured", value => value with
                {
                    CheckpointId = capture.Id.Value.ToString("D"),
                    CheckpointManifestHash = capture.Checkpoint.ManifestHash,
                    CheckpointSourceFingerprint = capture.Checkpoint.SourceFingerprint,
                    CheckpointStoreScope = checkpointScope,
                    ReplayedCheckpoint = false
                }).ConfigureAwait(false);
            }
            else
            {
                if (!Guid.TryParse(checkpointKey, out var selectedCheckpointId) || selectedCheckpointId == Guid.Empty)
                    throw new SnapshotStoreException(SnapshotIssueCodes.SnapshotPathInvalid, "Checkpoint reference does not contain a valid checkpoint identity.");
                await RecordProgressAsync("checkpoint", "replay-selected", value => value with
                {
                    CheckpointId = selectedCheckpointId.ToString("D"),
                    CheckpointStoreScope = checkpointScope,
                    ReplayedCheckpoint = true
                }).ConfigureAwait(false);
            }

            var checkpointArgument = checkpointKey
                ?? throw new InvalidDataException("Checkpoint identity was not resolved.");
            await using var loadedCheckpoint = await checkpointStore.OpenCompleteAsync(checkpointArgument, cancellationToken)
                .ConfigureAwait(false);
            var sourceCheckpoint = loadedCheckpoint.Manifest;
            await RecordProgressAsync("checkpoint", "completed", value => value with
            {
                CheckpointId = sourceCheckpoint.Id.Value.ToString("D"),
                CheckpointManifestHash = sourceCheckpoint.ManifestHash,
                CheckpointSourceFingerprint = sourceCheckpoint.SourceFingerprint,
                CheckpointStoreScope = checkpointScope,
                ReplayedCheckpoint = checkpointReference is not null
            }).ConfigureAwait(false);

            if (roles[0] == SystemRole.ShadowTarget)
            {
                await ExecuteShadowRunAsync(root, runDirectory, analysis, configuration, graph, loadedCheckpoint,
                    checkpointStore, targetNodes, reconciliationRules, RecordProgressAsync, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await ExecuteExternalRunAsync(root, runDirectory, analysis, configuration, graph, loadedCheckpoint,
                    checkpointStore, targetNodes, reconciliationRules,
                    observationId ?? throw new InvalidDataException("External observation label is unavailable."),
                    RecordProgressAsync,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await RecordProgressAsync("execution", "interrupted", value => value with
            {
                State = "Interrupted",
                Disposition = null,
                FailureCode = "PSWORKRUN_CANCELLED"
            }, CancellationToken.None).ConfigureAwait(false);
            return Result();
        }
        catch (Exception exception) when (IsExecutionFailure(exception))
        {
            await RecordProgressAsync("execution", "failed", value => value with
            {
                State = "Failed",
                Disposition = null,
                FailureCode = FailureCode(exception)
            }, CancellationToken.None).ConfigureAwait(false);
            return Result();
        }

        if (record.State == "Completed" && record.BaselineId is not null)
        {
            record = await WorkspaceRunManifestStore.ReadAsync(runDirectory, cancellationToken)
                .ConfigureAwait(false);
            var baselineComparison = await WorkspaceBaselineComparisonService.CompareAndPersistAsync(root, record,
                cancellationToken).ConfigureAwait(false);
            return Result() with { BaselineComparison = baselineComparison };
        }

        return Result();

        WorkspaceRunResult Result() => new(record, Path.GetRelativePath(root, runDirectory)
            .Replace(Path.DirectorySeparatorChar, '/'), record.ReadinessChecks);
    }

    internal static void ValidateDriftCanRun(WorkspaceSemanticDrift drift)
    {
        if (drift.Status == WorkspaceSemanticDriftStatus.Blocking || drift.BlockingConditions.Count > 0)
            throw new InvalidOperationException("Blocking plan conditions cannot be overridden by review.");
        if (drift.BaselineId is null && drift.Status is not WorkspaceSemanticDriftStatus.NoBaseline)
            throw new InvalidDataException("Baseline comparison did not resolve to a valid baseline or an explicit no-baseline selection.");
    }

    private static async Task ExecuteShadowRunAsync(string root, string runDirectory,
        WorkspaceEvaluationService.WorkspaceAnalysis analysis, LoadedProjectConfiguration configuration,
        MigrationGraph graph, ILoadedCheckpoint checkpoint, IMaterializedSnapshotStore checkpointStore,
        MigrationNode[] targetNodes, IReadOnlyList<string> reconciliationRules,
        Func<string, string, Func<WorkspaceRunManifest, WorkspaceRunManifest>?, CancellationToken?, Task> recordProgress,
        CancellationToken cancellationToken)
    {
        var connectors = analysis.Connectors!;
        var sourceRegistry = SourceRegistry(connectors, configuration, graph);
        var shadowRegistry = ShadowRegistry(connectors, configuration, targetNodes);
        var contextFactory = new RuntimeConnectorContextFactory(
            baseDirectory: Path.GetDirectoryName(analysis.ConfigurationPath)!);
        var projectionService = new ShadowProjectionService(sourceRegistry, shadowRegistry, contextFactory,
            new CheckpointSourceArtifactStreamProvider(checkpoint));

        await recordProgress("projection", "started", null, null).ConfigureAwait(false);
        var projection = await projectionService.ProjectAsync(configuration, graph, runDirectory, cancellationToken)
            .ConfigureAwait(false);
        var projectionManifestHash = await ProjectionRunManifestStore.WriteAsync(runDirectory, projection, cancellationToken)
            .ConfigureAwait(false);
        await recordProgress("projection", projection.Status == ProjectionStatus.Succeeded ? "completed" : "failed",
            value => value with
            {
                ProjectionRunId = projection.Id.Value.ToString("D"),
                ProjectionManifestReference = WorkspaceRelative(root,
                    Path.Combine(runDirectory, ".proofshift", "projections", projection.Id.Value.ToString("N"), "run.json")),
                ProjectionManifestHash = projectionManifestHash,
                ProjectionFingerprint = projection.Fingerprint,
                FailureCode = projection.Status == ProjectionStatus.Succeeded ? value.FailureCode :
                    projection.FailureCode ?? "PSWORKRUN_PROJECTION_FAILED"
            }, cancellationToken).ConfigureAwait(false);
        if (projection.Status != ProjectionStatus.Succeeded)
        {
            await recordProgress("execution", "failed", value => value with
            {
                State = projection.Status == ProjectionStatus.Cancelled ? "Interrupted" : "Failed",
                Disposition = null,
                FailureCode = projection.FailureCode ?? "PSWORKRUN_PROJECTION_FAILED"
            }, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        var binding = new ProjectionVerificationBinding(projection.Id, projection.ConfigurationHash,
            projection.GraphHash, checkpoint.Manifest.Id, checkpoint.Manifest.ManifestHash!,
            checkpoint.Manifest.SourceFingerprint!, projection.Fingerprint!,
            projection.FingerprintVersion, projection.SourceArtifactCount, projection.TargetArtifactCount,
            projection.Status.ToString().ToLowerInvariant(), projectionManifestHash, projection.JournalPath,
            projection.ConnectorVersions);
        var targets = ShadowTargets(connectors, contextFactory, configuration, targetNodes, projection.Id);
        var verificationService = new VerificationService(checkpointStore);
        await recordProgress("verification", "started", null, null).ConfigureAwait(false);
        await WorkspaceCleanupService.MarkTemporaryOwnedAsync(runDirectory,
            await WorkspaceRunManifestStore.ReadAsync(runDirectory, cancellationToken).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
        var verification = await verificationService.VerifyAsync(configuration, graph, binding, analysis.RuleSet!,
            runDirectory, Path.Combine(runDirectory, "temporary"), VersionCommands.GetProductVersion(),
            targets, cancellationToken).ConfigureAwait(false);
        var evidenceReference = await PersistEvidenceAsync(root, runDirectory, verification.EvidenceGraph,
            cancellationToken).ConfigureAwait(false);
        var failedReconciliationRules = FailedReconciliationRules(verification.Findings, reconciliationRules);
        await recordProgress("verification", verification.Run.State == VerificationRunState.Complete ? "completed" : "failed",
            value => value with
            {
                VerificationRunId = verification.Run.Id.Value.ToString("D"),
                VerificationOutcome = verification.Run.Outcome.ToString(),
                VerificationEvidenceFingerprint = verification.Run.EvidenceFingerprint,
                VerificationLedgerFingerprint = verification.Ledger.Fingerprint,
                VerificationLedgerReference = WorkspaceRelative(root,
                    Path.Combine(runDirectory, verification.Ledger.RelativePath.Replace('/', Path.DirectorySeparatorChar))),
                ReconciliationFailedRuleIds = failedReconciliationRules,
                EvidenceReference = evidenceReference.Reference,
                EvidenceManifestIntegrityHash = evidenceReference.ManifestHash
            }, cancellationToken).ConfigureAwait(false);
        if (verification.Run.State != VerificationRunState.Complete ||
            verification.Run.Outcome is VerificationOutcome.Error or VerificationOutcome.Cancelled)
        {
            await recordProgress("execution",
                verification.Run.Outcome == VerificationOutcome.Cancelled ? "interrupted" : "failed",
                value => value with
                {
                    State = verification.Run.Outcome == VerificationOutcome.Cancelled ? "Interrupted" : "Failed",
                    Disposition = null,
                    FailureCode = verification.Run.Outcome == VerificationOutcome.Cancelled
                        ? "PSWORKRUN_CANCELLED" : "PSWORKRUN_VERIFICATION_INCOMPLETE"
                }, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        var recoveryService = new RecoveryService(checkpointStore,
            new RecoveryCompensatorRegistry([new ShadowBaselineRestoreCompensator()]));
        var recoveryStore = new FileSystemRecoveryArtifactStore(Path.Combine(runDirectory, "recovery"));
        await recordProgress("recovery", "started", null, null).ConfigureAwait(false);
        var recovery = await recoveryService.AssessAndRehearseAsync(configuration, graph, binding, verification,
            analysis.RecoveryPolicy!, targets,
            runDirectory, cancellationToken).ConfigureAwait(false);
        var recoveryReceipt = await recoveryStore.SaveAsync(recovery, cancellationToken).ConfigureAwait(false);
        var recoveryReference = WorkspaceRelative(root, Path.Combine(runDirectory, "recovery",
            recoveryReceipt.RelativeDirectory.Replace('/', Path.DirectorySeparatorChar), "manifest.json"));
        await recordProgress("recovery",
            recovery.Qualification.Status is DryRunQualificationStatus.Qualified or DryRunQualificationStatus.NotQualified
                ? "completed" : "failed",
            value => value with
            {
                RecoveryRunId = recovery.Id.Value.ToString("D"),
                RecoveryQualification = recovery.Qualification.Status.ToString(),
                RecoveryArtifactReference = recoveryReference,
                RecoveryArtifactIntegrityHash = recoveryReceipt.IntegrityHash
            }, cancellationToken).ConfigureAwait(false);
        if (recovery.Qualification.Status is DryRunQualificationStatus.Error or DryRunQualificationStatus.Cancelled)
        {
            await recordProgress("execution",
                recovery.Qualification.Status == DryRunQualificationStatus.Cancelled ? "interrupted" : "failed",
                value => value with
                {
                    State = recovery.Qualification.Status == DryRunQualificationStatus.Cancelled ? "Interrupted" : "Failed",
                    Disposition = null,
                    FailureCode = recovery.Qualification.Status == DryRunQualificationStatus.Cancelled
                        ? "PSWORKRUN_CANCELLED" : "PSWORKRUN_RECOVERY_INCOMPLETE"
                }, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        var disposition = recovery.Qualification.Status == DryRunQualificationStatus.Qualified &&
            verification.Run.Outcome is VerificationOutcome.Passed or VerificationOutcome.PassedWithWarnings
            ? "QUALIFIED" : "NOT QUALIFIED";
        await recordProgress("completed", "completed", value => value with
        {
            State = "Completed",
            Disposition = disposition,
            FailureCode = null
        }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExecuteExternalRunAsync(string root, string runDirectory,
        WorkspaceEvaluationService.WorkspaceAnalysis analysis, LoadedProjectConfiguration configuration,
        MigrationGraph graph, ILoadedCheckpoint checkpoint, IMaterializedSnapshotStore checkpointStore,
        MigrationNode[] targetNodes, IReadOnlyList<string> reconciliationRules,
        string observationId,
        Func<string, string, Func<WorkspaceRunManifest, WorkspaceRunManifest>?, CancellationToken?, Task> recordProgress,
        CancellationToken cancellationToken)
    {
        var projectId = configuration.Root.Project?.Id;
        if (string.IsNullOrWhiteSpace(projectId))
            throw new InvalidDataException("External Verification requires a configured project identity.");

        var observationRunId = new RunId(Guid.NewGuid());
        var contextFactory = new RuntimeConnectorContextFactory(
            baseDirectory: Path.GetDirectoryName(analysis.ConfigurationPath)!);
        var endpoints = new List<ExternalTargetEndpoint>(targetNodes.Length);
        var targets = new List<VerificationTargetRuntime>(targetNodes.Length);
        foreach (var node in targetNodes.OrderBy(node => node.Name, StringComparer.Ordinal))
        {
            var system = configuration.Systems.Single(item => item.Id == node.SystemId);
            var endpoint = system.StorageEndpoints.Single(item => item.Id == node.EndpointId);
            var capability = analysis.Connectors!.Capabilities.SingleOrDefault(item => item.Id == endpoint.Connector.Value);
            if (capability is null || !capability.TargetObservation)
                throw new InvalidDataException($"Target node '{node.Name}' does not support read-only observation.");
            var observer = analysis.Connectors.Observer(endpoint.Connector.Value);
            endpoints.Add(new ExternalTargetEndpoint(node.Name, system.Id, endpoint.Id, observer.Id, observer.Version));
            targets.Add(new VerificationTargetRuntime(node.Name, observer,
                new TargetObservationContext(contextFactory.Create(configuration, node), observationRunId, SystemRole.Target)));
        }

        var observation = new ExternalMigrationObservation(observationRunId, observationId,
            configuration.ConfigurationHash, graph.GraphHash, checkpoint.Manifest.Id,
            checkpoint.Manifest.ManifestHash!, checkpoint.Manifest.SourceFingerprint!, endpoints, DateTimeOffset.UtcNow);
        await recordProgress("verification", "started", null, null).ConfigureAwait(false);
        await WorkspaceCleanupService.MarkTemporaryOwnedAsync(runDirectory,
            await WorkspaceRunManifestStore.ReadAsync(runDirectory, cancellationToken).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
        var verification = await new VerificationService(checkpointStore).VerifyExternalTargetAsync(
            configuration, graph, observation, analysis.RuleSet!, runDirectory,
            Path.Combine(runDirectory, "temporary"), VersionCommands.GetProductVersion(), targets, cancellationToken)
            .ConfigureAwait(false);
        var evidenceReference = await PersistEvidenceAsync(root, runDirectory, verification.EvidenceGraph,
            cancellationToken).ConfigureAwait(false);
        await new FileSystemExternalVerificationRunStore(runDirectory)
            .SaveAsync(projectId, verification, cancellationToken).ConfigureAwait(false);
        var failedReconciliationRules = FailedReconciliationRules(verification.Findings, reconciliationRules);
        var ledgerReference = WorkspaceRelative(root, Path.Combine(runDirectory,
            verification.Ledger.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
        await recordProgress("verification", "completed", value => value with
        {
            VerificationRunId = verification.Run.Id.Value.ToString("D"),
            VerificationOutcome = verification.Run.Outcome.ToString(),
            VerificationEvidenceFingerprint = verification.Run.EvidenceFingerprint,
            VerificationLedgerFingerprint = verification.Ledger.Fingerprint,
            VerificationLedgerReference = ledgerReference,
            ReconciliationFailedRuleIds = failedReconciliationRules,
            EvidenceReference = evidenceReference.Reference,
            EvidenceManifestIntegrityHash = evidenceReference.ManifestHash
        }, cancellationToken).ConfigureAwait(false);

        if (verification.Run.Outcome is VerificationOutcome.Error or VerificationOutcome.Cancelled)
        {
            await recordProgress("execution",
                verification.Run.Outcome == VerificationOutcome.Cancelled ? "interrupted" : "failed",
                value => value with
                {
                    State = verification.Run.Outcome == VerificationOutcome.Cancelled ? "Interrupted" : "Failed",
                    Disposition = null,
                    FailureCode = verification.Run.Outcome == VerificationOutcome.Cancelled
                        ? "PSWORKRUN_CANCELLED" : "PSWORKRUN_VERIFICATION_INCOMPLETE"
                }, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        await recordProgress("completed", "completed", value => value with
        {
            State = "Completed",
            Disposition = null,
            FailureCode = null
        }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<(string Reference, string ManifestHash)> PersistEvidenceAsync(string root,
        string runDirectory, EvidenceGraph evidence, CancellationToken cancellationToken)
    {
        var evidenceRoot = Path.Combine(runDirectory, "evidence");
        var store = new FileSystemEvidenceStore(evidenceRoot);
        var receipt = await store.SaveAsync(evidence, cancellationToken).ConfigureAwait(false);
        if (!await store.VerifyIntegrityAsync(evidence.VerificationRunId, cancellationToken).ConfigureAwait(false))
            throw new InvalidDataException("Persisted Evidence failed integrity verification.");
        var manifest = await store.ReadManifestAsync(evidence.VerificationRunId, cancellationToken).ConfigureAwait(false);
        var path = Path.Combine(evidenceRoot, receipt.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        return (WorkspaceRelative(root, path), manifest.IntegrityHash
            ?? throw new InvalidDataException("Evidence manifest has no integrity hash."));
    }

    private static async Task<IReadOnlyList<string>> ReadDiscoveryFingerprintsAsync(string root,
        EvaluationWorkspaceManifest manifest, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(root, manifest.Paths.Discovery.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(directory)) return [];
        var fingerprints = new List<string>();
        foreach (var artifactDirectory in Directory.EnumerateDirectories(directory).Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var artifact = await PhysicalDiscoveryStore.ReadAsync(artifactDirectory, cancellationToken).ConfigureAwait(false);
            fingerprints.Add(artifact.Fingerprint);
        }

        return fingerprints.Order(StringComparer.Ordinal).ToArray();
    }

    private static string[] FailedReconciliationRules(IEnumerable<VerificationFinding> findings,
        IReadOnlyCollection<string> reconciliationRules)
    {
        var configured = reconciliationRules.ToHashSet(StringComparer.Ordinal);
        return findings.Where(finding => finding.Result == EvidenceResult.Fail && configured.Contains(finding.RuleId.Value))
            .Select(finding => finding.RuleId.Value).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToArray();
    }

    private static ConnectorRegistry SourceRegistry(ConnectorCatalog catalog, LoadedProjectConfiguration configuration,
        MigrationGraph graph)
    {
        var connectorIds = graph.Nodes.Where(node => node.Type == MigrationNodeType.Source)
            .Select(node => configuration.Systems.Single(system => system.Id == node.SystemId)
                .StorageEndpoints.Single(endpoint => endpoint.Id == node.EndpointId).Connector.Value)
            .Distinct(StringComparer.Ordinal);
        return new ConnectorRegistry(connectorIds.Select(catalog.Reader));
    }

    private static ShadowTargetConnectorRegistry ShadowRegistry(ConnectorCatalog catalog,
        LoadedProjectConfiguration configuration, IEnumerable<MigrationNode> targetNodes)
    {
        var connectorIds = targetNodes.Select(node => configuration.Systems.Single(system => system.Id == node.SystemId)
                .StorageEndpoints.Single(endpoint => endpoint.Id == node.EndpointId).Connector.Value)
            .Distinct(StringComparer.Ordinal);
        return new ShadowTargetConnectorRegistry(connectorIds.Select(catalog.ShadowWriter));
    }

    private static VerificationTargetRuntime[] ShadowTargets(ConnectorCatalog catalog,
        RuntimeConnectorContextFactory contextFactory, LoadedProjectConfiguration configuration,
        IEnumerable<MigrationNode> targetNodes, RunId projectionRunId) =>
        targetNodes.OrderBy(node => node.Name, StringComparer.Ordinal).Select(node =>
        {
            var system = configuration.Systems.Single(item => item.Id == node.SystemId);
            var endpoint = system.StorageEndpoints.Single(item => item.Id == node.EndpointId);
            var connector = catalog.ShadowWriter(endpoint.Connector.Value);
            return new VerificationTargetRuntime(node.Name, connector,
                new ShadowTargetContext(contextFactory.Create(configuration, node), projectionRunId, system.Role));
        }).ToArray();

    private static FileSystemSnapshotStore CreateCheckpointStore(string root, string? checkpointReference,
        out string? checkpointKey, out string scope)
    {
        var defaultRoot = Path.Combine(root, ".proofshift", "checkpoints");
        checkpointKey = null;
        scope = "workspace";
        if (checkpointReference is null)
            return new FileSystemSnapshotStore(defaultRoot);

        if (Guid.TryParse(checkpointReference, out var parsedId) && parsedId != Guid.Empty)
        {
            checkpointKey = parsedId.ToString("N");
            return new FileSystemSnapshotStore(defaultRoot);
        }

        var path = Path.GetFullPath(checkpointReference, root);
        var directoryName = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        if (!Guid.TryParseExact(directoryName, "N", out var pathId) || pathId == Guid.Empty)
            throw new SnapshotStoreException(SnapshotIssueCodes.SnapshotPathInvalid,
                "Checkpoint path must identify a checkpoint directory named by its GUID.");
        checkpointKey = pathId.ToString("N");
        scope = ConnectorPathUtilities.IsContained(root, path) ? "workspace" : "external";
        return new FileSystemSnapshotStore(Path.GetDirectoryName(path)
            ?? throw new SnapshotStoreException(SnapshotIssueCodes.SnapshotPathInvalid, "Checkpoint parent directory is unavailable."));
    }

    private static string WorkspaceRelative(string root, string path)
    {
        var fullRoot = Path.GetFullPath(root);
        var fullPath = Path.GetFullPath(path);
        if (!ConnectorPathUtilities.IsContained(fullRoot, fullPath))
            throw new InvalidDataException("Workspace artifact reference is outside the workspace.");
        return Path.GetRelativePath(fullRoot, fullPath).Replace(Path.DirectorySeparatorChar, '/');
    }

    private static bool IsExecutionFailure(Exception exception) => exception is
        IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or
        InvalidDataException or FormatException or OverflowException or NotSupportedException or
        DbException or SocketException or TimeoutException or System.Text.Json.JsonException or
        ConnectorConfigurationException or ConnectorReadException or ConnectorResolutionException or
        SnapshotStoreException or ProjectionExecutionException or ProjectionManifestException or
        VerificationRuleException or RecoveryException;

    private static string FailureCode(Exception exception) => exception switch
    {
        SnapshotStoreException snapshot => snapshot.Code,
        ConnectorConfigurationException connector => connector.Code,
        ConnectorReadException connector => connector.Code,
        ConnectorResolutionException connector => connector.Code,
        ProjectionExecutionException projection => projection.Code,
        VerificationRuleException verification => verification.Code,
        RecoveryException recovery => recovery.Code,
        _ => "PSWORKRUN_EXECUTION_FAILED"
    };
}
