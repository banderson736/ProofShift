using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ProofShift.Domain;

namespace ProofShift.Connectors.Abstractions;

public sealed record PhysicalField(string Name, string NativeType, bool IsNullable, int Ordinal,
    bool TypeInferred = false, long EmptyValues = 0);
public sealed record PhysicalKey(string Name, bool Primary, IReadOnlyList<string> Fields);
public sealed record PhysicalRelationship(string Name, IReadOnlyList<string> Fields, string TargetSchema,
    string TargetObject, IReadOnlyList<string> TargetFields);
public sealed record PhysicalObject(string Schema, string Name, string Kind, IReadOnlyList<PhysicalField> Fields,
    IReadOnlyList<PhysicalKey> Keys, IReadOnlyList<PhysicalRelationship> Relationships,
    long? RecordCount = null, long? Bytes = null, IReadOnlyDictionary<string, string>? SelectorProperties = null);

public sealed record PhysicalDiscoveryArtifact(string Format, string ConnectorId, string ConnectorVersion,
    string SystemId, string EndpointId, DateTimeOffset ObservedAt, string Fingerprint, IReadOnlyList<PhysicalObject> Objects);

public interface IPhysicalDiscoveryConnector
{
    Task<PhysicalDiscoveryArtifact> DiscoverAsync(ConnectorContext context,
        IReadOnlyCollection<ArtifactSelector> selectors, CancellationToken cancellationToken);
}

public static class PhysicalDiscovery
{
    public const string Format = "proofshift-physical-discovery-v1";
    public static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    public static PhysicalDiscoveryArtifact Create(ConnectorContext context, string connectorId, string connectorVersion,
        IEnumerable<PhysicalObject> objects, DateTimeOffset? observedAt = null)
    {
        var ordered = Normalize(objects);
        var structural = ordered.Select(item => item with { RecordCount = null, Bytes = null,
            Fields = item.Fields.Select(field => field with { EmptyValues = 0 }).ToArray() }).ToArray();
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { format = Format, connectorId, connectorVersion, objects = structural }, JsonOptions)))).ToLowerInvariant();
        return new(Format, connectorId, connectorVersion, context.SystemKey, context.EndpointKey,
            observedAt ?? DateTimeOffset.UtcNow, fingerprint, ordered);
    }

    public static bool Verify(PhysicalDiscoveryArtifact artifact)
    {
        var structural = Normalize(artifact.Objects).Select(item => item with { RecordCount = null, Bytes = null,
            Fields = item.Fields.Select(field => field with { EmptyValues = 0 }).ToArray() }).ToArray();
        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            format = Format, connectorId = artifact.ConnectorId, connectorVersion = artifact.ConnectorVersion, objects = structural
        }, JsonOptions)))).ToLowerInvariant();
        return artifact.Format == Format && artifact.Fingerprint == expected;
    }

    private static PhysicalObject[] Normalize(IEnumerable<PhysicalObject> objects) => objects
        .OrderBy(item => item.Schema, StringComparer.Ordinal).ThenBy(item => item.Name, StringComparer.Ordinal)
        .Select(item => item with
        {
            Fields = item.Fields.OrderBy(field => field.Ordinal).ThenBy(field => field.Name, StringComparer.Ordinal).ToArray(),
            Keys = item.Keys.OrderBy(key => key.Name, StringComparer.Ordinal).ToArray(),
            Relationships = item.Relationships.OrderBy(relation => relation.Name, StringComparer.Ordinal).ToArray()
            ,SelectorProperties = item.SelectorProperties is null ? null : new SortedDictionary<string, string>(
                item.SelectorProperties.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal), StringComparer.Ordinal)
        }).ToArray();
}