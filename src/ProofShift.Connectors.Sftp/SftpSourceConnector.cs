using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using ProofShift.Connectors.Abstractions;
using ProofShift.Connectors.RemoteObjects;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace ProofShift.Connectors.Sftp;

/// <summary>SFTP read-only source. Host key fingerprint pinning is mandatory; symbolic links are never followed.</summary>
public sealed class SftpSourceConnector() : RemoteObjectSourceConnector("sftp", new SftpStoreFactory());

public sealed class SftpStoreFactory : IRemoteObjectStoreFactory
{
    public string Transport => "sftp";
    public RemoteStoreCapabilities Capabilities { get; } = new(true, false, false);

    public IReadOnlyDictionary<string, string> EndpointProperties { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["host"] = "string",
        ["port"] = "positive-integer-string",
        ["username"] = "string",
        ["remoteRoot"] = "string",
        ["hostKeyFingerprint"] = "string",
        ["authentication"] = "enum:password,private-key",
        ["password"] = "string-or-reference",
        ["privateKey"] = "string-or-reference",
        ["privateKeyPassphrase"] = "string-or-reference"
    };

    public IReadOnlyCollection<string> RequiredEndpointProperties { get; } = ["host", "username", "remoteRoot", "hostKeyFingerprint", "authentication"];

    public IRemoteObjectStore Create(ConnectorContext context)
    {
        string Value(string name, string fallback = "") => RemoteSettings.Optional(context, name, fallback);
        var host = Value("host");
        var fingerprint = Value("hostKeyFingerprint");
        if (host.Length == 0 || host.Contains('/', StringComparison.Ordinal) || host.Contains('@', StringComparison.Ordinal))
            throw new ConnectorConfigurationException(ConnectorIssueCodes.MissingConfiguration, "A bare SFTP host name is required.");
        if (!fingerprint.StartsWith("SHA256:", StringComparison.Ordinal) || fingerprint.Length < 20)
            throw new ConnectorConfigurationException(ConnectorIssueCodes.InsecureRemoteConfiguration,
                "SFTP requires a pinned host key fingerprint in the form SHA256:<base64>; trust-on-first-use is not supported.");
        var root = Value("remoteRoot");
        if (!root.StartsWith('/') || RemoteKeyRules.HasUnsafeSegment(root.TrimStart('/')))
            throw new ConnectorConfigurationException(ConnectorIssueCodes.MissingConfiguration, "SFTP remoteRoot must be an absolute POSIX path without dot segments.");
        var port = int.TryParse(Value("port", "22"), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed is > 0 and < 65536 ? parsed : 22;
        var username = Value("username");
        var methods = new List<AuthenticationMethod>();
        switch (Value("authentication"))
        {
            case "password":
                methods.Add(new PasswordAuthenticationMethod(username, RemoteSettings.RequiredSecret(context, "password")));
                break;
            case "private-key":
                var pem = RemoteSettings.RequiredSecret(context, "privateKey");
                var passphrase = RemoteSettings.TryGetSecret(context, "privateKeyPassphrase", out var phrase) ? phrase : null;
                try
                {
                    using var keyStream = new MemoryStream(Encoding.UTF8.GetBytes(pem));
                    methods.Add(new PrivateKeyAuthenticationMethod(username,
                        passphrase is null ? new PrivateKeyFile(keyStream) : new PrivateKeyFile(keyStream, passphrase)));
                }
                catch (Exception exception) when (exception is SshException or ArgumentException or IOException)
                {
                    throw new ConnectorConfigurationException(ConnectorIssueCodes.MissingConfiguration, "SFTP private key could not be loaded.");
                }

                break;
            default:
                throw new ConnectorConfigurationException(ConnectorIssueCodes.MissingConfiguration, "SFTP authentication must be password or private-key.");
        }

        var info = new ConnectionInfo(host, port, username, methods.ToArray()) { Timeout = TimeSpan.FromSeconds(30) };
        return new SftpObjectStore(new SftpClient(info), root.TrimEnd('/'), fingerprint, host, port);
    }
}

public sealed class SftpObjectStore(SftpClient client, string root, string expectedFingerprint, string host, int port) : IRemoteObjectStore
{
    private readonly HashSet<string> _verifiedDirectories = new(StringComparer.Ordinal);
    private bool _hostKeyMismatch;
    private int _disposed;

    public string Provider => "sftp";
    public string ScopeIdentity => $"sftp://{host}:{port.ToString(CultureInfo.InvariantCulture)}{root}/";
    public RemoteStoreCapabilities Capabilities { get; } = new(true, false, false);

    private void EnsureConnected()
    {
        if (client.IsConnected) return;
        client.HostKeyReceived += OnHostKey;
        try
        {
            client.Connect();
        }
        catch (SshAuthenticationException)
        {
            throw new RemoteStoreException(RemoteFailureKind.Authentication, "SFTP authentication failed.");
        }
        catch (SshConnectionException) when (_hostKeyMismatch)
        {
            throw new RemoteStoreException(RemoteFailureKind.HostKeyMismatch, "SFTP host key does not match the pinned fingerprint.");
        }
        catch (Exception exception) when (exception is SshException or System.Net.Sockets.SocketException or IOException or TimeoutException)
        {
            if (_hostKeyMismatch) throw new RemoteStoreException(RemoteFailureKind.HostKeyMismatch, "SFTP host key does not match the pinned fingerprint.");
            throw new RemoteStoreException(RemoteFailureKind.Transient, "SFTP connection failed.");
        }
    }

    private void OnHostKey(object? sender, HostKeyEventArgs args)
    {
        var actual = "SHA256:" + Convert.ToBase64String(SHA256.HashData(args.HostKey)).TrimEnd('=');
        args.CanTrust = CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(actual), Encoding.ASCII.GetBytes(expectedFingerprint.TrimEnd('=')));
        if (!args.CanTrust) _hostKeyMismatch = true;
    }

    public async IAsyncEnumerable<RemoteObjectInfo> ListAsync(string keyPrefix, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (keyPrefix.Length > 0) RemoteKeyRules.ValidateRelativeKey(keyPrefix.TrimEnd('/').Length == 0 ? "x" : keyPrefix.TrimEnd('/'));
        await Task.Yield();
        EnsureConnected();
        var startDirectory = keyPrefix.Length == 0 ? "" : keyPrefix[..(keyPrefix.LastIndexOf('/') + 1)].TrimEnd('/');
        var pending = new Stack<string>();
        pending.Push(startDirectory);
        var results = new List<RemoteObjectInfo>();
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            var path = directory.Length == 0 ? root : root + "/" + directory;
            List<Renci.SshNet.Sftp.ISftpFile> entries;
            try
            {
                entries = (await client.ListDirectoryAsync(path, cancellationToken).ToListAsync(cancellationToken).ConfigureAwait(false)).ToList();
            }
            catch (Exception exception) when (exception is SshException or IOException)
            {
                throw Map(exception);
            }

            foreach (var entry in entries.Where(entry => entry.Name is not ("." or "..")))
            {
                if (entry.IsSymbolicLink) continue; // never followed: a link cannot make an object appear outside the root
                var relative = directory.Length == 0 ? entry.Name : directory + "/" + entry.Name;
                if (entry.IsDirectory)
                {
                    pending.Push(relative);
                    continue;
                }

                if (!entry.IsRegularFile || !relative.StartsWith(keyPrefix, StringComparison.Ordinal)) continue;
                results.Add(new RemoteObjectInfo(RemoteKeyRules.ValidateRelativeKey(relative), entry.Length,
                    new DateTimeOffset(DateTime.SpecifyKind(entry.LastWriteTimeUtc, DateTimeKind.Utc))));
            }
        }

        // SFTP directory order is server-defined; sort to the contractual UTF-8 byte order.
        results.Sort((a, b) => RemoteKeyRules.CompareUtf8(a.Key, b.Key));
        foreach (var result in results) yield return result;
    }

    public Task<RemoteObjectInfo> StatAsync(string key, string? versionId, CancellationToken cancellationToken)
    {
        if (versionId is not null)
            throw new RemoteStoreException(RemoteFailureKind.VersionUnavailable, "SFTP does not support version pinning.");
        return RemoteRetryPolicy.Default.ExecuteAsync(token =>
        {
            token.ThrowIfCancellationRequested();
            EnsureConnected();
            try
            {
                var file = VerifiedEntry(key);
                return Task.FromResult(new RemoteObjectInfo(key, file.Length,
                    new DateTimeOffset(DateTime.SpecifyKind(file.LastWriteTimeUtc, DateTimeKind.Utc))));
            }
            catch (Exception exception) when (exception is SshException or IOException)
            {
                throw Map(exception);
            }
        }, cancellationToken);
    }

    private string CanonicalRoot() => _canonicalRoot ??= client.Get(root).FullName;
    private string? _canonicalRoot;

    private Renci.SshNet.Sftp.ISftpFile VerifiedEntry(string key)
    {
        RemoteKeyRules.ValidateRelativeKey(key);
        var segments = key.Split('/');
        var current = CanonicalRoot();
        for (var index = 0; index < segments.Length; index++)
        {
            current += "/" + segments[index];
            var last = index == segments.Length - 1;
            if (!last && _verifiedDirectories.Contains(current)) continue;
            var entry = client.Get(current);
            // SSH.NET resolves REALPATH before lstat, so a traversed link yields a different canonical name.
            if (entry.IsSymbolicLink || !string.Equals(entry.FullName, current, StringComparison.Ordinal))
                throw new RemoteStoreException(RemoteFailureKind.ScopeEscape, "Remote path traverses a symbolic link and was refused.");
            if (last)
            {
                if (!entry.IsRegularFile) throw new RemoteStoreException(RemoteFailureKind.NotFound, "Remote entry is not a regular file.");
                return entry;
            }

            if (!entry.IsDirectory) throw new RemoteStoreException(RemoteFailureKind.NotFound, "Remote path component is not a directory.");
            _verifiedDirectories.Add(current);
        }

        throw new RemoteStoreException(RemoteFailureKind.NotFound, "Remote object was not found.");
    }

    public ValueTask<Stream> OpenReadAsync(RemoteObjectInfo info, long offset, long? length, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureConnected();
        try
        {
            VerifiedEntry(info.Key);
            var stream = client.OpenRead(root + "/" + info.Key);
            if (offset > 0) stream.Seek(offset, SeekOrigin.Begin);
            return ValueTask.FromResult<Stream>(length is { } count ? new LimitedStream(stream, count) : stream);
        }
        catch (Exception exception) when (exception is SshException or IOException)
        {
            throw Map(exception);
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return ValueTask.CompletedTask;
        if (client.IsConnected) client.Disconnect();
        client.Dispose();
        return ValueTask.CompletedTask;
    }

    private static RemoteStoreException Map(Exception exception) => exception switch
    {
        Renci.SshNet.Common.SftpPathNotFoundException => new(RemoteFailureKind.NotFound, "SFTP path was not found."),
        Renci.SshNet.Common.SftpPermissionDeniedException => new(RemoteFailureKind.Authorization, "SFTP permission denied for the configured root."),
        _ => new(RemoteFailureKind.Transient, "SFTP operation failed.")
    };

    private sealed class LimitedStream(Stream inner, long limit) : Stream
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
