using System.Globalization;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ProofShift.Domain;
using ProofShift.Packs.Pension;

namespace ProofShift.Cli;

internal static class PensionDemoGenerator
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static async Task<int> GenerateAsync(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            Console.Error.WriteLine("Usage: proofshift demo generate <output-directory> [--scale fast|large] [--seed <integer>]");
            return 2;
        }

        var outputDirectory = Path.GetFullPath(args[0]);
        var scaleName = "fast";
        var seed = PensionSyntheticDatasetGenerator.DefaultSeed;
        for (var index = 1; index < args.Count; index++)
        {
            if (args[index] == "--scale" && index + 1 < args.Count)
            {
                scaleName = args[++index];
            }
            else if (args[index] == "--seed" && index + 1 < args.Count &&
                long.TryParse(args[index + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedSeed))
            {
                seed = parsedSeed;
                index++;
            }
            else
            {
                Console.Error.WriteLine("Demo generation accepts only --scale fast|large and --seed <integer>.");
                return 2;
            }
        }

        var scale = scaleName.ToLowerInvariant() switch
        {
            "fast" => PensionDatasetScale.Fast,
            "large" => PensionDatasetScale.Large,
            _ => null
        };
        if (scale is null)
        {
            Console.Error.WriteLine("Pension demo scale must be 'fast' or 'large'.");
            return 2;
        }

        try
        {
            if (Directory.Exists(outputDirectory) && Directory.EnumerateFileSystemEntries(outputDirectory).Any())
                throw new IOException("Output directory already contains files; refusing to overwrite fixture data.");
            Directory.CreateDirectory(outputDirectory);
            var sourceCounts = await ExportAsync(Path.Combine(outputDirectory, "source"),
                PensionSyntheticDatasetGenerator.Generate(seed, scale)).ConfigureAwait(false);
            var cleanRecords = PensionTargetDataModel.Transform(PensionSyntheticDatasetGenerator.Generate(seed, scale));
            var cleanCounts = await ExportAsync(Path.Combine(outputDirectory, "target-corrected"), cleanRecords).ConfigureAwait(false);
            var defectiveRecords = PensionDefectInjector.InjectTargetDefects(
                PensionTargetDataModel.Transform(PensionSyntheticDatasetGenerator.Generate(seed, scale)));
            var defectiveCounts = await ExportAsync(Path.Combine(outputDirectory, "target-defective"), defectiveRecords).ConfigureAwait(false);
            var manifest = new PensionDemoManifest(PensionSyntheticDatasetGenerator.Version,
                PensionTargetDataModel.Version, PensionDefectInjector.Version, seed, scaleName.ToLowerInvariant(),
                PensionSyntheticDatasetGenerator.Fingerprint(seed, scale), sourceCounts, cleanCounts, defectiveCounts,
                PensionDefectCounts.V1, PensionDefectInjector.FalseReverseDeclarations);
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, "demo-manifest.json"),
                JsonSerializer.Serialize(manifest, JsonOptions), new UTF8Encoding(false)).ConfigureAwait(false);
            Console.WriteLine(JsonSerializer.Serialize(manifest, JsonOptions));
            return 0;
        }
        catch
        {
            Console.Error.WriteLine("Synthetic pension demo data could not be generated.");
            return 1;
        }
    }

    public static Task<int> BenchmarkAsync(IReadOnlyList<string> args)
    {
        var scaleName = "large";
        var seed = PensionSyntheticDatasetGenerator.DefaultSeed;
        for (var index = 0; index < args.Count; index++)
        {
            if (args[index] == "--scale" && index + 1 < args.Count)
                scaleName = args[++index];
            else if (args[index] == "--seed" && index + 1 < args.Count &&
                long.TryParse(args[index + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedSeed))
            {
                seed = parsedSeed;
                index++;
            }
            else
            {
                Console.Error.WriteLine("Demo benchmark accepts only --scale fast|large and --seed <integer>.");
                return Task.FromResult(2);
            }
        }

        var scale = scaleName.ToLowerInvariant() switch
        {
            "fast" => PensionDatasetScale.Fast,
            "large" => PensionDatasetScale.Large,
            _ => null
        };
        if (scale is null)
        {
            Console.Error.WriteLine("Pension demo benchmark scale must be 'fast' or 'large'.");
            return Task.FromResult(2);
        }

        var stopwatch = Stopwatch.StartNew();
        long records = 0;
        long estimatedBytes = 0;
        foreach (var record in PensionSyntheticDatasetGenerator.Generate(seed, scale))
        {
            records++;
            estimatedBytes = checked(estimatedBytes + EstimateRecordBytes(record));
        }
        stopwatch.Stop();
        using var process = Process.GetCurrentProcess();
        var seconds = stopwatch.Elapsed.TotalSeconds;
        var result = new PensionGeneratorBenchmark("proofshift-pension-generator-v1", scaleName.ToLowerInvariant(), seed,
            records, estimatedBytes, stopwatch.Elapsed.TotalMilliseconds,
            seconds <= 0 ? 0 : records / seconds,
            seconds <= 0 ? 0 : estimatedBytes / seconds,
            process.PeakWorkingSet64);
        Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
        return Task.FromResult(0);
    }

    private static long EstimateRecordBytes(PensionSyntheticRecord record)
    {
        long bytes = Encoding.UTF8.GetByteCount(record.SemanticType) + Encoding.UTF8.GetByteCount(record.Identity);
        foreach (var pair in record.Values)
            bytes = checked(bytes + Encoding.UTF8.GetByteCount(pair.Key) + Encoding.UTF8.GetByteCount(ValueText(pair.Value)));
        return bytes;
    }

    private static string ValueText(ValueNode value) => value switch
    {
        NullValue => string.Empty,
        StringValue item => item.Value,
        IntegerValue item => item.Value.ToString(CultureInfo.InvariantCulture),
        DecimalValue item => item.Value.ToString("G29", CultureInfo.InvariantCulture),
        BooleanValue item => item.Value ? "true" : "false",
        DateValue item => item.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        InstantValue item => item.Value.ToString("O", CultureInfo.InvariantCulture),
        OffsetDateTimeValue item => item.Value.ToString("O", CultureInfo.InvariantCulture),
        LocalDateTimeValue item => item.Value.ToString("O", CultureInfo.InvariantCulture),
        BinaryReferenceValue item => item.Sha256,
        _ => throw new InvalidDataException("Pension generator benchmark contains an unsupported normalized value.")
    };

    private static async Task<IReadOnlyDictionary<string, long>> ExportAsync(string directory,
        IEnumerable<PensionSyntheticRecord> records)
    {
        Directory.CreateDirectory(directory);
        var writers = new Dictionary<PensionRecordKind, CsvFileWriter>();
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        try
        {
            foreach (var record in records)
            {
                if (!writers.TryGetValue(record.Kind, out var writer))
                {
                    writer = new CsvFileWriter(Path.Combine(directory, FileName(record.Kind)));
                    writers.Add(record.Kind, writer);
                }
                await writer.WriteAsync(record).ConfigureAwait(false);
                var key = record.Kind.ToString();
                counts[key] = counts.GetValueOrDefault(key) + 1;
            }
        }
        finally
        {
            foreach (var writer in writers.Values) await writer.DisposeAsync().ConfigureAwait(false);
        }
        return new SortedDictionary<string, long>(counts, StringComparer.Ordinal);
    }

    private static string FileName(PensionRecordKind kind)
    {
        var name = new StringBuilder();
        foreach (var character in kind.ToString())
        {
            if (char.IsUpper(character) && name.Length > 0) name.Append('-');
            name.Append(char.ToLowerInvariant(character));
        }
        return name + ".csv";
    }

    private sealed class CsvFileWriter : IAsyncDisposable
    {
        private readonly StreamWriter _writer;
        private bool _disposed;

        public CsvFileWriter(string path)
        {
            _writer = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
        }

        public async Task WriteAsync(PensionSyntheticRecord record)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_initializedFields.Length == 0)
            {
                var fields = record.Values.Keys.Order(StringComparer.Ordinal).ToArray();
                await _writer.WriteLineAsync(string.Join(',', fields.Select(Escape))).ConfigureAwait(false);
                _initializedFields = fields;
            }
            var activeFields = _initializedFields;
            await _writer.WriteLineAsync(string.Join(',', activeFields.Select(field => Escape(
                record.Values.TryGetValue(field, out var value) ? PensionDemoGenerator.ValueText(value) : string.Empty)))).ConfigureAwait(false);
        }

        private string[] _initializedFields = [];

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            await _writer.DisposeAsync().ConfigureAwait(false);
        }

        private static string Escape(string value) => value.IndexOfAny([',', '"', '\r', '\n']) < 0
            ? value
            : '"' + value.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';

    }

    private sealed record PensionDemoManifest(string GeneratorVersion, string TargetModelVersion,
        string DefectSetVersion, long Seed, string Scale, string SourceFingerprint,
        IReadOnlyDictionary<string, long> SourceRecords, IReadOnlyDictionary<string, long> CorrectedTargetRecords,
        IReadOnlyDictionary<string, long> DefectiveTargetRecords, PensionDefectCounts Defects,
        IReadOnlyList<PensionFalseReverseDeclaration> FalseReverseDeclarations);
    private sealed record PensionGeneratorBenchmark(string GeneratorVersion, string Scale, long Seed,
        long RecordsGenerated, long EstimatedUtf8Bytes, double DurationMilliseconds,
        double RecordsPerSecond, double EstimatedBytesPerSecond, long PeakWorkingSetBytes);
}
