using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ProofShift.Domain;

namespace ProofShift.Evidence;

public sealed class FileSystemEvidenceStore : IEvidenceStore
{
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

    public async Task<EvidenceStoreReceipt> SaveAsync(EvidenceGraph graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(graph);
        cancellationToken.ThrowIfCancellationRequested();
        var runDirectory = Path.Combine(_root, graph.VerificationRunId.Value.ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(runDirectory);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(runDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var records = graph.Records.Select(ToDocument).ToArray();
        var draft = new StoreDocument(EvidenceFormat.StoreFormatVersion,
            graph.VerificationRunId.Value.ToString("D", CultureInfo.InvariantCulture), graph.Fingerprint, records, null);
        var payloadHash = Hash(JsonSerializer.SerializeToUtf8Bytes(draft, JsonOptions));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(draft with { IntegrityHash = payloadHash }, JsonOptions);
        var path = Path.Combine(runDirectory, "evidence.json");
        var temporary = Path.Combine(runDirectory, $"evidence-{Guid.NewGuid():N}.partial");
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }

        return new EvidenceStoreReceipt(graph.VerificationRunId,
            Path.GetRelativePath(_root, path).Replace(Path.DirectorySeparatorChar, '/'), graph.Fingerprint, records.Length);
    }

    public async Task<bool> VerifyIntegrityAsync(RunId verificationRunId, CancellationToken cancellationToken)
    {
        var path = GetPath(verificationRunId);
        try
        {
            await using var stream = File.OpenRead(path);
            var document = await JsonSerializer.DeserializeAsync<StoreDocument>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            if (document is null || document.Format != EvidenceFormat.StoreFormatVersion ||
                !Guid.TryParseExact(document.RunId, "D", out var parsedRunId) || parsedRunId != verificationRunId.Value ||
                document.Fingerprint.Length != 64 || document.IntegrityHash is null)
                return false;
            var actualHash = Hash(JsonSerializer.SerializeToUtf8Bytes(document with { IntegrityHash = null }, JsonOptions));
            return string.Equals(actualHash, document.IntegrityHash, StringComparison.Ordinal);
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

    public async ValueTask<Stream> OpenReadAsync(RunId verificationRunId, CancellationToken cancellationToken)
    {
        if (!await VerifyIntegrityAsync(verificationRunId, cancellationToken).ConfigureAwait(false))
            throw new InvalidDataException("Evidence store integrity verification failed.");
        return new FileStream(GetPath(verificationRunId), FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    private string GetPath(RunId runId)
    {
        var path = Path.GetFullPath(Path.Combine(_root, runId.Value.ToString("N", CultureInfo.InvariantCulture), "evidence.json"));
        var relative = Path.GetRelativePath(_root, path);
        if (Path.IsPathRooted(relative) || relative.StartsWith("..", StringComparison.Ordinal))
            throw new InvalidOperationException("Evidence path is outside the configured store.");
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

    private sealed record StoreDocument(string Format, string RunId, string Fingerprint,
        EvidenceRecordDocument[] Records, string? IntegrityHash);

    private sealed record EvidenceRecordDocument(string Id, string RunId, string Type, string RuleId, string RuleVersion,
        string Result, string Severity, string? Code, string Explanation, string EvaluatedAt,
        EvidenceReferenceDocument[] Inputs, EvidenceValueDocument? Expected, EvidenceValueDocument? Actual);

    private sealed record EvidenceReferenceDocument(string? ArtifactId, string? GraphNodeId, string? EvidenceId,
        string? RuleId, string? MigrationEdgeId, string? RunId, string? CheckpointId, string? ProjectionRunId);

    private sealed record EvidenceValueDocument(string Kind, string? Value, EvidenceValueDocument[] Children,
        EvidenceValuePropertyDocument[]? Properties);

    private sealed record EvidenceValuePropertyDocument(string Name, EvidenceValueDocument Value);
}