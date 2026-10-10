using System.Globalization;
using System.Runtime.CompilerServices;
using ProofShift.Connectors.Abstractions;

namespace ProofShift.Connectors.RemoteObjects;

/// <summary>Read-only local-filesystem transport over the same abstraction, so format connectors run unchanged on any transport.</summary>
public sealed class LocalFileStoreFactory : IRemoteObjectStoreFactory
{
    public string Transport => "filesystem";
    public RemoteStoreCapabilities Capabilities { get; } = new(true, false, false);
    public IReadOnlyDictionary<string, string> EndpointProperties { get; } = new Dictionary<string, string>(StringComparer.Ordinal) { ["root"] = "string-or-reference" };
    public IReadOnlyCollection<string> RequiredEndpointProperties { get; } = ["root"];

    public IRemoteObjectStore Create(ConnectorContext context) => new LocalFileObjectStore(ConnectorPathUtilities.ResolveRoot(context));
}

public sealed class LocalFileObjectStore(string root) : IRemoteObjectStore
{
    public string Provider => "filesystem";
    public string ScopeIdentity => "file://local-endpoint-root/";
    public RemoteStoreCapabilities Capabilities { get; } = new(true, false, false);

    public async IAsyncEnumerable<RemoteObjectInfo> ListAsync(string keyPrefix, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        var results = new List<RemoteObjectInfo>();
        var options = new EnumerationOptions { RecurseSubdirectories = false, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System };
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            foreach (var path in Directory.EnumerateFileSystemEntries(directory, "*", options))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.System)) != 0) continue;
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(path);
                    continue;
                }

                var key = ConnectorPathUtilities.NormalizeRelativePath(Path.GetRelativePath(root, path));
                if (!key.StartsWith(keyPrefix, StringComparison.Ordinal)) continue;
                var info = new FileInfo(path);
                results.Add(new RemoteObjectInfo(RemoteKeyRules.ValidateRelativeKey(key), info.Length, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)));
            }
        }

        results.Sort((a, b) => RemoteKeyRules.CompareUtf8(a.Key, b.Key));
        foreach (var result in results) yield return result;
    }

    public Task<RemoteObjectInfo> StatAsync(string key, string? versionId, CancellationToken cancellationToken)
    {
        if (versionId is not null) throw new RemoteStoreException(RemoteFailureKind.VersionUnavailable, "Filesystem objects are not versioned.");
        var path = Resolve(key);
        var info = new FileInfo(path);
        return info.Exists
            ? Task.FromResult(new RemoteObjectInfo(key, info.Length, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)))
            : throw new RemoteStoreException(RemoteFailureKind.NotFound, "Object was not found.");
    }

    public ValueTask<Stream> OpenReadAsync(RemoteObjectInfo info, long offset, long? length, CancellationToken cancellationToken)
    {
        var path = Resolve(info.Key);
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        stream.Seek(offset, SeekOrigin.Begin);
        return ValueTask.FromResult<Stream>(length is { } count ? new BoundedReadStream(stream, count) : stream);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private string Resolve(string key)
    {
        RemoteKeyRules.ValidateRelativeKey(key);
        return ConnectorPathUtilities.TryResolveContainedPath(root, key, out var path)
            ? path
            : throw new RemoteStoreException(RemoteFailureKind.ScopeEscape, "Object path escapes the endpoint root.");
    }

    private sealed class BoundedReadStream(Stream inner, long limit) : Stream
    {
        private long _remaining = limit;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_remaining <= 0) return 0;
            var read = inner.Read(buffer, offset, (int)Math.Min(count, _remaining));
            _remaining -= read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_remaining <= 0) return 0;
            var read = await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, _remaining)], cancellationToken).ConfigureAwait(false);
            _remaining -= read;
            return read;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
