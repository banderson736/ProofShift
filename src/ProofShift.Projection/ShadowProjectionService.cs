using System.Globalization;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ProofShift.Configuration;
using ProofShift.Connectors.Abstractions;
using ProofShift.Domain;
using ProofShift.Engine;

namespace ProofShift.Projection;

public sealed class ShadowProjectionService
{
    private readonly ConnectorRegistry _sourceConnectors;
    private readonly ShadowTargetConnectorRegistry _targetConnectors;
    private readonly RuntimeConnectorContextFactory _contextFactory;
    private readonly ISourceArtifactStreamProvider? _sourceProvider;
    private readonly ProjectionExecutionOptions _executionOptions;

    public ShadowProjectionService(
        ConnectorRegistry sourceConnectors,
        ShadowTargetConnectorRegistry targetConnectors,
        RuntimeConnectorContextFactory? contextFactory = null,
        ISourceArtifactStreamProvider? sourceProvider = null,
        ProjectionExecutionOptions? executionOptions = null)
    {
        _sourceConnectors = sourceConnectors ?? throw new ArgumentNullException(nameof(sourceConnectors));
        _targetConnectors = targetConnectors ?? throw new ArgumentNullException(nameof(targetConnectors));
        _contextFactory = contextFactory ?? new RuntimeConnectorContextFactory();
        _sourceProvider = sourceProvider;
        _executionOptions = executionOptions ?? new ProjectionExecutionOptions();
    }

    public Task<ProjectionRun> ProjectAsync(
        LoadedProjectConfiguration configuration,
        MigrationGraph graph,
        string projectDirectory,
        CancellationToken cancellationToken = default) =>
        ProjectAsync(configuration, graph, projectDirectory, performanceRecorder: null, cancellationToken);

    public async Task<ProjectionRun> ProjectAsync(
        LoadedProjectConfiguration configuration,
        MigrationGraph graph,
        string projectDirectory,
        PerformanceRecorder? performanceRecorder,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);

        var runId = new RunId(Guid.NewGuid());
        var startedAt = DateTimeOffset.UtcNow;
        using var projectionStage = performanceRecorder?.StartStage(PerformanceStageKind.Projection, "projection");
        var runDirectoryName = runId.Value.ToString("N", CultureInfo.InvariantCulture);
        var journalRelativePath = Path.Combine(".proofshift", "projections", runDirectoryName, "journal.jsonl").Replace('\\', '/');
        var journalPath = Path.Combine(projectDirectory, ".proofshift", "projections", runDirectoryName, "journal.jsonl");
        var sourceCount = 0L;
        var targetCount = 0L;
        var failureCount = 0L;
        var status = ProjectionStatus.Running;
        string? failureCode = null;
        string? fingerprint = null;
        var versions = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var destinationContexts = new List<(MigrationNode Node, IShadowTargetConnector Connector, ShadowTargetContext Context)>();
        var writeSessions = new Dictionary<MigrationNodeId, IShadowTargetWriteSession>();
        var journal = new ProjectionJournalWriter(journalPath, runId);

        try
        {
            ValidateRoles(configuration, graph);
            ValidateExecutionPlan(graph);
            ValidateSourceDestinationIsolation(configuration, graph);
            var sourceNodes = graph.Nodes.Where(node => node.Type == MigrationNodeType.Source)
                .OrderBy(node => node.Name, StringComparer.Ordinal).ToArray();
            var targetNodes = graph.Nodes.Where(node => node.Type is MigrationNodeType.Target or MigrationNodeType.Archive)
                .OrderBy(node => node.Name, StringComparer.Ordinal).ToArray();
            if (sourceNodes.Length == 0 || (targetNodes.Length == 0 && graph.Edges.Any(edge => edge.Operation.Type != MigrationOperationType.Exclude)))
            {
                throw new ProjectionExecutionException("PSPROJ_GRAPH", "Projection requires at least one source and one shadow destination node.");
            }

            if (_sourceProvider is not null)
            {
                var requiredSourceKeys = sourceNodes.Select(node => node.Name).Order(StringComparer.Ordinal).ToArray();
                var checkpointSourceKeys = _sourceProvider.CheckpointSourceNodeKeys?.Order(StringComparer.Ordinal).ToArray() ?? [];
                if (_sourceProvider.CheckpointConfigurationHash != configuration.ConfigurationHash ||
                    _sourceProvider.CheckpointGraphHash != graph.GraphHash ||
                    !requiredSourceKeys.SequenceEqual(checkpointSourceKeys, StringComparer.Ordinal))
                {
                    throw new ProjectionExecutionException("PSPROJ_CHECKPOINT_MISMATCH",
                        "Checkpoint configuration, compiled graph, or exact source-node coverage does not match this projection request.");
                }
            }

            var sourceContexts = new Dictionary<MigrationNodeId, (ISourceConnector? Connector, ConnectorContext Context)>();
            foreach (var source in sourceNodes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var context = _contextFactory.Create(configuration, source);
                var connector = _sourceProvider is null ? _sourceConnectors.Resolve(context.Connector) : null;
                string connectorVersion;
                if (_sourceProvider is null)
                {
                    using var inspectionStage = performanceRecorder?.StartStage(PerformanceStageKind.Projection,
                        "projection source inspection", connector!.Id.Value, source.Name);
                    var inspection = await connector!.InspectAsync(context, source.Selector, cancellationToken).ConfigureAwait(false);
                    inspectionStage?.AddArtifacts(inspection.EstimatedRecords ?? 0);
                    if (inspection.Bytes is { } inspectedBytes) inspectionStage?.AddBytes(inspectedBytes);
                    if (inspection.Status != SourceInspectionStatus.Valid)
                    {
                        throw new ProjectionExecutionException("PSPROJ_SOURCE_INSPECTION", $"Source inspection failed for graph node '{source.Name}'.");
                    }

                    connectorVersion = connector.Version;
                }
                else
                {
                    connectorVersion = await _sourceProvider.ValidateAsync(source.Name, context, source.Selector, cancellationToken).ConfigureAwait(false);
                }

                sourceContexts.Add(source.Id, (connector, context));
                versions[$"source:{source.Name}"] = connectorVersion;
            }

            foreach (var target in targetNodes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var context = _contextFactory.Create(configuration, target);
                var system = FindSystem(configuration, target.SystemId);
                if (system.Role != SystemRole.ShadowTarget)
                {
                    throw new ProjectionExecutionException("PSPROJ_UNSAFE_TARGET", "Projection destinations must be explicitly classified as shadow-target.");
                }

                var connector = _targetConnectors.Resolve(context.Connector);
                var shadowContext = new ShadowTargetContext(context, runId, system.Role);
                destinationContexts.Add((target, connector, shadowContext));
                versions[$"target:{target.Name}"] = connector.Version;
            }

            foreach (var destination in destinationContexts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var prepareStage = performanceRecorder?.StartStage(PerformanceStageKind.Projection,
                    "shadow target prepare", destination.Connector.Id.Value, destination.Node.Name);
                await destination.Connector.PrepareAsync(destination.Context, destination.Node.Selector, cancellationToken).ConfigureAwait(false);
            }

            var batchTargetIds = graph.Edges
                .Where(edge => edge.Operation.Type is not (MigrationOperationType.Exclude or MigrationOperationType.Relationship) && edge.Targets.Count == 1)
                .SelectMany(edge => edge.Targets)
                .ToHashSet();
            foreach (var destination in destinationContexts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (batchTargetIds.Contains(destination.Node.Id) && destination.Connector is IShadowTargetWriteSessionProvider provider)
                {
                    writeSessions.Add(destination.Node.Id, await provider.OpenWriteSessionAsync(
                        destination.Context, destination.Node.Selector, _executionOptions.WriteBatchSize, cancellationToken).ConfigureAwait(false));
                }
            }

            var destinationsById = destinationContexts.ToDictionary(item => item.Node.Id);
            foreach (var edge in OrderEdges(graph))
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var edgeStage = performanceRecorder?.StartStage(PerformanceStageKind.Projection,
                    "projection edge", edge.Name);
                var edgeSourceCount = 0L;
                var edgeTargetCount = 0L;
                var targetWriteTicks = 0L;
                var journalTicks = 0L;
                var journalEntryCount = 0L;
                var edgeTransformations = GetTransformations(edge);
                if (edge.Operation.Type == MigrationOperationType.Relationship)
                {
                    var journalStarted = Stopwatch.GetTimestamp();
                    await journal.AppendAsync(new ProjectionJournalEntry(
                        runId, null, null, [], edge.Id, edge.Name, edge.Version,
                        edgeTransformations, ProjectionJournalResult.MetadataOnly, edge.Recovery), cancellationToken).ConfigureAwait(false);
                    journalTicks += Stopwatch.GetTimestamp() - journalStarted;
                    journalEntryCount++;
                    edgeStage?.AddMeasurement("journalMilliseconds", ToMilliseconds(journalTicks), "ms");
                    edgeStage?.AddMeasurement("journalEntries", journalEntryCount, "entries");
                    continue;
                }

                if (edge.Operation.Type == MigrationOperationType.Merge || edge.Sources.Count != 1)
                {
                    throw new ProjectionExecutionException("PSPROJ_MERGE_DEFERRED", "Merge execution is deferred because PS-0.5 does not define deterministic grouping/join semantics.");
                }

                var sourceNode = graph.Nodes.Single(node => node.Id == edge.Sources[0]);
                if (sourceNode.Type != MigrationNodeType.Source || !sourceContexts.TryGetValue(sourceNode.Id, out var sourceRuntime))
                {
                    throw new ProjectionExecutionException("PSPROJ_PATH_UNSUPPORTED", "PS-0.5 executes direct source-to-shadow paths only; chained target inputs are not supported.");
                }

                var targets = edge.Targets.Select(targetId => graph.Nodes.Single(node => node.Id == targetId))
                    .OrderBy(node => node.Name, StringComparer.Ordinal).ToArray();
                if (edge.Operation.Type != MigrationOperationType.Exclude && targets.Length == 0)
                {
                    throw new ProjectionExecutionException("PSPROJ_GRAPH", "Executable projection edge has no target node.");
                }

                IShadowTargetWriteSession? edgeWriteSession = null;
                if (targets.Length == 1)
                {
                    writeSessions.TryGetValue(targets[0].Id, out edgeWriteSession);
                }

                var bufferedWrites = new List<BufferedProjectionWrite>(edgeWriteSession?.BatchSize ?? 0);
                async Task FlushBufferedWritesAsync()
                {
                    if (bufferedWrites.Count == 0)
                    {
                        return;
                    }

                    var batch = bufferedWrites.ToArray();
                    var pendingStarted = Stopwatch.GetTimestamp();
                    await journal.AppendBatchAsync(batch.Select(write => write.Pending).ToArray(), cancellationToken).ConfigureAwait(false);
                    journalTicks += Stopwatch.GetTimestamp() - pendingStarted;
                    journalEntryCount += batch.Length;
                    var writeStarted = Stopwatch.GetTimestamp();
                    try
                    {
                        await edgeWriteSession!.WriteBatchAsync(batch.Select(write => write.Request).ToArray(), cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        targetWriteTicks += Stopwatch.GetTimestamp() - writeStarted;
                        var failedStarted = Stopwatch.GetTimestamp();
                        await journal.AppendBatchAsync(batch.Select(write => CreateBatchFailureEntry(write, runId, "PSPROJ_BATCH_CANCELLED")).ToArray(), CancellationToken.None).ConfigureAwait(false);
                        journalTicks += Stopwatch.GetTimestamp() - failedStarted;
                        journalEntryCount += batch.Length;
                        failureCount += batch.Length;
                        failureCode = "PSPROJ_BATCH_CANCELLED";
                        throw;
                    }
                    catch (Exception exception)
                    {
                        var code = exception is ProjectionConnectorException connector
                            ? connector.Code
                            : "PSPROJ_BATCH_FAILED";
                        targetWriteTicks += Stopwatch.GetTimestamp() - writeStarted;
                        var failedStarted = Stopwatch.GetTimestamp();
                        await journal.AppendBatchAsync(batch.Select(write => CreateBatchFailureEntry(write, runId, code)).ToArray(), CancellationToken.None).ConfigureAwait(false);
                        journalTicks += Stopwatch.GetTimestamp() - failedStarted;
                        journalEntryCount += batch.Length;
                        failureCount += batch.Length;
                        failureCode = code;
                        throw new ProjectionExecutionException(code, "Projection failed while committing a target write batch.");
                    }

                    targetWriteTicks += Stopwatch.GetTimestamp() - writeStarted;
                    targetCount += batch.Length;
                    edgeTargetCount += batch.Length;
                    var producedStarted = Stopwatch.GetTimestamp();
                    await journal.AppendBatchAsync(batch.Select(write => CreateProducedEntry(write, runId)).ToArray(), cancellationToken).ConfigureAwait(false);
                    journalTicks += Stopwatch.GetTimestamp() - producedStarted;
                    journalEntryCount += batch.Length;
                    bufferedWrites.Clear();
                }

                var sourceRecords = _sourceProvider is null
                    ? sourceRuntime.Connector!.ReadAsync(sourceRuntime.Context, sourceNode.Selector, new ReadOptions(), cancellationToken)
                    : _sourceProvider.ReadAsync(sourceNode.Name, sourceRuntime.Context, sourceNode.Selector, cancellationToken);
                await foreach (var sourceRecord in sourceRecords
                    .WithCancellation(cancellationToken).ConfigureAwait(false))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    sourceCount++;
                    edgeSourceCount++;
                    if (edge.Operation.Type == MigrationOperationType.Exclude)
                    {
                        var journalStarted = Stopwatch.GetTimestamp();
                        await journal.AppendAsync(new ProjectionJournalEntry(
                            runId, null, null, [sourceRecord.Artifact], edge.Id, edge.Name, edge.Version,
                            edgeTransformations, ProjectionJournalResult.Excluded, edge.Recovery),
                            cancellationToken).ConfigureAwait(false);
                        journalTicks += Stopwatch.GetTimestamp() - journalStarted;
                        journalEntryCount++;
                        continue;
                    }

                    foreach (var target in targets)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var destination = destinationsById[target.Id];
                        RecordEnvelope? projected = null;
                        try
                        {
                            projected = TransformationRuntime.Transform(sourceRecord, edge, target);
                            Func<string, CancellationToken, ValueTask<Stream>>? openBinary = null;
                            if (projected.Values.Any(pair => pair.Value is BinaryReferenceValue))
                            {
                                var binaryResolver = sourceRuntime.Connector as ISourceBinaryContentResolver;
                                if (_sourceProvider is null && binaryResolver is null)
                                {
                                    throw new ProjectionExecutionException("PSPROJ_BINARY_RESOLVER", "Source connector cannot reopen binary content for streaming projection.");
                                }

                                openBinary = (field, token) =>
                                {
                                    if (!projected.Values.TryGetValue(field, out var node) || node is not BinaryReferenceValue binary)
                                    {
                                        throw new ProjectionExecutionException("PSPROJ_BINARY_REFERENCE", "Projected binary field has no source content reference.");
                                    }

                                    return _sourceProvider is not null
                                        ? _sourceProvider.OpenBinaryReadAsync(sourceNode.Name, sourceRuntime.Context, sourceNode.Selector,
                                            sourceRecord.Artifact, binary, token)
                                        : binaryResolver!.OpenBinaryReadAsync(sourceRuntime.Context, sourceNode.Selector,
                                            sourceRecord.Artifact, binary, token);
                                };
                            }

                            var pendingEntry = new ProjectionJournalEntry(
                                runId, projected.Artifact, target.Name, [sourceRecord.Artifact], edge.Id, edge.Name, edge.Version,
                                edgeTransformations, ProjectionJournalResult.Pending, edge.Recovery);
                            var writeRequest = new ShadowWriteRequest(destination.Context, target.Selector, projected, target.Name, openBinary);
                            if (edgeWriteSession is not null)
                            {
                                bufferedWrites.Add(new BufferedProjectionWrite(writeRequest, sourceRecord.Artifact,
                                    edge, edgeTransformations, pendingEntry));
                            }
                            else
                            {
                                var pendingJournalStarted = Stopwatch.GetTimestamp();
                                await journal.AppendAsync(pendingEntry, cancellationToken).ConfigureAwait(false);
                                journalTicks += Stopwatch.GetTimestamp() - pendingJournalStarted;
                                journalEntryCount++;
                                var targetWriteStarted = Stopwatch.GetTimestamp();
                                await destination.Connector.WriteAsync(writeRequest, cancellationToken).ConfigureAwait(false);
                                targetWriteTicks += Stopwatch.GetTimestamp() - targetWriteStarted;
                                targetCount++;
                                edgeTargetCount++;
                                var producedJournalStarted = Stopwatch.GetTimestamp();
                                await journal.AppendAsync(new ProjectionJournalEntry(
                                    runId, projected.Artifact, target.Name, [sourceRecord.Artifact], edge.Id, edge.Name, edge.Version,
                                    edgeTransformations, ProjectionJournalResult.Produced, edge.Recovery), cancellationToken).ConfigureAwait(false);
                                journalTicks += Stopwatch.GetTimestamp() - producedJournalStarted;
                                journalEntryCount++;
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception exception)
                        {
                            failureCount++;
                            var code = exception is ProjectionTransformationException transformation
                                ? transformation.Code
                                : exception is ProjectionExecutionException execution
                                    ? execution.Code
                                    : exception is ProjectionConnectorException connector
                                        ? connector.Code
                                        : "PSPROJ_ARTIFACT_FAILED";
                            failureCode = code;
                            var failedJournalStarted = Stopwatch.GetTimestamp();
                            await journal.AppendAsync(new ProjectionJournalEntry(
                                runId, projected?.Artifact, target.Name, [sourceRecord.Artifact], edge.Id, edge.Name, edge.Version,
                                edgeTransformations, ProjectionJournalResult.Failed, edge.Recovery, code), CancellationToken.None).ConfigureAwait(false);
                            journalTicks += Stopwatch.GetTimestamp() - failedJournalStarted;
                            journalEntryCount++;
                            throw new ProjectionExecutionException(code, "Projection failed while materializing a source artifact.");
                        }

                        if (edgeWriteSession is not null && bufferedWrites.Count >= edgeWriteSession.BatchSize)
                        {
                            await FlushBufferedWritesAsync().ConfigureAwait(false);
                        }
                    }
                }

                await FlushBufferedWritesAsync().ConfigureAwait(false);

                edgeStage?.AddArtifacts(edgeTargetCount);
                edgeStage?.AddMeasurement("sourceArtifacts", edgeSourceCount, "artifacts");
                edgeStage?.AddMeasurement("targetArtifacts", edgeTargetCount, "artifacts");
                edgeStage?.AddMeasurement("shadowTargetWriteMilliseconds", ToMilliseconds(targetWriteTicks), "ms");
                edgeStage?.AddMeasurement("journalMilliseconds", ToMilliseconds(journalTicks), "ms");
                edgeStage?.AddMeasurement("journalEntries", journalEntryCount, "entries");
            }

            foreach (var destination in destinationContexts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var completeStage = performanceRecorder?.StartStage(PerformanceStageKind.Projection,
                    "shadow target complete", destination.Connector.Id.Value, destination.Node.Name);
                await destination.Connector.CompleteAsync(destination.Context, cancellationToken).ConfigureAwait(false);
            }

            using var readBackStage = performanceRecorder?.StartStage(PerformanceStageKind.Projection, "target read-back");
            var readBack = await MaterializedTargetFingerprint.ComputeAsync(
                destinationContexts.Select(destination => new MaterializedTargetReadback(
                    destination.Node.Name,
                    destination.Node.Selector,
                    destination.Connector,
                    destination.Context)),
                graph.GraphHash,
                performanceRecorder, cancellationToken).ConfigureAwait(false);
            readBackStage?.AddArtifacts(readBack.RecordCount);
            if (readBack.RecordCount != targetCount)
            {
                throw new ProjectionExecutionException("PSPROJ_READBACK_COUNT", "Materialized shadow output count did not match successful writes.");
            }

            targetCount = readBack.RecordCount;
            fingerprint = readBack.Fingerprint;
            status = ProjectionStatus.Succeeded;
        }
        catch (OperationCanceledException)
        {
            status = ProjectionStatus.Cancelled;
        }
        catch (ProjectionExecutionException exception)
        {
            status = ProjectionStatus.Failed;
            failureCode = exception.Code;
            failureCount = Math.Max(1, failureCount);
        }
        catch
        {
            status = ProjectionStatus.Failed;
            failureCode = "PSPROJ_FAILED";
            failureCount = Math.Max(1, failureCount);
        }
        finally
        {
            foreach (var session in writeSessions.Values)
            {
                try
                {
                    await session.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    status = ProjectionStatus.Failed;
                    failureCode = "PSPROJ_WRITE_SESSION_DISPOSE";
                    failureCount = Math.Max(1, failureCount);
                    fingerprint = null;
                }
            }
            try
            {
                await journal.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                status = ProjectionStatus.Failed;
                failureCode = "PSPROJ_JOURNAL_FINALIZE";
                failureCount = Math.Max(1, failureCount);
                fingerprint = null;
            }
        }

        var completedAt = DateTimeOffset.UtcNow;
        var connectorVersions = versions;
        var shadowDestinations = destinationContexts.Select(item => item.Connector.Id.Value switch
        {
            "postgres" => $"{item.Node.Name}:postgres-schema:proofshift_shadow_{runId.Value:N}",
            "files" => $"{item.Node.Name}:filesystem-run:{runId.Value:N}/{item.Node.Name}",
            _ => $"{item.Node.Name}:{item.Connector.Id.Value}:run:{runId.Value:N}"
        });
        return new ProjectionRun(
            runId,
            status,
            configuration.Root.Project?.Id ?? "unknown-project",
            configuration.ConfigurationHash,
            graph.GraphHash,
            startedAt,
            completedAt,
            sourceCount,
            targetCount,
            failureCount,
            failureCode,
            fingerprint,
            connectorVersions,
            shadowDestinations,
            journalRelativePath,
            _sourceProvider?.CheckpointId,
            _sourceProvider?.CheckpointSourceFingerprint,
            _sourceProvider?.CheckpointManifestHash);
    }

    private static void ValidateRoles(LoadedProjectConfiguration configuration, MigrationGraph graph)
    {
        foreach (var node in graph.Nodes)
        {
            var system = FindSystem(configuration, node.SystemId);
            if (node.Type == MigrationNodeType.Source && system.Role != SystemRole.Source)
            {
                throw new ProjectionExecutionException("PSPROJ_UNSAFE_SOURCE", "Source graph nodes must reference systems classified as source.");
            }

            if (node.Type is MigrationNodeType.Target or MigrationNodeType.Archive && system.Role != SystemRole.ShadowTarget)
            {
                throw new ProjectionExecutionException("PSPROJ_UNSAFE_TARGET", "Target and archive graph nodes must reference systems explicitly classified as shadow-target.");
            }

            if (node.Type is MigrationNodeType.Derived or MigrationNodeType.Aggregate)
            {
                throw new ProjectionExecutionException("PSPROJ_NODE_UNSUPPORTED", "PS-0.5 does not execute derived or aggregate graph nodes.");
            }
        }
    }

    private static void ValidateExecutionPlan(MigrationGraph graph)
    {
        if (graph.Validate().Count > 0)
        {
            throw new ProjectionExecutionException("PSPROJ_GRAPH_INVALID", "Projection requires a valid compiled migration graph.");
        }

        foreach (var edge in graph.Edges)
        {
            if (edge.Operation.Type == MigrationOperationType.Relationship)
            {
                continue;
            }

            if (edge.Operation.Type == MigrationOperationType.Merge || edge.Sources.Count != 1)
            {
                throw new ProjectionExecutionException("PSPROJ_MERGE_DEFERRED", "Merge execution is deferred because PS-0.5 does not define deterministic grouping/join semantics.");
            }

            if (edge.Operation.Type is not (MigrationOperationType.Copy or MigrationOperationType.Map or MigrationOperationType.Transform or
                MigrationOperationType.Split or MigrationOperationType.Archive or MigrationOperationType.Exclude))
            {
                throw new ProjectionExecutionException("PSPROJ_OPERATION_UNSUPPORTED", "Migration operation is not executable in PS-0.5.");
            }

            var source = graph.Nodes.Single(node => node.Id == edge.Sources[0]);
            if (source.Type != MigrationNodeType.Source)
            {
                throw new ProjectionExecutionException("PSPROJ_PATH_UNSUPPORTED", "PS-0.5 executes direct source-to-shadow paths only; chained target inputs are not supported.");
            }

            if (edge.Operation.Type != MigrationOperationType.Exclude && edge.Targets.Count == 0)
            {
                throw new ProjectionExecutionException("PSPROJ_GRAPH", "Executable projection edge has no target node.");
            }

            foreach (var targetId in edge.Targets)
            {
                var target = graph.Nodes.Single(node => node.Id == targetId);
                if (target.Type is not (MigrationNodeType.Target or MigrationNodeType.Archive))
                {
                    throw new ProjectionExecutionException("PSPROJ_PATH_UNSUPPORTED", "Projection edges must terminate at target or archive nodes.");
                }
            }

            var steps = edge.Operation.Steps.Concat(edge.Operation.Fields.SelectMany(field => field.Pipeline));
            foreach (var step in steps)
            {
                if (step.Version != "1" || step.Type is not (TransformationStepType.Copy or TransformationStepType.Rename or
                    TransformationStepType.Trim or TransformationStepType.NormalizeString or TransformationStepType.NormalizeDate or
                    TransformationStepType.CodeMap or TransformationStepType.Concatenate or TransformationStepType.Split))
                {
                    throw new ProjectionExecutionException("PSPROJ_UNSUPPORTED_TRANSFORM", "Transformation type or version is not executable in PS-0.5.");
                }
            }
        }
    }

    private void ValidateSourceDestinationIsolation(LoadedProjectConfiguration configuration, MigrationGraph graph)
    {
        var sourceNodes = graph.Nodes.Where(node => node.Type == MigrationNodeType.Source).ToArray();
        var destinationNodes = graph.Nodes.Where(node => node.Type is MigrationNodeType.Target or MigrationNodeType.Archive).ToArray();
        foreach (var source in sourceNodes)
        {
            var sourceContext = _contextFactory.Create(configuration, source);
            foreach (var destination in destinationNodes)
            {
                var destinationContext = _contextFactory.Create(configuration, destination);
                if (sourceContext.Connector == new ConnectorId("postgres") && destinationContext.Connector == sourceContext.Connector &&
                    SameRuntimeSetting(sourceContext, destinationContext, "connection"))
                {
                    throw new ProjectionExecutionException("PSPROJ_SOURCE_SHADOW_OVERLAP",
                        "A PostgreSQL source and shadow destination cannot use the same connection string.");
                }

                var sourceIsFilesystem = sourceContext.Connector.Value is "files" or "csv";
                if (destinationContext.Connector != new ConnectorId("files") || !sourceIsFilesystem)
                {
                    continue;
                }

                var sourceRoot = ConnectorPathUtilities.ResolveRoot(sourceContext);
                var destinationRoot = ConnectorPathUtilities.ResolveRoot(destinationContext);
                if (ConnectorPathUtilities.IsContained(sourceRoot, destinationRoot) ||
                    ConnectorPathUtilities.IsContained(destinationRoot, sourceRoot))
                {
                    throw new ProjectionExecutionException("PSPROJ_SOURCE_SHADOW_OVERLAP",
                        "Filesystem shadow root must not be equal to, contain, or be contained by a live source root.");
                }
            }
        }
    }

    private static bool SameRuntimeSetting(ConnectorContext source, ConnectorContext destination, string settingName)
    {
        if (!source.Configuration.TryGet(settingName, out var sourceSetting) ||
            !destination.Configuration.TryGet(settingName, out var destinationSetting))
        {
            return false;
        }

        var sourceValue = sourceSetting.UseValue(value => value.Trim());
        var destinationValue = destinationSetting.UseValue(value => value.Trim());
        return string.Equals(sourceValue, destinationValue, StringComparison.Ordinal);
    }

    private static SystemDefinition FindSystem(LoadedProjectConfiguration configuration, SystemId systemId) =>
        configuration.Systems.FirstOrDefault(system => system.Id == systemId)
        ?? throw new ProjectionExecutionException("PSPROJ_SYSTEM_MISSING", "Projection graph references an unavailable system.");

    private static IEnumerable<MigrationEdge> OrderEdges(MigrationGraph graph)
    {
        var edges = graph.Edges.OrderBy(edge => edge.Name, StringComparer.Ordinal).ToArray();
        var nodeRanks = graph.Nodes.ToDictionary(node => node.Id, node => node.Name, EqualityComparer<MigrationNodeId>.Default);
        return edges.OrderBy(edge => edge.Sources.Select(id => nodeRanks[id]).Order(StringComparer.Ordinal).FirstOrDefault() ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(edge => edge.Name, StringComparer.Ordinal);
    }

    private static ProjectionTransformation[] GetTransformations(MigrationEdge edge) =>
        edge.Operation.Steps.Select(step => new ProjectionTransformation(FormatStepType(step.Type), step.Version))
            .Concat(edge.Operation.Fields.SelectMany(field => field.Pipeline.Select(step =>
                new ProjectionTransformation(FormatStepType(step.Type), step.Version, field.Source, field.Target))))
            .ToArray();

    private static string FormatStepType(TransformationStepType type) => type switch
    {
        TransformationStepType.NormalizeString => "normalize-string",
        TransformationStepType.NormalizeDate => "normalize-date",
        TransformationStepType.CodeMap => "code-map",
        _ => type.ToString().ToLowerInvariant()
    };

    private static ProjectionJournalEntry CreateProducedEntry(BufferedProjectionWrite write, RunId runId) =>
        new(runId, write.Request.Record.Artifact, write.Request.NodeKey, [write.SourceArtifact], write.Edge.Id,
            write.Edge.Name, write.Edge.Version, write.Transformations, ProjectionJournalResult.Produced, write.Edge.Recovery);

    private static ProjectionJournalEntry CreateBatchFailureEntry(BufferedProjectionWrite write, RunId runId, string code) =>
        new(runId, write.Request.Record.Artifact, write.Request.NodeKey, [write.SourceArtifact], write.Edge.Id,
            write.Edge.Name, write.Edge.Version, write.Transformations, ProjectionJournalResult.Failed, write.Edge.Recovery, code);

    private sealed record BufferedProjectionWrite(
        ShadowWriteRequest Request,
        ArtifactReference SourceArtifact,
        MigrationEdge Edge,
        ProjectionTransformation[] Transformations,
        ProjectionJournalEntry Pending);

    private static double ToMilliseconds(long stopwatchTicks) =>
        stopwatchTicks * 1_000d / Stopwatch.Frequency;

    private sealed class ProjectionJournalWriter : IAsyncDisposable
    {
        private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        private readonly StreamWriter _writer;
        private readonly RunId _runId;

        public ProjectionJournalWriter(string path, RunId runId)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            _writer = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan), new UTF8Encoding(false));
            _runId = runId;
        }

        public async Task AppendAsync(ProjectionJournalEntry entry, CancellationToken cancellationToken)
        {
            await WriteEntryAsync(entry, cancellationToken).ConfigureAwait(false);
            await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task AppendBatchAsync(ProjectionJournalEntry[] entries, CancellationToken cancellationToken)
        {
            if (entries.Length == 0) return;
            foreach (var entry in entries)
                await WriteEntryAsync(entry, cancellationToken).ConfigureAwait(false);
            await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task WriteEntryAsync(ProjectionJournalEntry entry, CancellationToken cancellationToken)
        {
            var journalRecord = new JournalRecord(
                _runId.Value.ToString("D", CultureInfo.InvariantCulture),
                entry.Target is null ? null : new ArtifactRecord(entry.Target.Id.Value, entry.Target.SystemId.Value, entry.Target.EndpointId.Value,
                    entry.Target.ArtifactType, entry.Target.Identity),
                entry.TargetNode,
                entry.Sources.Select(source => new ArtifactRecord(source.Id.Value, source.SystemId.Value, source.EndpointId.Value,
                    source.ArtifactType, source.Identity)).ToArray(),
                entry.EdgeId.Value.ToString("D", CultureInfo.InvariantCulture),
                entry.Edge,
                entry.EdgeVersion,
                entry.Transformations,
                entry.RecoveryMode?.ToString().ToLowerInvariant(),
                entry.RecoveryRequiresSnapshot,
                entry.RecoveryStrategy,
                entry.Result.ToString().ToLowerInvariant(),
                entry.FailureCode);
            await _writer.WriteLineAsync(JsonSerializer.Serialize(journalRecord, JsonOptions).AsMemory(), cancellationToken).ConfigureAwait(false);
        }

        public ValueTask DisposeAsync() => _writer.DisposeAsync();

        private sealed record ArtifactRecord(string Id, string System, string Endpoint, string Type, string Identity);
        private sealed record JournalRecord(
            string RunId,
            ArtifactRecord? Target,
            string? TargetNode,
            IReadOnlyCollection<ArtifactRecord> Sources,
            string EdgeId,
            string Edge,
            string EdgeVersion,
            IReadOnlyCollection<ProjectionTransformation> Transformations,
            string? RecoveryMode,
            bool RecoveryRequiresSnapshot,
            string? RecoveryStrategy,
            string Result,
            string? FailureCode);
    }
}

public sealed class ProjectionExecutionException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}