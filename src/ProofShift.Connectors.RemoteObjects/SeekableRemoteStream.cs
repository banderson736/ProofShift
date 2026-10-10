using System.Buffers;

namespace ProofShift.Connectors.RemoteObjects;

/// <summary>
/// Read-only, seekable view over a remote object built from bounded range reads. Memory is limited to one block.
/// Reads are pinned to the <see cref="RemoteObjectInfo"/> (version / entity tag) supplied at construction.
/// </summary>
public sealed class SeekableRemoteStream : Stream
{
    private readonly IRemoteObjectStore _store;
    private readonly RemoteObjectInfo _info;
    private readonly RemoteRetryPolicy _retry;
    private readonly int _blockSize;
    private byte[]? _block;
    private int _blockLength;
    private long _blockStart = -1;
    private long _position;

    public long RangeRequests { get; private set; }
    public long BytesFetched { get; private set; }

    public SeekableRemoteStream(IRemoteObjectStore store, RemoteObjectInfo info, int blockSize = 1024 * 1024,
        RemoteRetryPolicy? retry = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(info);
        ArgumentOutOfRangeException.ThrowIfLessThan(blockSize, 1024);
        if (!store.Capabilities.RangeRead)
            throw new RemoteStoreException(RemoteFailureKind.Failed, "The transport does not support ranged reads.");
        _store = store;
        _info = info;
        _blockSize = blockSize;
        _retry = retry ?? RemoteRetryPolicy.Default;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _info.Length;
    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _info.Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        if (target < 0) throw new IOException("Seek before the start of the object.");
        _position = target;
        return _position;
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty || _position >= _info.Length) return 0;
        if (_block is null || _position < _blockStart || _position >= _blockStart + _blockLength)
            await FillAsync(cancellationToken).ConfigureAwait(false);
        var available = (int)(_blockStart + _blockLength - _position);
        var copy = Math.Min(available, buffer.Length);
        _block.AsSpan((int)(_position - _blockStart), copy).CopyTo(buffer.Span);
        _position += copy;
        return copy;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private async Task FillAsync(CancellationToken cancellationToken)
    {
        // Reads inside the last block fetch that whole tail so footer-style access needs one request.
        var start = Math.Min(_position, Math.Max(0, _info.Length - _blockSize));
        var length = (int)Math.Min(_blockSize, _info.Length - start);
        _block ??= new byte[_blockSize];
        var buffer = _block;
        var read = await _retry.ExecuteAsync(async token =>
        {
            Stream stream;
            try
            {
                stream = await _store.OpenReadAsync(_info, start, length, token).ConfigureAwait(false);
            }
            catch (IOException)
            {
                throw new RemoteStoreException(RemoteFailureKind.Transient, "Remote range read failed.");
            }

            await using (stream.ConfigureAwait(false))
            {
                var total = 0;
                try
                {
                    while (total < length)
                    {
                        var count = await stream.ReadAsync(buffer.AsMemory(total, length - total), token).ConfigureAwait(false);
                        if (count == 0) break;
                        total += count;
                    }
                }
                catch (IOException)
                {
                    throw new RemoteStoreException(RemoteFailureKind.Transient, "Remote range read was interrupted.");
                }

                return total;
            }
        }, cancellationToken).ConfigureAwait(false);
        if (read != length)
            throw new RemoteStoreException(RemoteFailureKind.Changed, "Remote object returned fewer bytes than its recorded length.");
        RangeRequests++;
        BytesFetched += read;
        _blockStart = start;
        _blockLength = read;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        _block = null;
        base.Dispose(disposing);
    }
}
