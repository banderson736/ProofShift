using System.Globalization;
using System.Text;
using ProofShift.Connectors.Abstractions;

namespace ProofShift.Connectors.RemoteObjects;

/// <summary>Provider-neutral failure classes. Messages never contain provider text, endpoints with credentials, or secret values.</summary>
public enum RemoteFailureKind
{
    Authentication,
    Authorization,
    NotFound,
    ScopeEscape,
    HostKeyMismatch,
    Transient,
    Changed,
    VersionUnavailable,
    Integrity,
    Insecure,
    Failed
}

public sealed class RemoteStoreException : Exception
{
    public RemoteFailureKind Kind { get; }

    public RemoteStoreException(RemoteFailureKind kind, string message) : base(message) => Kind = kind;

    public string Code => Kind switch
    {
        RemoteFailureKind.Authentication => ConnectorIssueCodes.RemoteAuthenticationFailed,
        RemoteFailureKind.Authorization => ConnectorIssueCodes.RemoteAuthorizationFailed,
        RemoteFailureKind.NotFound => ConnectorIssueCodes.SourceObjectNotFound,
        RemoteFailureKind.ScopeEscape => ConnectorIssueCodes.RemoteScopeEscape,
        RemoteFailureKind.HostKeyMismatch => ConnectorIssueCodes.RemoteHostKeyMismatch,
        RemoteFailureKind.Transient => ConnectorIssueCodes.RemoteTransientFailure,
        RemoteFailureKind.Changed => ConnectorIssueCodes.ArtifactChangedDuringCapture,
        RemoteFailureKind.VersionUnavailable => ConnectorIssueCodes.RemoteVersionUnavailable,
        RemoteFailureKind.Integrity => ConnectorIssueCodes.RemoteIntegrityMismatch,
        RemoteFailureKind.Insecure => ConnectorIssueCodes.InsecureRemoteConfiguration,
        _ => ConnectorIssueCodes.SourceReadFailed
    };
}

/// <summary>
/// One remote object. <see cref="Key"/> is relative to the configured endpoint scope and uses '/' separators.
/// <see cref="ETag"/> and <see cref="VersionId"/> are provider metadata: they are never treated as content hashes.
/// ProofShift computes SHA-256 over the bytes it actually reads.
/// </summary>
public sealed record RemoteObjectInfo(string Key, long Length, DateTimeOffset? LastModified = null,
    string? ETag = null, string? VersionId = null)
{
    public bool SameState(RemoteObjectInfo other) => Key == other.Key && Length == other.Length &&
        LastModified == other.LastModified && ETag == other.ETag && VersionId == other.VersionId;

    public string CanonicalState => string.Join('\n', Key, Length.ToString(CultureInfo.InvariantCulture),
        LastModified?.UtcTicks.ToString(CultureInfo.InvariantCulture) ?? "", ETag ?? "", VersionId ?? "");
}

public sealed record RemoteStoreCapabilities(bool RangeRead, bool VersionPinning, bool ConditionalRead);

/// <summary>
/// Transport abstraction. Implementations must be read-only: they expose no write, delete or rename operation.
/// Listing yields every object under the scope, ordered by the UTF-8 byte order of the key, streamed in provider pages.
/// </summary>
public interface IRemoteObjectStore : IAsyncDisposable
{
    string Provider { get; }
    /// <summary>Canonical, non-secret location of the scope, for provenance only (for example s3://bucket/prefix/).</summary>
    string ScopeIdentity { get; }
    RemoteStoreCapabilities Capabilities { get; }

    IAsyncEnumerable<RemoteObjectInfo> ListAsync(string keyPrefix, CancellationToken cancellationToken);
    Task<RemoteObjectInfo> StatAsync(string key, string? versionId, CancellationToken cancellationToken);
    /// <summary>Opens a forward-only stream of <paramref name="length"/> bytes (or to the end) starting at <paramref name="offset"/>.
    /// When the provider supports it the read is pinned to the version/entity tag in <paramref name="info"/>.</summary>
    ValueTask<Stream> OpenReadAsync(RemoteObjectInfo info, long offset, long? length, CancellationToken cancellationToken);
}

public interface IRemoteObjectStoreFactory
{
    string Transport { get; }
    RemoteStoreCapabilities Capabilities { get; }
    IReadOnlyDictionary<string, string> EndpointProperties { get; }
    IReadOnlyCollection<string> RequiredEndpointProperties { get; }
    IRemoteObjectStore Create(ConnectorContext context);
}

public static class RemoteKeyRules
{
    /// <summary>Normalizes a configured scope prefix: no leading '/', trailing '/' when non-empty, no dot segments.</summary>
    public static string NormalizePrefix(string? prefix)
    {
        var value = (prefix ?? string.Empty).Replace('\\', '/').Trim();
        value = value.TrimStart('/');
        if (value.Length == 0) return string.Empty;
        if (HasUnsafeSegment(value))
            throw new RemoteStoreException(RemoteFailureKind.ScopeEscape, "Configured prefix contains an unsafe path segment.");
        return value.EndsWith('/') ? value : value + "/";
    }

    public static bool HasUnsafeSegment(string key) =>
        key.Split('/').Any(segment => segment is "." or "..") || key.Contains('\0', StringComparison.Ordinal);

    /// <summary>Validates a scope-relative key (from a selector, reference, or listing).</summary>
    public static string ValidateRelativeKey(string key)
    {
        if (string.IsNullOrEmpty(key) || key.StartsWith('/') || key.Contains('\\', StringComparison.Ordinal) || HasUnsafeSegment(key))
            throw new RemoteStoreException(RemoteFailureKind.ScopeEscape, "Object key is empty, rooted, or contains an unsafe path segment.");
        return key;
    }

    /// <summary>Maps a provider key to a scope-relative key, rejecting anything outside the prefix boundary.</summary>
    public static string ToRelative(string normalizedPrefix, string fullKey)
    {
        if (!fullKey.StartsWith(normalizedPrefix, StringComparison.Ordinal) || fullKey.Length == normalizedPrefix.Length)
            throw new RemoteStoreException(RemoteFailureKind.ScopeEscape, "Provider returned an object outside the configured scope.");
        return ValidateRelativeKey(fullKey[normalizedPrefix.Length..]);
    }

    public static string ToFull(string normalizedPrefix, string relativeKey) => normalizedPrefix + ValidateRelativeKey(relativeKey);

    /// <summary>Compares keys by UTF-8 byte order, the order all transports must list in.</summary>
    public static int CompareUtf8(string left, string right)
    {
        var a = Encoding.UTF8.GetBytes(left);
        var b = Encoding.UTF8.GetBytes(right);
        return a.AsSpan().SequenceCompareTo(b);
    }
}

/// <summary>Bounded, cancellation-aware retry for classified transient failures only.</summary>
public sealed class RemoteRetryPolicy
{
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public int MaxAttempts { get; }
    public TimeSpan InitialDelay { get; }
    public TimeSpan MaxDelay { get; }

    public RemoteRetryPolicy(int maxAttempts = 4, TimeSpan? initialDelay = null, TimeSpan? maxDelay = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);
        MaxAttempts = maxAttempts;
        InitialDelay = initialDelay ?? TimeSpan.FromMilliseconds(200);
        MaxDelay = maxDelay ?? TimeSpan.FromSeconds(5);
        _delay = delay ?? Task.Delay;
    }

    public static RemoteRetryPolicy Default { get; } = new();

    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        var delay = InitialDelay;
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await operation(cancellationToken).ConfigureAwait(false);
            }
            catch (RemoteStoreException exception) when (exception.Kind == RemoteFailureKind.Transient && attempt < MaxAttempts)
            {
                await _delay(delay, cancellationToken).ConfigureAwait(false);
                delay = TimeSpan.FromTicks(Math.Min(MaxDelay.Ticks, delay.Ticks * 2));
            }
        }
    }
}

public static class RemoteSettings
{
    public static string Optional(ConnectorContext context, string name, string fallback = "") =>
        context.Configuration.TryGet(name, out var setting) ? setting.UseValue(value => value) : fallback;

    /// <summary>Credential material must arrive through a secret reference, never as an inline or plain-environment value.</summary>
    public static string RequiredSecret(ConnectorContext context, string name)
    {
        var setting = context.Configuration.GetRequired(name);
        if (!setting.IsSecret)
            throw new ConnectorConfigurationException(ConnectorIssueCodes.InsecureRemoteConfiguration,
                $"Endpoint property '{name}' carries credential material and must be supplied as a secret reference.");
        return setting.UseValue(value => value);
    }

    public static bool TryGetSecret(ConnectorContext context, string name, out string value)
    {
        value = string.Empty;
        if (!context.Configuration.TryGet(name, out _)) return false;
        value = RequiredSecret(context, name);
        return true;
    }
}
