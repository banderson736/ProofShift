using System.Globalization;
using ProofShift.Configuration;
using ProofShift.Connectors.Abstractions;
using ProofShift.Domain;
using ProofShift.Engine;
using ProofShift.Graph;

namespace ProofShift.Snapshots;

public sealed class SnapshotCaptureService
{
    private readonly ConnectorRegistry _connectors;
    private readonly IMaterializedSnapshotStore _store;
    private readonly RuntimeConnectorContextFactory _contextFactory;

    public SnapshotCaptureService(
        ConnectorRegistry connectors,
        IMaterializedSnapshotStore store,
        RuntimeConnectorContextFactory? contextFactory = null)
    {
        _connectors = connectors ?? throw new ArgumentNullException(nameof(connectors));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _contextFactory = contextFactory ?? new RuntimeConnectorContextFactory();
    }

    public async Task<SnapshotCaptureResult> CaptureAsync(
        LoadedProjectConfiguration configuration,
        MigrationGraph graph,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(graph);
        var checkpointId = new CheckpointId(Guid.NewGuid());
        var startedAt = DateTimeOffset.UtcNow;
        var sources = graph.Nodes.Where(node => node.Type == MigrationNodeType.Source)
            .OrderBy(node => node.Name, StringComparer.Ordinal).ToArray();
        if (sources.Length == 0)
        {
            throw new SnapshotStoreException(SnapshotIssueCodes.IncompleteSourceCoverage, "Migration graph has no source nodes to checkpoint.");
        }

        await using var session = await _store.CreateAsync(checkpointId, cancellationToken).ConfigureAwait(false);
        var endpoints = new List<CheckpointEndpoint>(sources.Length);
        long artifactCount = 0;
        long byteCount = 0;
        var status = CheckpointStatus.Creating;
        string? failureCode = null;
        try
        {
            foreach (var node in sources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var context = _contextFactory.Create(configuration, node);
                var connector = _connectors.Resolve(context.Connector);
                var inspection = await connector.InspectAsync(context, node.Selector, cancellationToken).ConfigureAwait(false);
                if (inspection.Status != SourceInspectionStatus.Valid)
                {
                    throw new SnapshotStoreException(SnapshotIssueCodes.CaptureFailed,
                        $"Required source node '{node.Name}' did not pass inspection.");
                }

                var captureStart = DateTimeOffset.UtcNow;
                await session.BeginSourceNodeAsync(node.Name, cancellationToken).ConfigureAwait(false);
                var fingerprint = new SnapshotFingerprints.MultisetAccumulator();
                long nodeArtifacts = 0;
                long nodeBytes = 0;
                var checkpointConnector = connector as ICheckpointSourceConnector;
                var read = checkpointConnector?.ReadForCheckpointAsync(context, node.Selector, new ReadOptions(), cancellationToken)
                    ?? connector.ReadAsync(context, node.Selector, new ReadOptions(), cancellationToken);
                var binaryResolver = connector as ISourceBinaryContentResolver;
                await foreach (var record in read.WithCancellation(cancellationToken).ConfigureAwait(false))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var recordFingerprint = SnapshotFingerprints.RecordFingerprint(record);
                    fingerprint.Add(recordFingerprint);
                    Func<string, CancellationToken, ValueTask<Stream>>? openBinary = null;
                    if (record.Values.Any(pair => pair.Value is BinaryReferenceValue))
                    {
                        if (binaryResolver is null)
                        {
                            throw new SnapshotStoreException(SnapshotIssueCodes.MissingReplayArtifact,
                                $"Source node '{node.Name}' has binary values without a stream resolver.");
                        }

                        openBinary = (field, token) =>
                        {
                            if (!record.Values.TryGetValue(field, out var value) || value is not BinaryReferenceValue binary)
                            {
                                throw new SnapshotStoreException(SnapshotIssueCodes.MissingReplayArtifact, "Captured binary field is unavailable.");
                            }

                            return binaryResolver.OpenBinaryReadAsync(context, node.Selector, record.Artifact, binary, token);
                        };
                    }

                    nodeBytes = checked(nodeBytes + await session.WriteRecordAsync(node.Name, record, openBinary, cancellationToken).ConfigureAwait(false));
                    nodeArtifacts++;
                }

                var segment = await session.CompleteSourceNodeAsync(node.Name, cancellationToken).ConfigureAwait(false);
                var captureComplete = DateTimeOffset.UtcNow;
                var consistency = checkpointConnector?.CheckpointConsistency ?? SourceConsistencyGuarantee.Observed;
                endpoints.Add(new CheckpointEndpoint(
                    node.Name,
                    node.SystemId,
                    node.EndpointId,
                    connector.Id,
                    connector.Version,
                    SnapshotFingerprints.SelectorHash(node.Selector),
                    node.Selector.IdentityFields,
                    captureStart,
                    captureComplete,
                    consistency,
                    CheckpointGuarantee.Materialized,
                    replayable: true,
                    nodeArtifacts,
                    nodeBytes,
                    fingerprint.Finish(node.Name),
                    segment.Reference,
                    segment.Sha256,
                    segment.Length));
                artifactCount = checked(artifactCount + nodeArtifacts);
                byteCount = checked(byteCount + nodeBytes);
            }

            var completedAt = DateTimeOffset.UtcNow;
            var sourceFingerprint = SnapshotFingerprints.AggregateSourceFingerprint(graph.GraphHash, endpoints);
            var minimumStart = endpoints.Min(endpoint => endpoint.CaptureStartedAt);
            var maximumCompletion = endpoints.Max(endpoint => endpoint.CaptureCompletedAt);
            var maximumSkew = maximumCompletion - minimumStart;
            var checkpoint = new SourceCheckpoint(
                checkpointId,
                CheckpointStatus.Complete,
                configuration.Root.Project?.Id ?? "unknown-project",
                configuration.ConfigurationHash,
                graph.GraphHash,
                startedAt,
                completedAt,
                maximumSkew,
                crossSystemAtomic: false,
                replayable: true,
                CheckpointGuarantee.Materialized,
                endpoints,
                artifactCount,
                byteCount,
                sourceFingerprint,
                manifestHash: new string('0', 64));
            var manifestHash = await session.FinalizeAsync(checkpoint, cancellationToken).ConfigureAwait(false);
            status = CheckpointStatus.Complete;
            var finalized = new SourceCheckpoint(
                checkpoint.Id,
                checkpoint.Status,
                checkpoint.ProjectId,
                checkpoint.ConfigurationHash,
                checkpoint.GraphHash,
                checkpoint.CaptureStartedAt,
                checkpoint.CaptureCompletedAt,
                checkpoint.MaximumEndpointSkew,
                checkpoint.CrossSystemAtomic,
                checkpoint.Replayable,
                checkpoint.Guarantee,
                checkpoint.Endpoints,
                checkpoint.ArtifactCount,
                checkpoint.ByteCount,
                checkpoint.SourceFingerprint,
                manifestHash);
            return new SnapshotCaptureResult(checkpointId, status, finalized, sources.Length, endpoints.Count,
                artifactCount, byteCount);
        }
        catch (OperationCanceledException)
        {
            status = CheckpointStatus.Cancelled;
            failureCode = "PSSNAP_CANCELLED";
            await session.MarkIncompleteAsync(status, failureCode, CancellationToken.None).ConfigureAwait(false);
            return new SnapshotCaptureResult(checkpointId, status, null, sources.Length, endpoints.Count, artifactCount, byteCount, failureCode);
        }
        catch (SnapshotStoreException exception)
        {
            status = CheckpointStatus.Failed;
            failureCode = exception.Code;
            await session.MarkIncompleteAsync(status, failureCode, CancellationToken.None).ConfigureAwait(false);
            return new SnapshotCaptureResult(checkpointId, status, null, sources.Length, endpoints.Count, artifactCount, byteCount, failureCode);
        }
        catch (Exception exception) when (exception is ConnectorReadException or ConnectorResolutionException or ConnectorConfigurationException)
        {
            status = CheckpointStatus.Failed;
            failureCode = exception is ConnectorReadException readFailure &&
                readFailure.Code == ConnectorIssueCodes.ArtifactChangedDuringCapture
                ? SnapshotIssueCodes.ArtifactChangedDuringCapture
                : exception is ConnectorReadException connectorFailure ? connectorFailure.Code : SnapshotIssueCodes.CaptureFailed;
            await session.MarkIncompleteAsync(status, failureCode, CancellationToken.None).ConfigureAwait(false);
            return new SnapshotCaptureResult(checkpointId, status, null, sources.Length, endpoints.Count, artifactCount, byteCount, failureCode);
        }
        catch
        {
            status = CheckpointStatus.Failed;
            failureCode = SnapshotIssueCodes.CaptureFailed;
            await session.MarkIncompleteAsync(status, failureCode, CancellationToken.None).ConfigureAwait(false);
            return new SnapshotCaptureResult(checkpointId, status, null, sources.Length, endpoints.Count, artifactCount, byteCount, failureCode);
        }
    }
}
