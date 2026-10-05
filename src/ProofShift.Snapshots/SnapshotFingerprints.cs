using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using ProofShift.Domain;

namespace ProofShift.Snapshots;

public static class SnapshotFingerprints
{
    public const string SourceFingerprintVersion = "proofshift-source-fingerprint-v1";
    public const string ManifestCanonicalizationVersion = "proofshift-snapshot-manifest-v2";
    public const string MaterializedFormatVersion = "proofshift-materialized-snapshot-v1";

    private static readonly BigInteger Modulus = BigInteger.One << 256;

    public static string SelectorHash(ArtifactSelector selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        var canonical = new StringBuilder();
        Append(canonical, selector.Kind);
        foreach (var property in selector.Properties.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            Append(canonical, property.Key);
            Append(canonical, property.Value);
        }

        canonical.Append("identities:").Append(selector.IdentityFields.Count.ToString(CultureInfo.InvariantCulture)).Append(';');
        foreach (var identity in selector.IdentityFields)
        {
            Append(canonical, identity);
        }

        return Hash(canonical.ToString());
    }

    public static string RecordFingerprint(RecordEnvelope record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var canonical = new StringBuilder();
        Append(canonical, "proofshift-source-artifact-v1");
        Append(canonical, record.Artifact.SystemId.Value);
        Append(canonical, record.Artifact.EndpointId.Value);
        Append(canonical, record.Artifact.ArtifactType);
        Append(canonical, record.Artifact.Identity);
        Append(canonical, record.SemanticType);
        foreach (var pair in record.Values.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            Append(canonical, pair.Key);
            Append(canonical, CanonicalValue(pair.Value));
        }

        foreach (var relationship in record.Relationships
                     .OrderBy(item => item.Type, StringComparer.Ordinal)
                     .ThenBy(item => item.Target.Id.Value, StringComparer.Ordinal)
                     .ThenBy(item => item.Direction))
        {
            Append(canonical, relationship.Type);
            Append(canonical, relationship.Direction.ToString());
            Append(canonical, relationship.Target.SystemId.Value);
            Append(canonical, relationship.Target.EndpointId.Value);
            Append(canonical, relationship.Target.ArtifactType);
            Append(canonical, relationship.Target.Identity);
        }

        if (record.Temporal is { } temporal)
        {
            Append(canonical, temporal.EffectiveFrom?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty);
            Append(canonical, temporal.EffectiveTo?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty);
            Append(canonical, temporal.RecordedAt?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty);
            Append(canonical, temporal.Version?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
        }
        else
        {
            Append(canonical, "no-temporal-metadata");
        }

        Append(canonical, record.Provenance.Connector.Value);
        Append(canonical, record.Provenance.Endpoint.Value);
        Append(canonical, record.Provenance.Location);
        Append(canonical, record.Provenance.SourceHash ?? string.Empty);
        foreach (var pair in record.Provenance.Metadata.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (pair.Key.StartsWith("snapshot", StringComparison.Ordinal))
            {
                continue;
            }

            Append(canonical, pair.Key);
            Append(canonical, pair.Value);
        }

        return Hash(canonical.ToString());
    }

    public static string EndpointFingerprint(string sourceNodeKey, long count, string modularSumHex)
    {
        var canonical = new StringBuilder();
        Append(canonical, SourceFingerprintVersion);
        Append(canonical, sourceNodeKey);
        Append(canonical, count.ToString(CultureInfo.InvariantCulture));
        Append(canonical, modularSumHex);
        return Hash(canonical.ToString());
    }

    public static string AggregateSourceFingerprint(string graphHash, IEnumerable<CheckpointEndpoint> endpoints)
    {
        var canonical = new StringBuilder();
        Append(canonical, SourceFingerprintVersion);
        Append(canonical, graphHash);
        foreach (var endpoint in endpoints.OrderBy(item => item.SourceNodeKey, StringComparer.Ordinal))
        {
            Append(canonical, endpoint.SourceNodeKey);
            Append(canonical, endpoint.SourceFingerprint);
        }

        return Hash(canonical.ToString());
    }

    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    internal static string CanonicalValue(ValueNode value) => value switch
    {
        NullValue => "null",
        StringValue text => Part("string") + Part(text.Value.Normalize(NormalizationForm.FormC)),
        IntegerValue integer => Part("integer") + Part(integer.Value.ToString(CultureInfo.InvariantCulture)),
        DecimalValue number => Part("decimal") + Part(number.Value.ToString("G29", CultureInfo.InvariantCulture)),
        BooleanValue boolean => boolean.Value ? "boolean:1" : "boolean:0",
        DateValue date => Part("date") + Part(date.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
        InstantValue instant => Part("instant") + Part(instant.Value.ToString("O", CultureInfo.InvariantCulture)),
        OffsetDateTimeValue offset => Part("offset-date-time") + Part(offset.Value.ToString("O", CultureInfo.InvariantCulture)),
        LocalDateTimeValue local => Part("local-date-time") + Part(local.Value.ToString("O", CultureInfo.InvariantCulture)),
        BinaryReferenceValue binary => Part("binary") + Part(binary.ContentLength.ToString(CultureInfo.InvariantCulture)) + Part(binary.Sha256),
        CollectionValue collection => Part("collection") + collection.Values.Count.ToString(CultureInfo.InvariantCulture) + ";" +
            string.Concat(collection.Values.Select(item => Part(CanonicalValue(item)))),
        ObjectValue obj => Part("object") + obj.Values.Count.ToString(CultureInfo.InvariantCulture) + ";" +
            string.Concat(obj.Values.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => Part(pair.Key) + Part(CanonicalValue(pair.Value)))),
        _ => throw new SnapshotStoreException(SnapshotIssueCodes.UnsupportedFormat, "Source contains an unsupported normalized value type.")
    };

    internal static void Append(StringBuilder builder, string value) => builder.Append(Part(value));

    internal static string Part(string value) =>
        $"{Encoding.UTF8.GetByteCount(value).ToString(CultureInfo.InvariantCulture)}:{value};";

    internal sealed class MultisetAccumulator
    {
        private BigInteger _sum;
        public long Count { get; private set; }

        public void Add(string fingerprint)
        {
            var bytes = Convert.FromHexString(fingerprint);
            _sum = (_sum + new BigInteger(bytes, isUnsigned: true, isBigEndian: true)) % Modulus;
            Count = checked(Count + 1);
        }

        public string Finish(string sourceNodeKey) => EndpointFingerprint(sourceNodeKey, Count,
            _sum.ToString("x64", CultureInfo.InvariantCulture));
    }
}