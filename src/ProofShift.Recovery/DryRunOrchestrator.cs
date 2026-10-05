using System.Globalization;
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

public enum DryRunExecutionOutcome
{
    Qualified,
    NotQualified,
    Failed,
    Cancelled
}

public sealed record DryRunExecutionResult(
    DryRunExecutionOutcome Outcome,
    SnapshotCaptureResult? Checkpoint,
    ProjectionRun? Projection,
    VerificationResult? Verification,
    RecoveryRunResult? Recovery,
    EvidenceStoreReceipt? VerificationEvidence,
    RecoveryArtifactReceipt? RecoveryArtifacts,
    string? FailureCode,
    PerformanceRun? Performance = null);

public sealed class DryRunOrchestrator
{
    private readonly ConnectorRegistry _sourceConnectors;
    private readonly ShadowTargetConnectorRegistry _targetConnectors;
    private readonly RuntimeConnectorContextFactory _contextFactory;
    private readonly IMaterializedSnapshotStore _snapshotStore;
    private readonly VerificationService _verificationService;
    private readonly RecoveryService _recoveryService;

    public DryRunOrchestrator(ConnectorRegistry sourceConnectors, ShadowTargetConnectorRegistry targetConnectors,
        IMaterializedSnapshotStore snapshotStore, VerificationService verificationService, RecoveryService recoveryService,
        RuntimeConnectorContextFactory? contextFactory = null)
    {
        _sourceConnectors = sourceConnectors ?? throw new ArgumentNullException(nameof(sourceConnectors));
        _targetConnectors = targetConnectors ?? throw new ArgumentNullException(nameof(targetConnectors));
        _snapshotStore = snapshotStore ?? throw new ArgumentNullException(nameof(snapshotStore));
        _verificationService = verificationService ?? throw new ArgumentNullException(nameof(verificationService));
        _recoveryService = recoveryService ?? throw new ArgumentNullException(nameof(recoveryService));
        _contextFactory = contextFactory ?? new RuntimeConnectorContextFactory();
    }

    public Task<DryRunExecutionResult> RunAsync(LoadedProjectConfiguration configuration, MigrationGraph graph,
        VerificationRuleSet ruleSet, EffectiveRecoveryPolicy recoveryPolicy, string projectDirectory,
        string temporaryDirectory, string proofShiftVersion, IEvidenceStore evidenceStore,
        IRecoveryArtifactStore recoveryArtifactStore, CancellationToken cancellationToken) =>
        RunAsync(configuration, graph, ruleSet, recoveryPolicy, projectDirectory, temporaryDirectory,
            proofShiftVersion, evidenceStore, recoveryArtifactStore, performanceRecorder: null, cancellationToken);

    public async Task<DryRunExecutionResult> RunAsync(LoadedProjectConfiguration configuration, MigrationGraph graph,
        VerificationRuleSet ruleSet, EffectiveRecoveryPolicy recoveryPolicy, string projectDirectory,
        string temporaryDirectory, string proofShiftVersion, IEvidenceStore evidenceStore,
        IRecoveryArtifactStore recoveryArtifactStore, PerformanceRecorder? performanceRecorder,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(ruleSet);
        ArgumentNullException.ThrowIfNull(recoveryPolicy);
        ArgumentNullException.ThrowIfNull(evidenceStore);
        ArgumentNullException.ThrowIfNull(recoveryArtifactStore);
        var recorder = performanceRecorder ?? new PerformanceRecorder(configuration.Root.Project?.Id ?? "dry-run");

        DryRunExecutionResult Complete(DryRunExecutionOutcome outcome, SnapshotCaptureResult? capturedCheckpoint,
            ProjectionRun? projected, VerificationResult? verified, RecoveryRunResult? recovered,
            EvidenceStoreReceipt? savedEvidence, RecoveryArtifactReceipt? savedRecovery, string? failureCode) =>
            new(outcome, capturedCheckpoint, projected, verified, recovered, savedEvidence, savedRecovery,
                failureCode, recorder.Complete());

        SnapshotCaptureResult? checkpoint = null;
        ProjectionRun? projection = null;
        VerificationResult? verification = null;
        EvidenceStoreReceipt? evidenceReceipt = null;
        try
        {
            var checkpointService = new SnapshotCaptureService(_sourceConnectors, _snapshotStore, _contextFactory);
            checkpoint = await checkpointService.CaptureAsync(configuration, graph, recorder, cancellationToken).ConfigureAwait(false);
            if (checkpoint.Status == CheckpointStatus.Cancelled)
                return Complete(DryRunExecutionOutcome.Cancelled, checkpoint, null, null, null, null, null, checkpoint.FailureCode);
            if (checkpoint.Status != CheckpointStatus.Complete || checkpoint.Checkpoint is null)
                return Complete(DryRunExecutionOutcome.Failed, checkpoint, null, null, null, null, null, checkpoint.FailureCode);

            await using (var loadedCheckpoint = await _snapshotStore.OpenCompleteAsync(
                checkpoint.Id.Value.ToString("N", CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false))
            {
                var projectionService = new ShadowProjectionService(_sourceConnectors, _targetConnectors,
                    _contextFactory, new CheckpointSourceArtifactStreamProvider(loadedCheckpoint));
                projection = await projectionService.ProjectAsync(configuration, graph, projectDirectory,
                    recorder, cancellationToken).ConfigureAwait(false);
            }
            if (projection.Status == ProjectionStatus.Cancelled)
                return Complete(DryRunExecutionOutcome.Cancelled, checkpoint, projection, null, null, null, null, projection.FailureCode);
            if (projection.Status != ProjectionStatus.Succeeded || projection.Fingerprint is null)
                return Complete(DryRunExecutionOutcome.NotQualified, checkpoint, projection, null, null, null, null, projection.FailureCode);

            await ProjectionRunManifestStore.WriteAsync(projectDirectory, projection, cancellationToken).ConfigureAwait(false);
            var projectionManifest = await ProjectionRunManifestStore.ReadAsync(projectDirectory, projection.Id, cancellationToken).ConfigureAwait(false);
            var binding = new ProjectionVerificationBinding(projectionManifest.RunId, projectionManifest.ConfigurationHash,
                projectionManifest.GraphHash, checkpoint.Id, checkpoint.Checkpoint.ManifestHash!,
                checkpoint.Checkpoint.SourceFingerprint!, projectionManifest.ProjectionFingerprint!,
                projectionManifest.ProjectionFingerprintVersion, projectionManifest.SourceArtifactCount,
                projectionManifest.TargetArtifactCount, projectionManifest.Status.ToString().ToLowerInvariant(),
                projectionManifest.ManifestHash, projectionManifest.JournalPath, projectionManifest.ConnectorVersions);
            var targetRuntimes = BuildTargetRuntimes(configuration, graph, projectionManifest.RunId);
            verification = await _verificationService.VerifyAsync(configuration, graph, binding, ruleSet,
                projectDirectory, temporaryDirectory, proofShiftVersion, targetRuntimes, recorder, cancellationToken).ConfigureAwait(false);
            using (var evidenceStage = recorder.StartStage(PerformanceStageKind.Reporting, "verification evidence persistence"))
            {
                evidenceReceipt = await evidenceStore.SaveAsync(verification.EvidenceGraph, cancellationToken).ConfigureAwait(false);
                evidenceStage.AddArtifacts(verification.EvidenceGraph.Records.Count);
            }
            var result = await _recoveryService.AssessAndRehearseAsync(configuration, graph, binding, verification,
                recoveryPolicy, targetRuntimes, projectDirectory, recorder, cancellationToken).ConfigureAwait(false);
            RecoveryArtifactReceipt recoveryReceipt;
            using (var recoveryPersistenceStage = recorder.StartStage(PerformanceStageKind.Reporting, "recovery artifact persistence"))
            {
                recoveryReceipt = await recoveryArtifactStore.SaveAsync(result, cancellationToken).ConfigureAwait(false);
                recoveryPersistenceStage.AddArtifacts(result.EvidenceGraph.Graph.Records.Count);
            }
            var outcome = result.Qualification.Status switch
            {
                DryRunQualificationStatus.Qualified => DryRunExecutionOutcome.Qualified,
                DryRunQualificationStatus.Cancelled => DryRunExecutionOutcome.Cancelled,
                DryRunQualificationStatus.Error => DryRunExecutionOutcome.Failed,
                _ => DryRunExecutionOutcome.NotQualified
            };
            return Complete(outcome, checkpoint, projection, verification, result, evidenceReceipt, recoveryReceipt, null);
        }
        catch (OperationCanceledException)
        {
            return Complete(DryRunExecutionOutcome.Cancelled, checkpoint, projection, verification,
                null, evidenceReceipt, null, "PSREADY_CANCELLED");
        }
        catch (SnapshotStoreException exception)
        {
            return Complete(DryRunExecutionOutcome.Failed, checkpoint, projection, verification,
                null, evidenceReceipt, null, exception.Code);
        }
        catch (ProjectionExecutionException exception)
        {
            return Complete(DryRunExecutionOutcome.NotQualified, checkpoint, projection, verification,
                null, evidenceReceipt, null, exception.Code);
        }
        catch (ProjectionManifestException exception)
        {
            return Complete(DryRunExecutionOutcome.Failed, checkpoint, projection, verification,
                null, evidenceReceipt, null, exception.Code);
        }
        catch (RecoveryException exception)
        {
            return Complete(DryRunExecutionOutcome.NotQualified, checkpoint, projection, verification,
                null, evidenceReceipt, null, exception.Code);
        }
        catch (VerificationRuleException exception)
        {
            return Complete(DryRunExecutionOutcome.Failed, checkpoint, projection, verification,
                null, evidenceReceipt, null, exception.Code);
        }
    }

    private VerificationTargetRuntime[] BuildTargetRuntimes(LoadedProjectConfiguration configuration,
        MigrationGraph graph, RunId projectionRunId) => graph.Nodes
        .Where(node => node.Type is MigrationNodeType.Target or MigrationNodeType.Archive)
        .OrderBy(node => node.Name, StringComparer.Ordinal)
        .Select(node =>
        {
            var system = configuration.Systems.Single(item => item.Id == node.SystemId);
            var endpoint = system.StorageEndpoints.Single(item => item.Id == node.EndpointId);
            var connector = _targetConnectors.Resolve(endpoint.Connector);
            return new VerificationTargetRuntime(node.Name, connector,
                new ShadowTargetContext(_contextFactory.Create(configuration, node), projectionRunId, system.Role));
        }).ToArray();
}
