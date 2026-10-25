using System.Collections.ObjectModel;
using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using ProofShift.Domain;
using ProofShift.Recovery;
using ProofShift.Reporting;

namespace ProofShift.DemoScenarios;

public interface IDemoScenario
{
    string Id { get; }
    string DisplayName { get; }
    Task<DemoScenarioExecutionResult> ExecuteAsync(DemoScenarioExecutionContext context);
}

public sealed record DemoScenarioExecutionResult(JsonElement Data, string HumanReadable)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static DemoScenarioExecutionResult Create<T>(T data, string humanReadable) =>
        new(JsonSerializer.SerializeToElement(data, JsonOptions), humanReadable);
}

public sealed record DemoScenarioExecutionContext(
    string WorkspaceDirectory,
    string ResultFilePath,
    string ContentRootDirectory,
    CancellationToken CancellationToken,
    bool PreserveArtifacts = false)
{
    private static readonly AsyncLocal<CancellationToken?> ActiveCancellationToken = new();

    public static CancellationToken CurrentCancellationToken =>
        ActiveCancellationToken.Value ??
        throw new InvalidOperationException("No demo scenario execution context is active.");

    public IDisposable Activate()
    {
        var previous = ActiveCancellationToken.Value;
        ActiveCancellationToken.Value = CancellationToken;
        return new ExecutionScope(previous);
    }

    private sealed class ExecutionScope(CancellationToken? previous) : IDisposable
    {
        public void Dispose() => ActiveCancellationToken.Value = previous;
    }
}

public sealed class DemoScenarioRegistry
{
    private readonly IReadOnlyDictionary<string, IDemoScenario> _scenarios;

    public DemoScenarioRegistry(IEnumerable<IDemoScenario> scenarios)
    {
        ArgumentNullException.ThrowIfNull(scenarios);
        var registered = new SortedDictionary<string, IDemoScenario>(StringComparer.Ordinal);
        foreach (var scenario in scenarios)
        {
            ArgumentNullException.ThrowIfNull(scenario);
            if (string.IsNullOrWhiteSpace(scenario.Id))
                throw new ArgumentException("Demo scenario IDs cannot be empty.", nameof(scenarios));
            if (!registered.TryAdd(scenario.Id, scenario))
                throw new ArgumentException($"Duplicate demo scenario ID '{scenario.Id}'.", nameof(scenarios));
        }
        _scenarios = new ReadOnlyDictionary<string, IDemoScenario>(registered);
    }

    public IReadOnlyList<IDemoScenario> Scenarios => _scenarios.Values.ToArray();

    public IDemoScenario GetRequired(string id) =>
        _scenarios.TryGetValue(id, out var scenario)
            ? scenario
            : throw new KeyNotFoundException($"Unknown demo scenario '{id}'.");
}

public sealed class DemoScenarioUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);

internal static class DemoScenarioWorkspace
{
    public static IDisposable BeginFixtureCleanup(string path, bool preserveArtifacts) =>
        new FixtureCleanup(path, !preserveArtifacts);

    public static async Task StageCheckpointForCliAsync(string artifactDirectory, DryRunId dryRunId,
        CancellationToken cancellationToken)
    {
        var recoveryStore = new FileSystemRecoveryArtifactStore(
            Path.Combine(artifactDirectory, ".proofshift", "recovery"));
        var recovery = await recoveryStore.ReadSummaryAsync(dryRunId, cancellationToken).ConfigureAwait(false);
        var checkpointId = recovery.CheckpointId.Value.ToString("N");
        var destination = Path.Combine(artifactDirectory, ".proofshift", "checkpoints", checkpointId);
        if (Directory.Exists(destination))
        {
            if (!File.Exists(Path.Combine(destination, "manifest.json")))
                throw new InvalidDataException($"Staged checkpoint directory has no manifest: {destination}");
            return;
        }

        CopyDirectory(Path.Combine(artifactDirectory, "checkpoints", checkpointId), destination);
    }

    public static void CleanupFixtureRoot(string path, bool deleteOnDispose)
    {
        if (!Directory.Exists(path)) return;
        if (deleteOnDispose)
            Directory.Delete(path, recursive: true);
    }

    private sealed class FixtureCleanup(string path, bool deleteOnDispose) : IDisposable
    {
        public void Dispose() => CleanupFixtureRoot(path, deleteOnDispose);
    }

    private static void CopyDirectory(string source, string destination)
    {
        if (!Directory.Exists(source))
            throw new DirectoryNotFoundException($"Persisted CLI artifact directory is missing: {source}");
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        foreach (var directory in Directory.EnumerateDirectories(source))
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }
}

internal static class ScenarioInvariant
{
    public static void Equal<T>(T expected, T actual)
    {
        if (expected is IDictionary expectedDictionary && actual is IDictionary actualDictionary)
        {
            if (expectedDictionary.Count != actualDictionary.Count)
                throw new InvalidOperationException("The demo scenario produced an unexpected dictionary.");
            foreach (DictionaryEntry entry in expectedDictionary)
            {
                if (!actualDictionary.Contains(entry.Key) ||
                    !Equals(entry.Value, actualDictionary[entry.Key]))
                    throw new InvalidOperationException("The demo scenario produced an unexpected dictionary.");
            }
            return;
        }

        if (expected is IEnumerable expectedItems && actual is IEnumerable actualItems &&
            expected is not string && actual is not string)
        {
            var expectedValues = expectedItems.Cast<object?>().ToArray();
            var actualValues = actualItems.Cast<object?>().ToArray();
            if (!expectedValues.SequenceEqual(actualValues))
                throw new InvalidOperationException("The demo scenario produced an unexpected value sequence.");
            return;
        }

        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException("The demo scenario produced an unexpected value.");
    }

    public static void Equal<T>(T expected, T actual, IEqualityComparer<T> comparer)
    {
        if (!comparer.Equals(expected, actual))
            throw new InvalidOperationException("The demo scenario produced an unexpected value.");
    }

    public static void NotEqual<T>(T unexpected, T actual)
    {
        if (EqualityComparer<T>.Default.Equals(unexpected, actual))
            throw new InvalidOperationException("The demo scenario produced a duplicate run identity.");
    }

    public static void True(bool condition, string? message = null)
    {
        if (!condition)
            throw new InvalidOperationException(message ?? "A demo scenario invariant was not satisfied.");
    }

    public static void False(bool condition)
    {
        if (condition)
            throw new InvalidOperationException("A demo scenario invariant was not satisfied.");
    }

    public static void NotNull([NotNull] object? value)
    {
        if (value is null)
            throw new InvalidOperationException("A required demo scenario result was missing.");
    }

    public static void Contains<T>(IEnumerable<T> values, Func<T, bool> predicate)
    {
        if (!values.Any(predicate))
            throw new InvalidOperationException("A required demo scenario result was missing.");
    }

    public static void Contains<T>(T expected, IEnumerable<T> values)
    {
        if (!values.Contains(expected))
            throw new InvalidOperationException("A required demo scenario result was missing.");
    }

    public static void DoesNotContain<T>(IEnumerable<T> values, Func<T, bool> predicate)
    {
        if (values.Any(predicate))
            throw new InvalidOperationException("The demo scenario returned an unexpected result.");
    }

    public static void DoesNotContain<T>(T unexpected, IEnumerable<T> values)
    {
        if (values.Contains(unexpected))
            throw new InvalidOperationException("The demo scenario returned an unexpected result.");
    }

    public static void Contains(string expectedSubstring, string actual) =>
        Contains(expectedSubstring, actual, StringComparison.Ordinal);

    public static void Contains(string expectedSubstring, string actual, StringComparison comparison)
    {
        if (!actual.Contains(expectedSubstring, comparison))
            throw new InvalidOperationException("The demo scenario report omitted a required summary.");
    }

    public static void DoesNotContain(string unexpectedSubstring, string actual, StringComparison comparison)
    {
        if (actual.Contains(unexpectedSubstring, comparison))
            throw new InvalidOperationException("The demo scenario report included an unsupported domain term.");
    }

    public static void DoesNotContain(string unexpectedSubstring, string actual) =>
        DoesNotContain(unexpectedSubstring, actual, StringComparison.Ordinal);

    public static void All<T>(IEnumerable<T> values, Action<T> assertion)
    {
        foreach (var value in values) assertion(value);
    }

    public static void Empty<T>(IEnumerable<T> values)
    {
        if (values.Any())
            throw new InvalidOperationException("The demo scenario returned an unexpected non-empty collection.");
    }

    public static void NotEmpty<T>(IEnumerable<T> values)
    {
        if (!values.Any())
            throw new InvalidOperationException("The demo scenario omitted required data.");
    }

    public static void InRange<T>(T actual, T low, T high) where T : IComparable<T>
    {
        if (actual.CompareTo(low) < 0 || actual.CompareTo(high) > 0)
            throw new InvalidOperationException("The demo scenario produced a value outside the accepted range.");
    }

    public static T Single<T>(IEnumerable<T> values)
    {
        using var enumerator = values.GetEnumerator();
        if (!enumerator.MoveNext())
            throw new InvalidOperationException("The demo scenario omitted a required result.");
        var value = enumerator.Current;
        if (enumerator.MoveNext())
            throw new InvalidOperationException("The demo scenario returned more than one matching result.");
        return value;
    }

    public static T Single<T>(IEnumerable<T> values, Func<T, bool> predicate)
    {
        var matches = values.Where(predicate).Take(2).ToArray();
        if (matches.Length != 1)
            throw new InvalidOperationException("The demo scenario did not produce exactly one required result.");
        return matches[0];
    }

    public static void AssertNoExpectedOrActualProperties(string json)
    {
        using var document = JsonDocument.Parse(json);
        AssertNoExpectedOrActualProperties(document.RootElement);
    }

    private static void AssertNoExpectedOrActualProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                True(!property.Name.Equals("expected", StringComparison.OrdinalIgnoreCase) &&
                    !property.Name.Equals("actual", StringComparison.OrdinalIgnoreCase),
                    $"Report contains a raw '{property.Name}' field.");
                AssertNoExpectedOrActualProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                AssertNoExpectedOrActualProperties(item);
        }
    }
}
