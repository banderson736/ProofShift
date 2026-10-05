using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ProofShift.Domain;

namespace ProofShift.Evidence;

public sealed class FileSystemEvidenceStore : IEvidenceStore
{
    private const string SegmentFileName = "evidence.ndjson";
    private static readonly byte[] NewLine = [(byte)'\n'];
    private readonly string _root;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public FileSystemEvidenceStore(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.GetFullPath(root);
    }

    public Task<EvidenceStoreReceipt> SaveAsync(EvidenceGraph graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(graph);
        return SaveAsync(graph.VerificationRunId, graph.CanonicalizationVersion, graph.Fingerprint,
            graph.Records.Count, graph.VerificationContext, EnumerateRecordsAsync(graph.Records), cancellationToken);
    }

    public async Task<EvidenceStoreReceipt> SaveAsync(RunId verificationRunId, string canonicalizationVersion,
        string semanticFingerprint, long recordCount, EvidenceVerificationContext? verificationContext,
        IAsyncEnumerable<EvidenceRecord> records,
        CancellationToken cancellationToken)
    {
        if (verificationRunId.Value == Guid.Empty) throw new ArgumentException("Verification run ID must not be empty.", nameof(verificationRunId));
        ArgumentNullException.ThrowIfNull(records);
        if (canonicalizationVersion != EvidenceFormat.CanonicalizationVersion)
            throw new ArgumentException("Evidence canonicalization version is unsupported.", nameof(canonicalizationVersion));
        if (semanticFingerprint.Length != 64 || semanticFingerprint.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("Evidence fingerprint must be a SHA-256 digest.", nameof(semanticFingerprint));
        ArgumentOutOfRangeException.ThrowIfNegative(recordCount);
        cancellationToken.ThrowIfCancellationRequested();
        var runDirectory = Path.Combine(_root, verificationRunId.Value.ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(runDirectory);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(runDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var segmentPath = Path.Combine(runDirectory, SegmentFileName);
        var manifestPath = Path.Combine(runDirectory, "manifest.json");
        var temporarySegment = Path.Combine(runDirectory, $"evidence-{Guid.NewGuid():N}.partial");
        var temporaryManifest = Path.Combine(runDirectory, $"manifest-{Guid.NewGuid():N}.partial");
        var segmentMoved = false;
        var manifestMoved = false;
        long streamedRecordCount = 0;
        try
        {
            using var segmentHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            Guid? previousRecordId = null;
            await using (var stream = new FileStream(temporarySegment, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await foreach (var record in records.WithCancellation(cancellationToken).ConfigureAwait(false))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (record.RunId != verificationRunId)
                        throw new InvalidDataException("Evidence record belongs to a different verification run.");
                    if (previousRecordId is { } priorId && record.Id.Value.CompareTo(priorId) <= 0)
                        throw new InvalidDataException("Streaming Evidence records must be strictly ordered by stable evidence ID.");
                    previousRecordId = record.Id.Value;
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(ToDocument(record), JsonOptions);
                    await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                    await stream.WriteAsync(NewLine, cancellationToken).ConfigureAwait(false);
                    segmentHash.AppendData(bytes);
                    segmentHash.AppendData(NewLine);
                    streamedRecordCount++;
                }
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (streamedRecordCount != recordCount)
                throw new InvalidDataException("Evidence stream record count does not match its declared count.");
            var segmentSha256 = Convert.ToHexString(segmentHash.GetHashAndReset()).ToLowerInvariant();
            File.Move(temporarySegment, segmentPath, overwrite: false);
            segmentMoved = true;

            var manifest = new EvidenceStoreManifest(EvidenceFormat.StoreFormatVersion,
                canonicalizationVersion, verificationRunId.Value.ToString("D", CultureInfo.InvariantCulture),
                semanticFingerprint, streamedRecordCount, verificationContext, SegmentFileName, segmentSha256,
                Complete: true, IntegrityHash: null);
            var integrityHash = Hash(JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions));
            var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest with { IntegrityHash = integrityHash }, JsonOptions);
            await File.WriteAllBytesAsync(temporaryManifest, manifestBytes, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryManifest, manifestPath, overwrite: false);
            manifestMoved = true;
        }
        finally
        {
            if (File.Exists(temporarySegment)) File.Delete(temporarySegment);
            if (File.Exists(temporaryManifest)) File.Delete(temporaryManifest);
            if (segmentMoved && !manifestMoved && File.Exists(segmentPath)) File.Delete(segmentPath);
        }

        return new EvidenceStoreReceipt(verificationRunId,
            Path.GetRelativePath(_root, manifestPath).Replace(Path.DirectorySeparatorChar, '/'), semanticFingerprint, streamedRecordCount);
    }

    private static async IAsyncEnumerable<EvidenceRecord> EnumerateRecordsAsync(IEnumerable<EvidenceRecord> records)
    {
        foreach (var record in records) yield return record;
        await Task.CompletedTask.ConfigureAwait(false);
    }

    public async Task<bool> VerifyIntegrityAsync(RunId verificationRunId, CancellationToken cancellationToken)
    {
        try
        {
            var manifest = await ReadManifestCoreAsync(verificationRunId, cancellationToken).ConfigureAwait(false);
            if (manifest.Format != EvidenceFormat.StoreFormatVersion ||
                manifest.CanonicalizationVersion != EvidenceFormat.CanonicalizationVersion ||
                !Guid.TryParseExact(manifest.RunId, "D", out var parsedRunId) || parsedRunId != verificationRunId.Value ||
                manifest.Fingerprint.Length != 64 || manifest.RecordCount < 0 || !manifest.Complete ||
                manifest.SegmentFile != SegmentFileName || manifest.IntegrityHash is null || manifest.SegmentSha256.Length != 64)
                return false;
            var actualManifestHash = Hash(JsonSerializer.SerializeToUtf8Bytes(manifest with { IntegrityHash = null }, JsonOptions));
            if (!string.Equals(actualManifestHash, manifest.IntegrityHash, StringComparison.Ordinal)) return false;

            using var segmentHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using var segment = File.OpenRead(GetSegmentPath(verificationRunId, manifest.SegmentFile));
            var buffer = new byte[64 * 1024];
            long recordCount = 0;
            var lastByte = -1;
            int read;
            while ((read = await segment.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                segmentHash.AppendData(buffer.AsSpan(0, read));
                for (var index = 0; index < read; index++)
                    if (buffer[index] == (byte)'\n') recordCount++;
                lastByte = buffer[read - 1];
            }
            var actualSegmentHash = Convert.ToHexString(segmentHash.GetHashAndReset()).ToLowerInvariant();
            return lastByte == (byte)'\n' && recordCount == manifest.RecordCount &&
                string.Equals(actualSegmentHash, manifest.SegmentSha256, StringComparison.Ordinal);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    public async Task<EvidenceStoreManifest> ReadManifestAsync(RunId verificationRunId, CancellationToken cancellationToken)
    {
        if (!await VerifyIntegrityAsync(verificationRunId, cancellationToken).ConfigureAwait(false))
            throw new InvalidDataException("Evidence store integrity verification failed.");
        return await ReadManifestCoreAsync(verificationRunId, cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<JsonElement> ReadRecordsAsync(RunId verificationRunId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var manifest = await ReadManifestAsync(verificationRunId, cancellationToken).ConfigureAwait(false);
        await using var stream = File.OpenRead(GetSegmentPath(verificationRunId, manifest.SegmentFile));
        using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false,
            bufferSize: 64 * 1024, leaveOpen: true);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var document = JsonDocument.Parse(line);
            yield return document.RootElement.Clone();
        }
    }

    public async ValueTask<Stream> OpenReadAsync(RunId verificationRunId, CancellationToken cancellationToken)
    {
        if (!await VerifyIntegrityAsync(verificationRunId, cancellationToken).ConfigureAwait(false))
            throw new InvalidDataException("Evidence store integrity verification failed.");
        return new FileStream(GetSegmentPath(verificationRunId, SegmentFileName), FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    private async Task<EvidenceStoreManifest> ReadManifestCoreAsync(RunId runId, CancellationToken cancellationToken)
    {
        var path = GetManifestPath(runId);
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<EvidenceStoreManifest>(stream, JsonOptions, cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidDataException("Evidence manifest is empty.");
    }

    private string GetManifestPath(RunId runId)
    {
        var path = Path.GetFullPath(Path.Combine(_root, runId.Value.ToString("N", CultureInfo.InvariantCulture), "manifest.json"));
        var relative = Path.GetRelativePath(_root, path);
        if (Path.IsPathRooted(relative) || relative.StartsWith("..", StringComparison.Ordinal))
            throw new InvalidOperationException("Evidence path is outside the configured store.");
        return path;
    }

    private string GetSegmentPath(RunId runId, string fileName)
    {
        if (fileName != SegmentFileName) throw new InvalidDataException("Evidence manifest references an unsupported segment path.");
        var directory = Path.GetDirectoryName(GetManifestPath(runId))!;
        var path = Path.GetFullPath(Path.Combine(directory, fileName));
        var relative = Path.GetRelativePath(directory, path);
        if (Path.IsPathRooted(relative) || relative.StartsWith("..", StringComparison.Ordinal))
            throw new InvalidOperationException("Evidence segment path is outside its run directory.");
        return path;
    }

    private static EvidenceRecordDocument ToDocument(EvidenceRecord record) => new(
        record.Id.Value.ToString("D", CultureInfo.InvariantCulture), record.RunId.Value.ToString("D", CultureInfo.InvariantCulture),
        record.Type.ToString(), record.RuleId.Value, record.RuleVersion, record.Result.ToString(), record.Severity.ToString(),
        record.Code, record.Explanation, record.EvaluatedAt.ToString("O", CultureInfo.InvariantCulture),
        record.Inputs.Select(ToDocument).ToArray(), record.Expected is null ? null : ToDocument(record.Expected.Value),
        record.Actual is null ? null : ToDocument(record.Actual.Value));

    private static EvidenceReferenceDocument ToDocument(EvidenceReference reference) => new(
        reference.ArtifactId?.Value, reference.GraphNodeId?.Value.ToString("D", CultureInfo.InvariantCulture),
        reference.EvidenceId?.Value.ToString("D", CultureInfo.InvariantCulture), reference.RuleId?.Value,
        reference.MigrationEdgeId?.Value.ToString("D", CultureInfo.InvariantCulture),
        reference.RunId?.Value.ToString("D", CultureInfo.InvariantCulture),
        reference.CheckpointId?.Value.ToString("D", CultureInfo.InvariantCulture),
        reference.ProjectionRunId?.Value.ToString("D", CultureInfo.InvariantCulture));

    private static EvidenceValueDocument ToDocument(EvidenceValue evidence) => ToDocument(evidence.Value);

    private static EvidenceValueDocument ToDocument(ValueNode value) => value switch
    {
        NullValue => new("null", null, [], null),
        StringValue text => new("string", text.Value, [], null),
        IntegerValue integer => new("integer", integer.Value.ToString(CultureInfo.InvariantCulture), [], null),
        DecimalValue number => new("decimal", number.Value.ToString("G29", CultureInfo.InvariantCulture), [], null),
        BooleanValue boolean => new("boolean", boolean.Value ? "true" : "false", [], null),
        DateValue date => new("date", date.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), [], null),
        InstantValue instant => new("instant", instant.Value.ToString("O", CultureInfo.InvariantCulture), [], null),
        OffsetDateTimeValue offset => new("offset", offset.Value.ToString("O", CultureInfo.InvariantCulture), [], null),
        LocalDateTimeValue local => new("local", local.Value.ToString("O", CultureInfo.InvariantCulture), [], null),
        BinaryReferenceValue binary => new("binary-reference", $"{binary.ContentLength}:{binary.Sha256}", [], null),
        CollectionValue collection => new("collection", null, collection.Values.Select(ToDocument).ToArray(), null),
        ObjectValue obj => new("object", null, [], obj.Values.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new EvidenceValuePropertyDocument(pair.Key, ToDocument(pair.Value))).ToArray()),
        _ => throw new ArgumentOutOfRangeException(nameof(value), "Unsupported evidence value kind.")
    };

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed record EvidenceRecordDocument(string Id, string RunId, string Type, string RuleId, string RuleVersion,
        string Result, string Severity, string? Code, string Explanation, string EvaluatedAt,
        EvidenceReferenceDocument[] Inputs, EvidenceValueDocument? Expected, EvidenceValueDocument? Actual);

    private sealed record EvidenceReferenceDocument(string? ArtifactId, string? GraphNodeId, string? EvidenceId,
        string? RuleId, string? MigrationEdgeId, string? RunId, string? CheckpointId, string? ProjectionRunId);

    private sealed record EvidenceValueDocument(string Kind, string? Value, EvidenceValueDocument[] Children,
        EvidenceValuePropertyDocument[]? Properties);

    private sealed record EvidenceValuePropertyDocument(string Name, EvidenceValueDocument Value);
}