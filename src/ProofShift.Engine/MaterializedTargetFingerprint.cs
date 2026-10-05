using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ProofShift.Connectors.Abstractions;
using ProofShift.Domain;

namespace ProofShift.Engine;

public sealed record MaterializedTargetReadback(
    string NodeKey,
    ArtifactSelector Selector,
    IShadowTargetConnector Connector,
    ShadowTargetContext Context);

public sealed record MaterializedTargetFingerprintResult(string Fingerprint, long RecordCount);

public static class MaterializedTargetFingerprint
{
    public const string Version = "proofshift-projection-fingerprint-v1";

    public static MaterializedTargetFingerprintBuilder CreateBuilder(string graphHash) => new(graphHash);

    public static Task<MaterializedTargetFingerprintResult> ComputeAsync(
        IEnumerable<MaterializedTargetReadback> targets,
        string graphHash,
        CancellationToken cancellationToken) =>
        ComputeAsync(targets, graphHash, performanceRecorder: null, cancellationToken);

    public static async Task<MaterializedTargetFingerprintResult> ComputeAsync(
        IEnumerable<MaterializedTargetReadback> targets,
        string graphHash,
        PerformanceRecorder? performanceRecorder,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentException.ThrowIfNullOrWhiteSpace(graphHash);
        var builder = CreateBuilder(graphHash);
        foreach (var target in targets.OrderBy(item => item.NodeKey, StringComparer.Ordinal))
        {
            using var targetStage = performanceRecorder?.StartStage(PerformanceStageKind.Projection,
                "target read-back node", target.Connector.Id.Value, target.NodeKey);
            long nodeArtifactCount = 0;
            await foreach (var record in target.Connector.ReadAsync(
                new ReadRequest(target.Context, target.Selector), cancellationToken)
                .WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                builder.Add(target.NodeKey, record);
                nodeArtifactCount++;
            }
            targetStage?.AddArtifacts(nodeArtifactCount);
        }

        return builder.Finish();
    }
}

public sealed class MaterializedTargetFingerprintBuilder : IDisposable
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private bool _finished;
    public long RecordCount { get; private set; }

    internal MaterializedTargetFingerprintBuilder(string graphHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(graphHash);
        Append(_hash, MaterializedTargetFingerprint.Version);
        Append(_hash, graphHash);
    }

    public void Add(string nodeKey, RecordEnvelope record)
    {
        if (_finished) throw new InvalidOperationException("Projection fingerprint builder is finalized.");
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeKey);
        ArgumentNullException.ThrowIfNull(record);
        RecordCount = checked(RecordCount + 1);
        Append(_hash, "record-v1");
        Append(_hash, nodeKey);
        Append(_hash, record.Artifact.Identity);
        Append(_hash, record.SemanticType);
        Append(_hash, record.Values.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var pair in record.Values.OrderBy(value => value.Key, StringComparer.Ordinal))
        {
            Append(_hash, pair.Key);
            Append(_hash, GraphTargetIdentity.CanonicalValue(pair.Value));
        }
    }

    public MaterializedTargetFingerprintResult Finish()
    {
        if (_finished) throw new InvalidOperationException("Projection fingerprint builder is finalized.");
        _finished = true;
        return new MaterializedTargetFingerprintResult(Convert.ToHexString(_hash.GetHashAndReset()).ToLowerInvariant(), RecordCount);
    }

    public void Dispose() => _hash.Dispose();

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
}
