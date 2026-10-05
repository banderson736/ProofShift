using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using ProofShift.Domain;

namespace ProofShift.Snapshots;

internal static class SnapshotManifestCodec
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public static (byte[] Bytes, string Hash) Encode(SourceCheckpoint checkpoint)
    {
        var document = FromDomain(checkpoint, manifestHash: null);
        var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions))).ToLowerInvariant();
        return (JsonSerializer.SerializeToUtf8Bytes(document with { ManifestHash = hash }, JsonOptions), hash);
    }

    public static SourceCheckpoint Decode(ReadOnlySpan<byte> bytes)
    {
        SnapshotManifestDocument document;
        try
        {
            document = JsonSerializer.Deserialize<SnapshotManifestDocument>(bytes, JsonOptions)
                ?? throw new SnapshotStoreException(SnapshotIssueCodes.IntegrityFailure, "Snapshot manifest is empty.");
        }
        catch (SnapshotStoreException)
        {
            throw;
        }
        catch
        {
            throw new SnapshotStoreException(SnapshotIssueCodes.IntegrityFailure, "Snapshot manifest could not be decoded.");
        }

        if (!string.Equals(document.ManifestFormatVersion, SnapshotFingerprints.ManifestCanonicalizationVersion, StringComparison.Ordinal) ||
            !string.Equals(document.MaterializedFormatVersion, SnapshotFingerprints.MaterializedFormatVersion, StringComparison.Ordinal))
        {
            throw new SnapshotStoreException(SnapshotIssueCodes.UnsupportedFormat, "Snapshot manifest or materialized record format is unsupported.");
        }

        var canonicalDocument = document with { ManifestHash = null };
        var actualHash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(canonicalDocument, JsonOptions))).ToLowerInvariant();
        if (!string.Equals(actualHash, document.ManifestHash, StringComparison.Ordinal))
        {
            throw new SnapshotStoreException(SnapshotIssueCodes.IntegrityFailure, "Snapshot manifest hash does not match its canonical content.");
        }

        if (!string.Equals(document.Status, "complete", StringComparison.Ordinal))
        {
            throw new SnapshotStoreException(SnapshotIssueCodes.CheckpointNotComplete, "Only complete checkpoints can be replayed.");
        }

        try
        {
            return ToDomain(document);
        }
        catch (SnapshotStoreException)
        {
            throw;
        }
        catch
        {
            throw new SnapshotStoreException(SnapshotIssueCodes.IntegrityFailure, "Snapshot manifest fields are invalid.");
        }
    }

    private static SnapshotManifestDocument FromDomain(SourceCheckpoint checkpoint, string? manifestHash) => new(
        SnapshotFingerprints.ManifestCanonicalizationVersion,
        SnapshotFingerprints.MaterializedFormatVersion,
        manifestHash,
        checkpoint.Id.Value.ToString("N", CultureInfo.InvariantCulture),
        checkpoint.Status.ToString().ToLowerInvariant(),
        checkpoint.ProjectId,
        checkpoint.ConfigurationHash,
        checkpoint.GraphHash,
        checkpoint.CaptureStartedAt.ToString("O", CultureInfo.InvariantCulture),
        checkpoint.CaptureCompletedAt.ToString("O", CultureInfo.InvariantCulture),
        checkpoint.MaximumEndpointSkew.Ticks,
        checkpoint.CrossSystemAtomic,
        checkpoint.Replayable,
        checkpoint.Guarantee.ToString().ToLowerInvariant(),
        checkpoint.ArtifactCount,
        checkpoint.ByteCount,
        checkpoint.SourceFingerprint,
        checkpoint.Endpoints.Select(endpoint => new EndpointDocument(
            endpoint.SourceNodeKey,
            endpoint.SystemId.Value,
            endpoint.EndpointId.Value,
            endpoint.Connector.Value,
            endpoint.ConnectorVersion,
            endpoint.SelectorHash,
            endpoint.IdentityFields.ToArray(),
            endpoint.CaptureStartedAt.ToString("O", CultureInfo.InvariantCulture),
            endpoint.CaptureCompletedAt.ToString("O", CultureInfo.InvariantCulture),
            endpoint.SourceConsistency.ToString().ToLowerInvariant(),
            endpoint.RequestedConsistencyStrategy,
            endpoint.EffectiveConsistencyStrategy,
            endpoint.ConsistencyDowngrade,
            endpoint.Guarantee.ToString().ToLowerInvariant(),
            endpoint.Replayable,
            endpoint.ArtifactCount,
            endpoint.ByteCount,
            endpoint.SourceFingerprint,
            endpoint.SegmentReference,
            endpoint.SegmentSha256,
            endpoint.SegmentLength)).OrderBy(endpoint => endpoint.SourceNodeKey, StringComparer.Ordinal).ToArray());

    private static SourceCheckpoint ToDomain(SnapshotManifestDocument document)
    {
        var endpoints = document.Endpoints.Select(endpoint => new CheckpointEndpoint(
            endpoint.SourceNodeKey,
            new SystemId(endpoint.SystemId),
            new StorageEndpointId(endpoint.EndpointId),
            new ConnectorId(endpoint.Connector),
            endpoint.ConnectorVersion,
            endpoint.SelectorHash,
            endpoint.IdentityFields,
            DateTimeOffset.ParseExact(endpoint.CaptureStartedAt, "O", CultureInfo.InvariantCulture, DateTimeStyles.None),
            DateTimeOffset.ParseExact(endpoint.CaptureCompletedAt, "O", CultureInfo.InvariantCulture, DateTimeStyles.None),
            Enum.Parse<SourceConsistencyGuarantee>(endpoint.SourceConsistency, ignoreCase: true),
            Enum.Parse<CheckpointGuarantee>(endpoint.Guarantee, ignoreCase: true),
            endpoint.Replayable,
            endpoint.ArtifactCount,
            endpoint.ByteCount,
            endpoint.SourceFingerprint,
            endpoint.SegmentReference,
            endpoint.SegmentSha256,
            endpoint.SegmentLength,
            endpoint.RequestedConsistencyStrategy,
            endpoint.EffectiveConsistencyStrategy,
            endpoint.ConsistencyDowngrade)).ToArray();

        return new SourceCheckpoint(
            new CheckpointId(Guid.ParseExact(document.CheckpointId, "N")),
            Enum.Parse<CheckpointStatus>(document.Status, ignoreCase: true),
            document.ProjectId,
            document.ConfigurationHash,
            document.GraphHash,
            DateTimeOffset.ParseExact(document.CaptureStartedAt, "O", CultureInfo.InvariantCulture, DateTimeStyles.None),
            DateTimeOffset.ParseExact(document.CaptureCompletedAt, "O", CultureInfo.InvariantCulture, DateTimeStyles.None),
            TimeSpan.FromTicks(document.MaximumEndpointSkewTicks),
            document.CrossSystemAtomic,
            document.Replayable,
            Enum.Parse<CheckpointGuarantee>(document.Guarantee, ignoreCase: true),
            endpoints,
            document.ArtifactCount,
            document.ByteCount,
            document.SourceFingerprint,
            document.ManifestHash);
    }

    private sealed record SnapshotManifestDocument(
        string ManifestFormatVersion,
        string MaterializedFormatVersion,
        string? ManifestHash,
        string CheckpointId,
        string Status,
        string ProjectId,
        string ConfigurationHash,
        string GraphHash,
        string CaptureStartedAt,
        string CaptureCompletedAt,
        long MaximumEndpointSkewTicks,
        bool CrossSystemAtomic,
        bool Replayable,
        string Guarantee,
        long ArtifactCount,
        long ByteCount,
        string? SourceFingerprint,
        EndpointDocument[] Endpoints);

    private sealed record EndpointDocument(
        string SourceNodeKey,
        string SystemId,
        string EndpointId,
        string Connector,
        string ConnectorVersion,
        string SelectorHash,
        string[] IdentityFields,
        string CaptureStartedAt,
        string CaptureCompletedAt,
        string SourceConsistency,
        string RequestedConsistencyStrategy,
        string EffectiveConsistencyStrategy,
        string? ConsistencyDowngrade,
        string Guarantee,
        bool Replayable,
        long ArtifactCount,
        long ByteCount,
        string SourceFingerprint,
        string SegmentReference,
        string SegmentSha256,
        long SegmentLength);
}