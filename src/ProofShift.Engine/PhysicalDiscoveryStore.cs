using System.Security.Cryptography;
using System.Text.Json;
using ProofShift.Connectors.Abstractions;

namespace ProofShift.Engine;

public static class PhysicalDiscoveryStore
{
    public static async Task SaveAsync(string directory, PhysicalDiscoveryArtifact artifact, CancellationToken cancellationToken)
    {
        if (!PhysicalDiscovery.Verify(artifact)) throw new InvalidDataException("Discovery fingerprint is invalid.");
        if (File.Exists(directory) || Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
            throw new IOException("Discovery destination must be new or empty; no existing artifact was overwritten.");
        Directory.CreateDirectory(directory);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(artifact, PhysicalDiscovery.JsonOptions);
        await File.WriteAllBytesAsync(Path.Combine(directory, "physical-model.json"), bytes, cancellationToken).ConfigureAwait(false);
        var manifest = new DiscoveryManifest(PhysicalDiscovery.Format, artifact.ConnectorId, artifact.ConnectorVersion,
            artifact.SystemId, artifact.EndpointId, artifact.ObservedAt, artifact.Fingerprint,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        await File.WriteAllTextAsync(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(manifest,
            PhysicalDiscovery.JsonOptions), cancellationToken).ConfigureAwait(false);
    }

    public static async Task<PhysicalDiscoveryArtifact> ReadAsync(string directory, CancellationToken cancellationToken)
    {
        var manifest = JsonSerializer.Deserialize<DiscoveryManifest>(await File.ReadAllTextAsync(Path.Combine(directory, "manifest.json"),
            cancellationToken).ConfigureAwait(false), PhysicalDiscovery.JsonOptions) ?? throw new InvalidDataException("Discovery manifest is empty.");
        var bytes = await File.ReadAllBytesAsync(Path.Combine(directory, "physical-model.json"), cancellationToken).ConfigureAwait(false);
        var artifact = JsonSerializer.Deserialize<PhysicalDiscoveryArtifact>(bytes, PhysicalDiscovery.JsonOptions)
            ?? throw new InvalidDataException("Discovery physical model is empty.");
        if (manifest.Format != PhysicalDiscovery.Format || !string.Equals(manifest.PayloadHash, Convert.ToHexString(SHA256.HashData(bytes)), StringComparison.OrdinalIgnoreCase) ||
            manifest.Fingerprint != artifact.Fingerprint || manifest.SystemId != artifact.SystemId || manifest.EndpointId != artifact.EndpointId ||
            manifest.ConnectorId != artifact.ConnectorId || manifest.ConnectorVersion != artifact.ConnectorVersion ||
            manifest.ObservedAt != artifact.ObservedAt || !PhysicalDiscovery.Verify(artifact))
            throw new InvalidDataException("Discovery artifact integrity or binding is invalid.");
        return artifact;
    }

    private sealed record DiscoveryManifest(string Format, string ConnectorId, string ConnectorVersion,
        string SystemId, string EndpointId, DateTimeOffset ObservedAt, string Fingerprint, string PayloadHash);
}

public sealed record PhysicalDrift(string Kind, string ObjectName, string? Field);

public static class PhysicalDiscoveryDiff
{
    public static IReadOnlyList<PhysicalDrift> Compare(PhysicalDiscoveryArtifact before, PhysicalDiscoveryArtifact after)
    {
        if (!PhysicalDiscovery.Verify(before) || !PhysicalDiscovery.Verify(after)) throw new InvalidDataException("Discovery fingerprint is invalid.");
        var result = new List<PhysicalDrift>();
        var left = before.Objects.ToDictionary(item => $"{item.Schema}.{item.Name}", StringComparer.Ordinal);
        var right = after.Objects.ToDictionary(item => $"{item.Schema}.{item.Name}", StringComparer.Ordinal);
        foreach (var name in left.Keys.Union(right.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            if (!left.TryGetValue(name, out var oldObject)) { result.Add(new("object-added", name, null)); continue; }
            if (!right.TryGetValue(name, out var newObject)) { result.Add(new("object-removed", name, null)); continue; }
            var oldFields = oldObject.Fields.ToDictionary(field => field.Name, StringComparer.Ordinal);
            var newFields = newObject.Fields.ToDictionary(field => field.Name, StringComparer.Ordinal);
            foreach (var field in oldFields.Keys.Union(newFields.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                if (!oldFields.TryGetValue(field, out var oldField)) { result.Add(new("field-added", name, field)); continue; }
                if (!newFields.TryGetValue(field, out var newField)) { result.Add(new("field-removed", name, field)); continue; }
                if (oldField.NativeType != newField.NativeType) result.Add(new("field-type-changed", name, field));
                if (oldField.IsNullable != newField.IsNullable) result.Add(new("field-nullability-changed", name, field));
            }
            if (JsonSerializer.Serialize(oldObject.Keys.Where(key => key.Primary)) != JsonSerializer.Serialize(newObject.Keys.Where(key => key.Primary)))
                result.Add(new("primary-key-changed", name, null));
            if (JsonSerializer.Serialize(oldObject.Keys.Where(key => !key.Primary)) != JsonSerializer.Serialize(newObject.Keys.Where(key => !key.Primary)))
                result.Add(new("unique-constraint-changed", name, null));
            if (JsonSerializer.Serialize(oldObject.Relationships) != JsonSerializer.Serialize(newObject.Relationships))
                result.Add(new("foreign-key-changed", name, null));
        }
        return result;
    }
}