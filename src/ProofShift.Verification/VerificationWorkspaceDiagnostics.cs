using System.Text.Json;
using Microsoft.Data.Sqlite;
using ProofShift.Domain;
using ProofShift.Engine;

namespace ProofShift.Verification;

internal sealed class VerificationWorkspaceDiagnostics(PerformanceRecorder? performanceRecorder)
{
    private readonly Dictionary<string, string[]> _queryPlans = new(StringComparer.Ordinal);

    internal async Task CaptureQueryPlanAsync(SqliteConnection connection, string name, SqliteCommand query,
        Action<SqliteCommand> routeReadCommand,
        Func<SqliteCommand, CancellationToken, Task<SqliteDataReader>> executeReadAsync,
        CancellationToken cancellationToken)
    {
        routeReadCommand(query);
        if (performanceRecorder is null || _queryPlans.ContainsKey(name)) return;
        await using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + query.CommandText;
        foreach (SqliteParameter parameter in query.Parameters)
            command.Parameters.AddWithValue(parameter.ParameterName, parameter.Value);
        var details = new List<string>();
        await using var reader = await executeReadAsync(command, cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) details.Add(reader.GetString(3));
        _queryPlans.Add(name, details.ToArray());
    }

    internal async Task WriteAsync(SqliteConnection connection, VerificationWorkspaceDiagnosticsSnapshot snapshot,
        Func<SqliteCommand, CancellationToken, Task<SqliteDataReader>> executeReadAsync,
        Func<SqliteCommand, CancellationToken, Task<object?>> executeScalarReadAsync,
        Action observePartitionHighWater, CancellationToken cancellationToken)
    {
        using var stage = performanceRecorder?.StartStage(PerformanceStageKind.Verification, "verification workspace diagnostics");
        observePartitionHighWater();
        var expectedRowsByPartition = new long[snapshot.PartitionCount];
        await using (var expectedCounts = connection.CreateCommand())
        {
            var expectedSchema = snapshot.HasSharedExpected ? "expected." : string.Empty;
            expectedCounts.CommandText = $"SELECT partition_bucket,COUNT(*) FROM {expectedSchema}expected_targets GROUP BY partition_bucket";
            await using var reader = await executeReadAsync(expectedCounts, cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                expectedRowsByPartition[reader.GetInt32(0) % snapshot.PartitionCount] += reader.GetInt64(1);
        }
        AddPartitionDistribution(stage, "actualArtifacts", snapshot.PartitionActualRows);
        AddPartitionDistribution(stage, "expectedTargetRows", expectedRowsByPartition);
        AddPartitionDistribution(stage, "journalEntries", snapshot.PartitionJournalRows);
        for (var index = 0; index < snapshot.PartitionCount; index++)
        {
            var schema = snapshot.PartitionCount == 1 ? "main" : $"partition{index}";
            await using var indexCommand = connection.CreateCommand();
            indexCommand.CommandText = $"SELECT COUNT(*) FROM {schema}.sqlite_master WHERE type='index'";
            var indexCount = (long)(await indexCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
            long largestIndexBytes = 0;
            var indexSizeAvailable = false;
            try
            {
                indexCommand.CommandText = $"SELECT COALESCE(MAX(bytes),0) FROM (SELECT name,SUM(pgsize) AS bytes FROM dbstat($schema) WHERE name IN (SELECT name FROM {schema}.sqlite_master WHERE type='index') GROUP BY name)";
                indexCommand.Parameters.AddWithValue("$schema", schema);
                largestIndexBytes = (long)(await indexCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
                indexSizeAvailable = true;
            }
            catch (SqliteException) { }
            var writer = snapshot.PartitionWriters[index];
            performanceRecorder?.RecordMeasuredStage(PerformanceStageKind.Verification, $"verification scratch partition {index:D3}", 0,
                artifactCount: snapshot.PartitionActualRows[index], measurements:
                [
                    new PerformanceMeasurement("partitionCount", snapshot.PartitionCount, "partitions"),
                    new PerformanceMeasurement("actualRows", snapshot.PartitionActualRows[index], "rows"),
                    new PerformanceMeasurement("journalRows", snapshot.PartitionJournalRows[index], "rows"),
                    new PerformanceMeasurement("databaseHighWaterBytes", snapshot.PartitionDatabaseHighWater[index], "bytes"),
                    new PerformanceMeasurement("walHighWaterBytes", snapshot.PartitionWalHighWater[index], "bytes"),
                    new PerformanceMeasurement("indexes", indexCount, "indexes"),
                    new PerformanceMeasurement("largestIndexBytes", largestIndexBytes, "bytes"),
                    new PerformanceMeasurement("indexSizeAvailable", indexSizeAvailable ? 1 : 0, "available"),
                    new PerformanceMeasurement("dmlExecutions", writer?.DmlOperations ?? snapshot.ReferenceDmlOperations, "commands"),
                    new PerformanceMeasurement("dmlElapsedMicroseconds", writer?.DmlMicroseconds ?? snapshot.ReferenceDmlMicroseconds, "microseconds"),
                    new PerformanceMeasurement("committedTransactions", writer?.CommittedTransactions ?? snapshot.CommittedTransactions, "transactions"),
                    new PerformanceMeasurement("commitElapsedMicroseconds", writer?.CommitMicroseconds ?? snapshot.ReferenceCommitMicroseconds, "microseconds")
                ]);
        }
        var rows = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var table in new[] { "source_artifacts", "expected_targets", "actual_targets", "expected_values", "actual_values", "artifact_order_keys", "journal_entries", "journal_sources" })
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM {table}";
            rows.Add(table, (long)(await executeScalarReadAsync(command, cancellationToken).ConfigureAwait(false))!);
        }
        long logicalInputBytes = 0;
        foreach (var table in new[] { "source_artifacts", "expected_targets", "actual_targets" })
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COALESCE(SUM(length(CAST(record_json AS BLOB))),0) FROM {table}";
            logicalInputBytes += (long)(await executeScalarReadAsync(command, cancellationToken).ConfigureAwait(false))!;
        }
        var indexes = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name FROM sqlite_schema WHERE type='index' ORDER BY name";
            await using var reader = await executeReadAsync(command, cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) indexes.Add(reader.GetString(0));
        }
        var worksets = snapshot.Worksets.OrderBy(item => item.Role).ThenBy(item => item.SemanticType, StringComparer.Ordinal)
            .Select(item => new
            {
                role = item.Role.ToString(),
                semanticType = item.SemanticType,
                artifacts = item.Artifacts,
                physicalFieldsObserved = item.PhysicalFieldsObserved,
                fieldsRetained = item.FieldsRetained,
                fieldsOmitted = item.FieldsOmitted,
                fullSerializedArtifactBytesObserved = item.FullSerializedArtifactBytesObserved,
                projectedSerializedArtifactBytesWritten = item.ProjectedSerializedArtifactBytesWritten,
                availableFields = item.AvailableFields.Order(StringComparer.Ordinal).ToArray(),
                requiredFields = item.RequiredFields.Order(StringComparer.Ordinal).ToArray(),
                omittedFields = item.OmittedFields.Order(StringComparer.Ordinal).ToArray()
            }).ToArray();
        var document = new
        {
            rows,
            committedTransactions = snapshot.CommittedTransactions,
            writeBatchSize = snapshot.WriteBatchSize,
            logicalInputBytes,
            databaseBytes = new FileInfo(snapshot.DatabasePath).Length,
            walBytes = File.Exists(snapshot.DatabasePath + "-wal") ? new FileInfo(snapshot.DatabasePath + "-wal").Length : 0,
            indexes,
            secondaryOrderingIndexBuiltAfterIngest = snapshot.OrderingIndexCreated,
            queryPlans = _queryPlans,
            worksets
        };
        var partitionManifest = new
        {
            format = "proofshift-verification-partition-manifest-v1",
            partitionCount = snapshot.PartitionCount,
            hashVersion = VerificationPartitioning.Version,
            ruleContractFingerprint = snapshot.RuleContractFingerprint,
            rules = snapshot.PartitionRules.Select(rule => new
            {
                id = rule.Id,
                type = rule.Type,
                mode = rule.Mode.ToString(),
                basis = rule.Basis?.ToString(),
                role = rule.Role?.ToString(),
                fieldOptions = rule.FieldOptions,
                fields = rule.Fields.Select(key => new { key.SemanticType, key.Field }).ToArray()
            }).ToArray(),
            partitions = Enumerable.Range(0, snapshot.PartitionCount).Select(index => new
            {
                index,
                actualArtifacts = snapshot.PartitionActualRows[index],
                expectedTargetRows = expectedRowsByPartition[index],
                journalEntries = snapshot.PartitionJournalRows[index]
            }).ToArray()
        };
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(partitionManifest);
        var manifest = new
        {
            partitionManifest.format,
            partitionManifest.partitionCount,
            partitionManifest.hashVersion,
            partitionManifest.ruleContractFingerprint,
            partitionManifest.rules,
            partitionManifest.partitions,
            fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(manifestBytes)).ToLowerInvariant()
        };
        await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(snapshot.Directory)!,
            $"{Path.GetFileName(snapshot.Directory)}-partition-manifest.json"), JsonSerializer.Serialize(manifest), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(snapshot.Directory)!,
            Path.GetFileName(snapshot.Directory) + "-diagnostics.json"), JsonSerializer.Serialize(document), cancellationToken).ConfigureAwait(false);
        RecordWorksetDiagnostics(snapshot.Worksets);
    }

    internal static long DirectorySize(string directory) =>
        Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Sum(path => new FileInfo(path).Length);

    internal void RecordWorksetDiagnostics(IReadOnlyList<VerificationWorksetDiagnosticSnapshot> worksets)
    {
        if (performanceRecorder is null) return;
        foreach (var item in worksets.OrderBy(item => item.Role)
            .ThenBy(item => item.SemanticType, StringComparer.Ordinal))
        {
            var measurements = new List<PerformanceMeasurement>
            {
                new("artifactsObserved", item.Artifacts, "artifacts"),
                new("totalPhysicalFieldsObserved", item.PhysicalFieldsObserved, "field-instances"),
                new("fieldsRetained", item.FieldsRetained, "field-instances"),
                new("fieldsOmitted", item.FieldsOmitted, "field-instances"),
                new("fullSerializedArtifactBytesObserved", item.FullSerializedArtifactBytesObserved, "bytes"),
                new("projectedSerializedArtifactBytesWritten", item.ProjectedSerializedArtifactBytesWritten, "bytes"),
                new("availableFieldCount", item.AvailableFields.Length, "fields"),
                new("requiredFieldCount", item.RequiredFields.Length, "fields"),
                new("omittedFieldCount", item.OmittedFields.Length, "fields")
            };
            measurements.AddRange(item.AvailableFields.Order(StringComparer.Ordinal)
                .Select(field => new PerformanceMeasurement($"availableField.{field}", 1, "fields")));
            measurements.AddRange(item.RequiredFields.Order(StringComparer.Ordinal)
                .Select(field => new PerformanceMeasurement($"requiredField.{field}", 1, "fields")));
            measurements.AddRange(item.OmittedFields.Order(StringComparer.Ordinal)
                .Select(field => new PerformanceMeasurement($"omittedField.{field}", 1, "fields")));
            performanceRecorder.RecordMeasuredStage(PerformanceStageKind.Verification,
                $"verification workset {item.Role} {item.SemanticType}", elapsedMicroseconds: 0,
                artifactCount: item.Artifacts, byteCount: item.ProjectedSerializedArtifactBytesWritten,
                measurements: measurements);
        }
    }

    private static void AddPartitionDistribution(PerformanceRecorder.PerformanceStageScope? stage, string name,
        long[] counts)
    {
        if (counts.Length == 0) return;
        var minimum = counts.Min();
        var maximum = counts.Max();
        var average = counts.Average();
        stage?.AddMeasurement($"{name}.minimum", minimum, "artifacts");
        stage?.AddMeasurement($"{name}.maximum", maximum, "artifacts");
        stage?.AddMeasurement($"{name}.average", average, "artifacts");
        stage?.AddMeasurement($"{name}.maximumToAverageRatio", average == 0 ? 0 : maximum / average, "ratio");
    }
}

internal sealed record VerificationWorkspaceDiagnosticsSnapshot(
    string Directory,
    string DatabasePath,
    int PartitionCount,
    bool HasSharedExpected,
    long[] PartitionActualRows,
    long[] PartitionJournalRows,
    long[] PartitionDatabaseHighWater,
    long[] PartitionWalHighWater,
    VerificationPartitionWriterDiagnostics?[] PartitionWriters,
    long ReferenceDmlOperations,
    long ReferenceDmlMicroseconds,
    long ReferenceCommitMicroseconds,
    long CommittedTransactions,
    bool OrderingIndexCreated,
    int WriteBatchSize,
    string? RuleContractFingerprint,
    IReadOnlyList<VerificationPartitionRuleDiagnostics> PartitionRules,
    IReadOnlyList<VerificationWorksetDiagnosticSnapshot> Worksets);

internal sealed record VerificationPartitionWriterDiagnostics(
    long DmlOperations,
    long DmlMicroseconds,
    long CommittedTransactions,
    long CommitMicroseconds);

internal sealed record VerificationPartitionRuleDiagnostics(
    string Id,
    string Type,
    VerificationPartitionExecution Mode,
    VerificationPartitionBasis? Basis,
    VerificationArtifactRole? Role,
    string[] FieldOptions,
    VerificationOrderingKey[] Fields);

internal sealed record VerificationWorksetDiagnosticSnapshot(
    VerificationArtifactRole Role,
    string SemanticType,
    long Artifacts,
    long PhysicalFieldsObserved,
    long FieldsRetained,
    long FieldsOmitted,
    long FullSerializedArtifactBytesObserved,
    long ProjectedSerializedArtifactBytesWritten,
    string[] AvailableFields,
    string[] RequiredFields,
    string[] OmittedFields);
