using System.Collections;
using System.Collections.Immutable;

namespace ProofShift.Domain;

public readonly record struct ProjectId
{
    public Guid Value { get; }

    public ProjectId(Guid value) => Value = DomainGuard.Required(value, nameof(value));
}

public readonly record struct RunId
{
    public Guid Value { get; }

    public RunId(Guid value) => Value = DomainGuard.Required(value, nameof(value));
}

public readonly record struct SnapshotId
{
    public Guid Value { get; }

    public SnapshotId(Guid value) => Value = DomainGuard.Required(value, nameof(value));
}

public readonly record struct MigrationPlanId
{
    public Guid Value { get; }

    public MigrationPlanId(Guid value) => Value = DomainGuard.Required(value, nameof(value));
}

public readonly record struct MigrationGraphId
{
    public Guid Value { get; }

    public MigrationGraphId(Guid value) => Value = DomainGuard.Required(value, nameof(value));
}

public readonly record struct MigrationNodeId
{
    public Guid Value { get; }

    public MigrationNodeId(Guid value) => Value = DomainGuard.Required(value, nameof(value));
}

public readonly record struct MigrationEdgeId
{
    public Guid Value { get; }

    public MigrationEdgeId(Guid value) => Value = DomainGuard.Required(value, nameof(value));
}

public readonly record struct EvidenceId
{
    public Guid Value { get; }

    public EvidenceId(Guid value) => Value = DomainGuard.Required(value, nameof(value));
}

public readonly record struct ArtifactId
{
    public string Value { get; }

    public ArtifactId(string value) => Value = DomainGuard.Required(value, nameof(value));
}

public readonly record struct RuleId
{
    public string Value { get; }

    public RuleId(string value) => Value = DomainGuard.Required(value, nameof(value));
}

public readonly record struct ConnectorId
{
    public string Value { get; }

    public ConnectorId(string value) => Value = DomainGuard.Required(value, nameof(value));
}

public readonly record struct SystemId
{
    public string Value { get; }

    public SystemId(string value) => Value = DomainGuard.Required(value, nameof(value));
}

public readonly record struct StorageEndpointId
{
    public string Value { get; }

    public StorageEndpointId(string value) => Value = DomainGuard.Required(value, nameof(value));
}

public sealed class DomainList<T> : IReadOnlyList<T>, IEquatable<DomainList<T>>
{
    private readonly ImmutableArray<T> _items;

    public DomainList(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        _items = items.ToImmutableArray();
    }

    public int Count => _items.Length;

    public T this[int index] => _items[index];

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)_items).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public bool Equals(DomainList<T>? other) =>
        other is not null && _items.AsSpan().SequenceEqual(other._items.AsSpan());

    public override bool Equals(object? obj) => obj is DomainList<T> other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var item in _items)
        {
            hash.Add(item);
        }

        return hash.ToHashCode();
    }
}

public sealed class DomainDictionary<T> : IReadOnlyDictionary<string, T>, IEquatable<DomainDictionary<T>>
{
    private readonly ImmutableSortedDictionary<string, T> _items;

    public DomainDictionary(IEnumerable<KeyValuePair<string, T>> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var builder = ImmutableSortedDictionary.CreateBuilder<string, T>(StringComparer.Ordinal);
        foreach (var (key, value) in items)
        {
            builder.Add(DomainGuard.Required(key, nameof(items)), value);
        }

        _items = builder.ToImmutable();
    }

    public int Count => _items.Count;

    public IEnumerable<string> Keys => _items.Keys;

    public IEnumerable<T> Values => _items.Values;

    public T this[string key] => _items[key];

    public bool ContainsKey(string key) => _items.ContainsKey(key);

    public bool TryGetValue(string key, out T value) => _items.TryGetValue(key, out value!);

    public IEnumerator<KeyValuePair<string, T>> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public bool Equals(DomainDictionary<T>? other)
    {
        if (other is null || Count != other.Count)
        {
            return false;
        }

        return this.SequenceEqual(other);
    }

    public override bool Equals(object? obj) => obj is DomainDictionary<T> other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var (key, value) in _items)
        {
            hash.Add(key, StringComparer.Ordinal);
            hash.Add(value);
        }

        return hash.ToHashCode();
    }
}

internal static class DomainGuard
{
    public static Guid Required(Guid value, string parameterName) =>
        value == Guid.Empty
            ? throw new ArgumentException("Identifier must not be empty.", parameterName)
            : value;

    public static string Required(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value must not be empty.", parameterName);
        }

        return value.Trim();
    }

    public static ProjectId Required(ProjectId value, string parameterName) => new(Required(value.Value, parameterName));

    public static RunId Required(RunId value, string parameterName) => new(Required(value.Value, parameterName));

    public static SnapshotId Required(SnapshotId value, string parameterName) => new(Required(value.Value, parameterName));

    public static MigrationPlanId Required(MigrationPlanId value, string parameterName) => new(Required(value.Value, parameterName));

    public static MigrationGraphId Required(MigrationGraphId value, string parameterName) => new(Required(value.Value, parameterName));

    public static MigrationNodeId Required(MigrationNodeId value, string parameterName) => new(Required(value.Value, parameterName));

    public static MigrationEdgeId Required(MigrationEdgeId value, string parameterName) => new(Required(value.Value, parameterName));

    public static EvidenceId Required(EvidenceId value, string parameterName) => new(Required(value.Value, parameterName));

    public static ArtifactId Required(ArtifactId value, string parameterName) => new(Required(value.Value, parameterName));

    public static RuleId Required(RuleId value, string parameterName) => new(Required(value.Value, parameterName));

    public static ConnectorId Required(ConnectorId value, string parameterName) => new(Required(value.Value, parameterName));

    public static SystemId Required(SystemId value, string parameterName) => new(Required(value.Value, parameterName));

    public static StorageEndpointId Required(StorageEndpointId value, string parameterName) => new(Required(value.Value, parameterName));

    public static T NotNull<T>(T? value, string parameterName) where T : class =>
        value ?? throw new ArgumentNullException(parameterName);
}
