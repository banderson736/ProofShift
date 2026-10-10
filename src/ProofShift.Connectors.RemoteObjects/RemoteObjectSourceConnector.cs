using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ProofShift.Connectors.Abstractions;
using ProofShift.Domain;

namespace ProofShift.Connectors.RemoteObjects;

/// <summary>
/// Shared source connector for object/file transports (S3, Azure Blob, SFTP and any later provider).
/// Selector kind <c>object-pattern</c>; one artifact per matched object identified by its scope-relative key.
/// Observation is read-only; checkpoints materialize bytes through <see cref="ISourceBinaryContentResolver"/>.
/// </summary>
public class RemoteObjectSourceConnector : ICheckpointSourceConnector, ISourceBinaryContentResolver,
    IPhysicalDiscoveryConnector, IConnectorConfigurationSchemaProvider
{
    public const string SelectorKind = "object-pattern";
    private const string ReferencePrefix = "object-v1:";
    private readonly IRemoteObjectStoreFactory _factory;

    public RemoteObjectSourceConnector(string id, IRemoteObjectStoreFactory factory, string version = "0.10.0")
    {
        ArgumentNullException.ThrowIfNull(factory);
        Id = new ConnectorId(id);
        Version = version;
        _factory = factory;
    }

    public ConnectorId Id { get; }
    public string Version { get; }
    public SourceConsistencyGuarantee CheckpointConsistency => SourceConsistencyGuarantee.Observed;

    public ConnectorCapabilityDescriptor Capabilities => new(Id.Value, Version, true, true, true, true, false, true, true,
        "Observed", false, _factory.Transport, false, _factory.Capabilities.RangeRead, _factory.Capabilities.VersionPinning, true);

    public ConnectorConfigurationSchema ConfigurationSchema => new(Id.Value, SelectorKind,
        _factory.EndpointProperties, _factory.RequiredEndpointProperties,
        new Dictionary<string, string>(StringComparer.Ordinal) { ["pattern"] = "string" },
        new Dictionary<string, string>(StringComparer.Ordinal), [["pattern"]]);

    public async Task<SourceInspection> InspectAsync(ConnectorContext context, ArtifactSelector selector, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(selector);
        if (!string.Equals(selector.Kind, SelectorKind, StringComparison.OrdinalIgnoreCase))
            return Invalid(context, ConnectorIssueCodes.UnsupportedSelector, "Object connectors support object-pattern selectors only.");
        if (!TryGetPattern(selector, out var pattern))
            return Invalid(context, ConnectorIssueCodes.InvalidPhysicalIdentifier, "Object pattern is required and must remain relative to the endpoint scope.");
        if (!HasSupportedIdentity(selector))
            return Invalid(context, ConnectorIssueCodes.IdentityFieldNotFound, "Object artifact identity is the scope-relative key.");
        try
        {
            await using var store = _factory.Create(context);
            long files = 0, bytes = 0;
            await foreach (var info in MatchAsync(store, pattern, cancellationToken).ConfigureAwait(false))
            {
                files++;
                bytes = checked(bytes + info.Length);
            }

            return files == 0
                ? Invalid(context, ConnectorIssueCodes.SourceObjectNotFound, "Object pattern did not match any objects.")
                : new SourceInspection(SourceInspectionStatus.Valid, identityFields: ["relativePath"], estimatedRecords: files,
                    physicalObject: pattern, files: files, bytes: bytes);
        }
        catch (OperationCanceledException) { throw; }
        catch (ConnectorConfigurationException exception) { return Invalid(context, exception.Code, exception.Message); }
        catch (RemoteStoreException exception) { return Invalid(context, exception.Code, exception.Message); }
        catch
        {
            return new SourceInspection(SourceInspectionStatus.Failed, issues:
                [new ConnectorIssue(ConnectorIssueCodes.SourceReadFailed, ConnectorIssueSeverity.Error, "Remote object inspection failed.", context.NodeKey)]);
        }
    }

    public IAsyncEnumerable<RecordEnvelope> ReadAsync(ConnectorContext context, ArtifactSelector selector, ReadOptions options,
        CancellationToken cancellationToken) =>
        Translate(ReadCoreAsync(context, selector, options, cancellationToken), cancellationToken);

    public async IAsyncEnumerable<RecordEnvelope> ReadForCheckpointAsync(ConnectorContext context, ArtifactSelector selector,
        ReadOptions options, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var before = await InventoryAsync(context, selector, cancellationToken).ConfigureAwait(false);
        await foreach (var record in ReadAsync(context, selector, options, cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
            yield return record;
        var after = await InventoryAsync(context, selector, cancellationToken).ConfigureAwait(false);
        if (before != after)
            throw new ConnectorReadException(ConnectorIssueCodes.ArtifactChangedDuringCapture,
                "Matched remote objects changed while the checkpoint was being captured.");
    }

    public async Task<PhysicalDiscoveryArtifact> DiscoverAsync(ConnectorContext context,
        IReadOnlyCollection<ArtifactSelector> selectors, CancellationToken cancellationToken)
    {
        try
        {
            await using var store = _factory.Create(context);
            var groups = new SortedDictionary<string, (long Count, long Bytes)>(StringComparer.Ordinal);
            await foreach (var info in store.ListAsync(string.Empty, cancellationToken).ConfigureAwait(false))
            {
                RemoteKeyRules.ValidateRelativeKey(info.Key);
                var extension = Path.GetExtension(info.Key).ToLowerInvariant();
                var prior = groups.GetValueOrDefault(extension);
                groups[extension] = (prior.Count + 1, prior.Bytes + info.Length);
            }

            return PhysicalDiscovery.Create(context, Id.Value, Version, groups.Select(pair => new PhysicalObject("",
                string.IsNullOrEmpty(pair.Key) ? "**/extensionless" : "**/*" + pair.Key, SelectorKind, [], [], [], pair.Value.Count, pair.Value.Bytes)));
        }
        catch (RemoteStoreException exception)
        {
            throw new ConnectorReadException(exception.Code, exception.Message);
        }
    }

    public async ValueTask<Stream> OpenBinaryReadAsync(ConnectorContext context, ArtifactSelector selector,
        ArtifactReference artifact, BinaryReferenceValue binaryReference, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(binaryReference);
        if (artifact.SystemId.Value != context.SystemKey || artifact.EndpointId.Value != context.EndpointKey ||
            !TryDecodeReference(binaryReference.Reference, out var key, out var versionId) ||
            !string.Equals(key, artifact.Identity, StringComparison.Ordinal))
            throw new ConnectorReadException(ConnectorIssueCodes.SourceObjectNotFound, "Binary reference does not belong to the supplied source artifact.");
        IRemoteObjectStore? store = null;
        try
        {
            store = _factory.Create(context);
            var info = await RemoteRetryPolicy.Default.ExecuteAsync(token => store.StatAsync(key, versionId, token), cancellationToken).ConfigureAwait(false);
            if (info.Length != binaryReference.ContentLength)
                throw new RemoteStoreException(RemoteFailureKind.Changed, "Remote object length no longer matches the recorded binary reference.");
            var stream = await RemoteRetryPolicy.Default.ExecuteAsync(
                async token => await store.OpenReadAsync(info, 0, null, token).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
            return new StoreOwningStream(stream, store);
        }
        catch (RemoteStoreException exception)
        {
            if (store is not null) await store.DisposeAsync().ConfigureAwait(false);
            throw new ConnectorReadException(exception.Code, exception.Message);
        }
        catch
        {
            if (store is not null) await store.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async IAsyncEnumerable<RecordEnvelope> ReadCoreAsync(ConnectorContext context, ArtifactSelector selector,
        ReadOptions options, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(options);
        if (options.Partition.Count > 0)
            throw new ConnectorReadException(ConnectorIssueCodes.PartitioningUnsupported, "Partitioned source reads are not implemented yet.");
        if (!string.Equals(selector.Kind, SelectorKind, StringComparison.OrdinalIgnoreCase))
            throw new ConnectorReadException(ConnectorIssueCodes.UnsupportedSelector, "Object connectors support object-pattern selectors only.");
        if (!TryGetPattern(selector, out var pattern))
            throw new ConnectorReadException(ConnectorIssueCodes.InvalidPhysicalIdentifier, "Object pattern is required and must remain relative to the endpoint scope.");
        if (!HasSupportedIdentity(selector))
            throw new ConnectorReadException(ConnectorIssueCodes.IdentityFieldNotFound, "Object artifact identity is the scope-relative key.");

        await using var store = _factory.Create(context);
        await foreach (var listed in MatchAsync(store, pattern, cancellationToken).ConfigureAwait(false))
        {
            yield return await ReadObjectAsync(store, context, listed, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<RecordEnvelope> ReadObjectAsync(IRemoteObjectStore store, ConnectorContext context,
        RemoteObjectInfo listed, CancellationToken cancellationToken)
    {
        var retry = RemoteRetryPolicy.Default;
        var info = await retry.ExecuteAsync(token => store.StatAsync(listed.Key, listed.VersionId, token), cancellationToken).ConfigureAwait(false);
        var hash = await retry.ExecuteAsync(async token =>
        {
            await using var stream = await store.OpenReadAsync(info, 0, null, token).ConfigureAwait(false);
            using var incremental = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024];
            long total = 0;
            try
            {
                int read;
                while ((read = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                {
                    incremental.AppendData(buffer, 0, read);
                    total = checked(total + read);
                }
            }
            catch (IOException)
            {
                throw new RemoteStoreException(RemoteFailureKind.Transient, "Remote object read was interrupted.");
            }

            if (total != info.Length)
                throw new RemoteStoreException(RemoteFailureKind.Changed, "Remote object length changed while it was being read.");
            return Convert.ToHexString(incremental.GetHashAndReset()).ToLowerInvariant();
        }, cancellationToken).ConfigureAwait(false);

        var after = await retry.ExecuteAsync(token => store.StatAsync(listed.Key, info.VersionId, token), cancellationToken).ConfigureAwait(false);
        if (!info.SameState(after))
            throw new RemoteStoreException(RemoteFailureKind.Changed, "Remote object metadata changed while it was being read.");

        var key = info.Key;
        var values = new Dictionary<string, ValueNode>(StringComparer.Ordinal)
        {
            ["relativePath"] = new StringValue(key),
            ["size"] = new IntegerValue(info.Length),
            ["sha256"] = new StringValue(hash),
            ["content"] = new BinaryReferenceValue(EncodeReference(key, info.VersionId), info.Length, hash)
        };
        if (info.LastModified is { } modified) values["modifiedAt"] = new InstantValue(modified);
        var metadata = new List<KeyValuePair<string, string>>
        {
            new("observationKind", "read"), new("transport", store.Provider), new("scope", store.ScopeIdentity)
        };
        if (info.ETag is not null) metadata.Add(new("providerETag", info.ETag));
        if (info.VersionId is not null) metadata.Add(new("providerVersionId", info.VersionId));
        var artifact = new ArtifactReference(
            new ArtifactId(ArtifactIdentity.Create(context.SystemKey, context.EndpointKey, "file", key)),
            new SystemId(context.SystemKey), new StorageEndpointId(context.EndpointKey), "file", key);
        return new RecordEnvelope(artifact, context.SemanticType, values,
            new ProvenanceMetadata(Id, new StorageEndpointId(context.EndpointKey), key, DateTimeOffset.UtcNow, hash, metadata),
            temporal: info.LastModified is { } recorded ? new TemporalMetadata(recordedAt: recorded) : null);
    }

    public static async IAsyncEnumerable<RemoteObjectInfo> MatchAsync(IRemoteObjectStore store, string pattern,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var matcher = CreatePatternMatcher(pattern);
        var wildcard = pattern.IndexOfAny(['*', '?']);
        var literal = wildcard < 0 ? pattern : pattern[..wildcard];
        var prefix = literal[..(literal.LastIndexOf('/') + 1)];
        string? previous = null;
        await foreach (var info in store.ListAsync(prefix, cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            RemoteKeyRules.ValidateRelativeKey(info.Key);
            if (previous is not null && RemoteKeyRules.CompareUtf8(previous, info.Key) >= 0)
                throw new ConnectorReadException(ConnectorIssueCodes.NonDeterministicIdentity,
                    "Transport listing was not strictly ordered; deterministic identity cannot be guaranteed.");
            previous = info.Key;
            if (matcher.IsMatch(info.Key)) yield return info;
        }
    }

    private async Task<string> InventoryAsync(ConnectorContext context, ArtifactSelector selector, CancellationToken cancellationToken)
    {
        if (!TryGetPattern(selector, out var pattern))
            throw new ConnectorReadException(ConnectorIssueCodes.InvalidPhysicalIdentifier, "Object pattern is invalid.");
        try
        {
            await using var store = _factory.Create(context);
            return await InventoryAsync(store, pattern, cancellationToken).ConfigureAwait(false);
        }
        catch (RemoteStoreException exception)
        {
            throw new ConnectorReadException(exception.Code, exception.Message);
        }
    }

    /// <summary>Digest of the key, length, modification time, entity tag and version of every matched object.</summary>
    public static async Task<string> InventoryAsync(IRemoteObjectStore store, string pattern, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await foreach (var info in MatchAsync(store, pattern, cancellationToken).ConfigureAwait(false))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(info.CanonicalState));
            hash.AppendData("\n"u8);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }
    private static async IAsyncEnumerable<T> Translate<T>(IAsyncEnumerable<T> source,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var enumerator = source.GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            bool has;
            try
            {
                has = await enumerator.MoveNextAsync().ConfigureAwait(false);
            }
            catch (RemoteStoreException exception) { throw new ConnectorReadException(exception.Code, exception.Message); }
            catch (ConnectorConfigurationException exception) { throw new ConnectorReadException(exception.Code, exception.Message); }
            catch (Exception exception) when (exception is not (OperationCanceledException or ConnectorReadException))
            {
                throw new ConnectorReadException(ConnectorIssueCodes.SourceReadFailed, "Remote object read failed.");
            }

            if (!has) yield break;
            yield return enumerator.Current;
        }
    }

    public static bool TryGetPattern(ArtifactSelector selector, out string pattern)
    {
        if (!selector.Properties.TryGetValue("pattern", out pattern!) || string.IsNullOrWhiteSpace(pattern) ||
            pattern.StartsWith('/') || pattern.Contains('\\', StringComparison.Ordinal) || RemoteKeyRules.HasUnsafeSegment(pattern))
        {
            pattern = string.Empty;
            return false;
        }

        return true;
    }

    private static bool HasSupportedIdentity(ArtifactSelector selector) =>
        selector.IdentityFields.Count == 0 || selector.IdentityFields.Count == 1 &&
        string.Equals(selector.IdentityFields[0], "relativePath", StringComparison.Ordinal);

    public static string EncodeReference(string key, string? versionId)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string?> { ["k"] = key, ["v"] = versionId });
        return ReferencePrefix + Convert.ToBase64String(json).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static bool TryDecodeReference(string reference, out string key, out string? versionId)
    {
        key = string.Empty;
        versionId = null;
        if (!reference.StartsWith(ReferencePrefix, StringComparison.Ordinal)) return false;
        try
        {
            var encoded = reference[ReferencePrefix.Length..].Replace('-', '+').Replace('_', '/');
            encoded += new string('=', (4 - encoded.Length % 4) % 4);
            using var document = JsonDocument.Parse(Convert.FromBase64String(encoded));
            key = document.RootElement.GetProperty("k").GetString() ?? string.Empty;
            versionId = document.RootElement.GetProperty("v").GetString();
            RemoteKeyRules.ValidateRelativeKey(key);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static Regex CreatePatternMatcher(string pattern)
    {
        var escaped = Regex.Escape(pattern)
            .Replace("\\*\\*/", "(?:.*/)?", StringComparison.Ordinal)
            .Replace("\\*\\*", ".*", StringComparison.Ordinal)
            .Replace("\\*", "[^/]*", StringComparison.Ordinal)
            .Replace("\\?", "[^/]", StringComparison.Ordinal);
        return new Regex($"^{escaped}$", RegexOptions.CultureInvariant);
    }

    private static SourceInspection Invalid(ConnectorContext context, string code, string message) =>
        new(SourceInspectionStatus.Invalid, issues: [new ConnectorIssue(code, ConnectorIssueSeverity.Error, message, context.NodeKey)]);

    private sealed class StoreOwningStream(Stream inner, IRemoteObjectStore store) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            await store.DisposeAsync().ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                store.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }

            base.Dispose(disposing);
        }
    }
}
