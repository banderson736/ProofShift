using ProofShift.Connectors.Abstractions;
using ProofShift.Connectors.RemoteObjects;

namespace ProofShift.Connectors.RemoteObjects.Tests;

/// <summary>Deterministic in-memory transport with fault injection used to test the shared remote-object contract.</summary>
public sealed class InMemoryObjectStore : IRemoteObjectStore
{
    private readonly SortedDictionary<string, (byte[] Data, int Version)> _objects = new(StringComparer.Ordinal);
    public int FailNextOpens { get; set; }
    public int ListPageSize { get; set; } = 3;
    public int Pages { get; private set; }
    public int OpenCalls { get; private set; }
    public bool ReorderListing { get; set; }
    public bool TruncateReads { get; set; }
    public RemoteStoreCapabilities Capabilities { get; set; } = new(true, true, true);
    public string Provider => "memory";
    public string ScopeIdentity => "memory://scope/";

    public void Put(string key, byte[] data) =>
        _objects[key] = (data, _objects.TryGetValue(key, out var existing) ? existing.Version + 1 : 1);

    private RemoteObjectInfo Info(string key) => new(key, _objects[key].Data.Length,
        new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero), $"etag-{_objects[key].Version}", $"v{_objects[key].Version}");

    public async IAsyncEnumerable<RemoteObjectInfo> ListAsync(string keyPrefix,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var keys = _objects.Keys.Where(key => key.StartsWith(keyPrefix, StringComparison.Ordinal)).ToList();
        if (ReorderListing) keys.Reverse();
        for (var index = 0; index < keys.Count; index++)
        {
            if (index % ListPageSize == 0) Pages++;
            await Task.Yield();
            yield return Info(keys[index]);
        }
    }

    public Task<RemoteObjectInfo> StatAsync(string key, string? versionId, CancellationToken cancellationToken)
    {
        if (!_objects.ContainsKey(key)) throw new RemoteStoreException(RemoteFailureKind.NotFound, "missing");
        var info = Info(key);
        if (versionId is not null && versionId != info.VersionId)
            throw new RemoteStoreException(RemoteFailureKind.VersionUnavailable, "version gone");
        return Task.FromResult(info);
    }

    public ValueTask<Stream> OpenReadAsync(RemoteObjectInfo info, long offset, long? length, CancellationToken cancellationToken)
    {
        OpenCalls++;
        if (FailNextOpens > 0)
        {
            FailNextOpens--;
            throw new RemoteStoreException(RemoteFailureKind.Transient, "transient");
        }

        var data = _objects[info.Key].Data;
        var count = (int)Math.Min(length ?? data.Length - offset, data.Length - offset);
        if (TruncateReads && count > 1) count--;
        return ValueTask.FromResult<Stream>(new MemoryStream(data, (int)offset, count, writable: false));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class InMemoryFactory(InMemoryObjectStore store) : IRemoteObjectStoreFactory
{
    public string Transport => "memory";
    public RemoteStoreCapabilities Capabilities => store.Capabilities;
    public IReadOnlyDictionary<string, string> EndpointProperties { get; } = new Dictionary<string, string> { ["scope"] = "string" };
    public IReadOnlyCollection<string> RequiredEndpointProperties { get; } = ["scope"];
    public IRemoteObjectStore Create(ConnectorContext context) => store;
}
