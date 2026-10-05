using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ProofShift.Domain;

namespace ProofShift.Evidence;

public static class EvidenceFormat
{
    public const string CanonicalizationVersion = "proofshift-evidence-canonical-v2";
    public const string StoreFormatVersion = "proofshift-evidence-store-v2";
}

public sealed record EvidenceGraph
{
    public RunId VerificationRunId { get; }
    public string CanonicalizationVersion { get; }
    public EvidenceVerificationContext? VerificationContext { get; }
    public DomainList<EvidenceRecord> Records { get; }
    public string Fingerprint { get; }

    public EvidenceGraph(RunId verificationRunId, IEnumerable<EvidenceRecord> records,
        EvidenceVerificationContext? verificationContext = null)
    {
        if (verificationRunId.Value == Guid.Empty) throw new ArgumentException("Verification run ID must not be empty.", nameof(verificationRunId));
        VerificationRunId = verificationRunId;
        CanonicalizationVersion = EvidenceFormat.CanonicalizationVersion;
        VerificationContext = verificationContext;
        var materialized = records.OrderBy(record => record.Id.Value).ToArray();
        if (materialized.Any(record => record.RunId != verificationRunId))
            throw new ArgumentException("Every evidence record must belong to the verification run.", nameof(records));
        if (materialized.Select(record => record.Id).Distinct().Count() != materialized.Length)
            throw new ArgumentException("Evidence IDs must be unique within an evidence graph.", nameof(records));
        ValidateReferences(materialized);
        Records = new DomainList<EvidenceRecord>(materialized);
        Fingerprint = EvidenceGraphCanonicalizer.Fingerprint(materialized);
    }

    private static void ValidateReferences(IReadOnlyCollection<EvidenceRecord> records)
    {
        var byId = records.ToDictionary(record => record.Id);
        var edges = records.ToDictionary(record => record.Id,
            record => record.Inputs.Where(input => input.EvidenceId is not null).Select(input => input.EvidenceId!.Value).ToArray());
        foreach (var evidenceId in edges.Values.SelectMany(value => value))
        {
            if (!byId.ContainsKey(evidenceId))
                throw new ArgumentException("Evidence graph contains a reference to an absent evidence record.", nameof(records));
        }

        var visiting = new HashSet<EvidenceId>();
        var visited = new HashSet<EvidenceId>();
        foreach (var id in byId.Keys) Visit(id);

        void Visit(EvidenceId id)
        {
            if (visited.Contains(id)) return;
            if (!visiting.Add(id)) throw new ArgumentException("Evidence references must form a directed acyclic graph.", nameof(records));
            foreach (var child in edges[id]) Visit(child);
            visiting.Remove(id);
            visited.Add(id);
        }
    }
}

public sealed record EvidenceVerificationContext(string ConfigurationHash, string GraphHash,
    string CheckpointId, string CheckpointManifestHash, string SourceFingerprint, string RuleSetFingerprint,
    string? ProjectionRunId = null, string? ProjectionFingerprint = null,
    string? ObservationId = null, string? ObservationRunId = null, string? TargetFingerprint = null,
    string? VerificationLedgerFingerprint = null);

public static class EvidenceGraphCanonicalizer
{
    public static string Fingerprint(IEnumerable<EvidenceRecord> evidenceRecords)
    {
        ArgumentNullException.ThrowIfNull(evidenceRecords);
        var records = evidenceRecords.ToDictionary(record => record.Id);
        var semanticHashes = new Dictionary<EvidenceId, string>();
        foreach (var evidenceId in records.Keys.OrderBy(id => id.Value)) Compute(evidenceId);
        var graph = new StringBuilder();
        Append(graph, EvidenceFormat.CanonicalizationVersion);
        foreach (var record in records.Values.OrderBy(item => semanticHashes[item.Id], StringComparer.Ordinal))
            Append(graph, semanticHashes[record.Id]);
        return Hash(graph.ToString());

        string Compute(EvidenceId id)
        {
            if (semanticHashes.TryGetValue(id, out var existing)) return existing;
            var record = records[id];
            var canonical = new StringBuilder();
            Append(canonical, record.Type.ToString());
            Append(canonical, record.RuleId.Value);
            Append(canonical, record.RuleVersion);
            Append(canonical, record.Result.ToString());
            Append(canonical, record.Severity.ToString());
            Append(canonical, record.Code ?? string.Empty);
            Append(canonical, record.Explanation);
            foreach (var input in record.Inputs.Select(ReferenceKey).Order(StringComparer.Ordinal)) Append(canonical, input);
            Append(canonical, record.Expected is null ? string.Empty : CanonicalValue(record.Expected.Value));
            Append(canonical, record.Actual is null ? string.Empty : CanonicalValue(record.Actual.Value));
            var hash = Hash(canonical.ToString());
            semanticHashes.Add(id, hash);
            return hash;
        }

        string ReferenceKey(EvidenceReference reference)
        {
            if (reference.EvidenceId is { } evidenceId) return "evidence:" + Compute(evidenceId);
            if (reference.ArtifactId is { } artifactId)
                return $"artifact:{reference.GraphNodeId?.Value.ToString("D", CultureInfo.InvariantCulture) ?? "unscoped"}:{artifactId.Value}";
            if (reference.RuleId is { } ruleId) return "rule:" + ruleId.Value;
            if (reference.MigrationEdgeId is { } edgeId) return "edge:" + edgeId.Value.ToString("D", CultureInfo.InvariantCulture);
            if (reference.CheckpointId is { } checkpointId) return "checkpoint:" + checkpointId.Value.ToString("N", CultureInfo.InvariantCulture);
            if (reference.ProjectionRunId is { } projectionId) return "projection:" + projectionId.Value.ToString("N", CultureInfo.InvariantCulture);
            if (reference.RunId is { } runId) return "run:" + runId.Value.ToString("N", CultureInfo.InvariantCulture);
            throw new InvalidOperationException("Evidence reference is empty.");
        }
    }

    private static string CanonicalValue(EvidenceValue value) => CanonicalValue(value.Value);

    private static string CanonicalValue(ValueNode value) => value switch
    {
        NullValue => "null",
        StringValue text => "string:" + text.Value.Normalize(NormalizationForm.FormC),
        IntegerValue integer => "integer:" + integer.Value.ToString(CultureInfo.InvariantCulture),
        DecimalValue number => "decimal:" + number.Value.ToString("G29", CultureInfo.InvariantCulture),
        BooleanValue boolean => boolean.Value ? "boolean:true" : "boolean:false",
        DateValue date => "date:" + date.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        InstantValue instant => "instant:" + instant.Value.ToString("O", CultureInfo.InvariantCulture),
        OffsetDateTimeValue offset => "offset:" + offset.Value.ToString("O", CultureInfo.InvariantCulture),
        LocalDateTimeValue local => "local:" + local.Value.ToString("O", CultureInfo.InvariantCulture),
        BinaryReferenceValue binary => $"binary:{binary.ContentLength.ToString(CultureInfo.InvariantCulture)}:{binary.Sha256}",
        CollectionValue collection => "collection:[" + string.Concat(collection.Values.Select(item => Part(CanonicalValue(item)))) + "]",
        ObjectValue obj => "object:{" + string.Concat(obj.Values.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => Part(pair.Key) + Part(CanonicalValue(pair.Value)))) + "}",
        _ => throw new ArgumentOutOfRangeException(nameof(value), "Unsupported evidence value kind.")
    };

    private static string Part(string value) => $"{Encoding.UTF8.GetByteCount(value).ToString(CultureInfo.InvariantCulture)}:{value}";
    private static void Append(StringBuilder builder, string value) => builder.Append(Part(value));
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

public interface IEvidenceStore
{
    Task<EvidenceStoreReceipt> SaveAsync(EvidenceGraph graph, CancellationToken cancellationToken);
    Task<EvidenceStoreReceipt> SaveAsync(RunId verificationRunId, string canonicalizationVersion,
        string semanticFingerprint, long recordCount, EvidenceVerificationContext? verificationContext,
        IAsyncEnumerable<EvidenceRecord> records,
        CancellationToken cancellationToken);
    Task<bool> VerifyIntegrityAsync(RunId verificationRunId, CancellationToken cancellationToken);
    Task<EvidenceStoreManifest> ReadManifestAsync(RunId verificationRunId, CancellationToken cancellationToken);
    IAsyncEnumerable<JsonElement> ReadRecordsAsync(RunId verificationRunId, CancellationToken cancellationToken);
    ValueTask<Stream> OpenReadAsync(RunId verificationRunId, CancellationToken cancellationToken);
}

public sealed record EvidenceStoreReceipt(RunId VerificationRunId, string RelativePath, string Fingerprint, long RecordCount);

public sealed record EvidenceStoreManifest(string Format, string CanonicalizationVersion, string RunId,
    string Fingerprint, long RecordCount, EvidenceVerificationContext? VerificationContext,
    string SegmentFile, string SegmentSha256, bool Complete, string? IntegrityHash);
