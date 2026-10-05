using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using ProofShift.Connectors.Abstractions;
using ProofShift.Domain;

namespace ProofShift.Connectors.Csv;

public sealed class CsvSourceConnector : ICheckpointSourceConnector, IPhysicalDiscoveryConnector
{
    public ConnectorId Id { get; } = new("csv");
    public string Version => "0.1.0";
    public SourceConsistencyGuarantee CheckpointConsistency => SourceConsistencyGuarantee.Observed;

    public async Task<PhysicalDiscoveryArtifact> DiscoverAsync(ConnectorContext context,
        IReadOnlyCollection<ArtifactSelector> selectors, CancellationToken cancellationToken)
    {
        var objects = new List<PhysicalObject>();
        foreach (var selector in selectors.Distinct())
        {
            if (!TryResolveCsvPath(context, selector, out var root, out var relativePath) ||
                !ConnectorPathUtilities.TryResolveContainedPath(root, relativePath, out var fullPath))
                throw new ConnectorReadException(ConnectorIssueCodes.PathOutsideRoot, "Discovery CSV path is outside its endpoint root.");
            using var text = CreateTextReaderSafe(fullPath);
            using var csv = CreateCsvReaderSafe(text, GetDelimiter(context, selector));
            var headers = await ReadHeaderAsync(csv, cancellationToken).ConfigureAwait(false)
                ?? throw new ConnectorReadException(ConnectorIssueCodes.InvalidCsv, "Discovery CSV requires headers.");
            if (headers.Distinct(StringComparer.Ordinal).Count() != headers.Length || headers.Any(string.IsNullOrWhiteSpace))
                throw new ConnectorReadException(ConnectorIssueCodes.InvalidCsv, "Discovery CSV headers must be nonempty and unique.");
            var kinds = Enumerable.Repeat("unknown", headers.Length).ToArray();
            var empty = new long[headers.Length];
            long rows = 0;
            while (await csv.ReadAsync().ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                rows++;
                for (var index = 0; index < headers.Length; index++)
                {
                    var value = csv.GetField(index) ?? string.Empty;
                    if (string.IsNullOrEmpty(value)) { empty[index]++; continue; }
                    var candidate = long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) ? "integer" :
                        decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out _) ? "decimal" :
                        bool.TryParse(value, out _) ? "boolean" :
                        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ? "date" : "string";
                    kinds[index] = kinds[index] == "unknown" || kinds[index] == candidate ? candidate :
                        (kinds[index] is "integer" or "decimal" && candidate is "integer" or "decimal") ? "decimal" : "string";
                }
            }
            if (selector.IdentityFields.Any(field => !headers.Contains(field, StringComparer.Ordinal)))
                throw new ConnectorReadException(ConnectorIssueCodes.IdentityFieldNotFound, "Declared discovery CSV identity field is absent.");
            objects.Add(new("", ConnectorPathUtilities.NormalizeRelativePath(relativePath), "csv",
                headers.Select((name, index) => new PhysicalField(name, kinds[index], empty[index] > 0, index + 1, true, empty[index])).ToArray(),
                selector.IdentityFields.Count == 0 ? [] : [new PhysicalKey("configured-identity", true, selector.IdentityFields)], [], rows,
                new FileInfo(fullPath).Length, new Dictionary<string, string> { ["path"] = relativePath, ["delimiter"] = GetDelimiter(context, selector) }));
        }
        return PhysicalDiscovery.Create(context, Id.Value, Version, objects);
    }

    public async IAsyncEnumerable<RecordEnvelope> ReadForCheckpointAsync(
        ConnectorContext context,
        ArtifactSelector selector,
        ReadOptions options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!TryResolveCsvPath(context, selector, out var root, out var relativePath) ||
            !ConnectorPathUtilities.TryResolveContainedPath(root, relativePath, out var fullPath))
        {
            throw new ConnectorReadException(ConnectorIssueCodes.PathOutsideRoot, "CSV path resolves outside the configured endpoint root.");
        }

        var before = await GetFileStateAsync(fullPath, cancellationToken).ConfigureAwait(false);
        await foreach (var record in ReadAsync(context, selector, options, cancellationToken)
            .WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return record;
        }

        var after = await GetFileStateAsync(fullPath, cancellationToken).ConfigureAwait(false);
        if (before != after)
        {
            throw new ConnectorReadException(ConnectorIssueCodes.ArtifactChangedDuringCapture,
                "CSV source file changed while its logical rows were being captured.");
        }
    }

    public async Task<SourceInspection> InspectAsync(
        ConnectorContext context,
        ArtifactSelector selector,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(selector);
        if (!string.Equals(selector.Kind, "csv", StringComparison.OrdinalIgnoreCase))
        {
            return Invalid(context, ConnectorIssueCodes.UnsupportedSelector, "CSV connector supports csv selectors only.");
        }

        if (!TryResolveCsvPath(context, selector, out var root, out var relativePath))
        {
            return Invalid(context, ConnectorIssueCodes.PathOutsideRoot,
                "CSV path must be relative to the configured endpoint root and remain inside it.");
        }

        if (selector.IdentityFields.Count == 0)
        {
            return Invalid(context, ConnectorIssueCodes.NonDeterministicIdentity,
                "CSV selector requires one or more identity fields.", relativePath);
        }

        if (!ConnectorPathUtilities.TryResolveContainedPath(root, relativePath, out var fullPath))
        {
            return Invalid(context, ConnectorIssueCodes.PathOutsideRoot,
                "CSV path resolves outside the configured endpoint root.");
        }

        if (!File.Exists(fullPath))
        {
            return Invalid(context, ConnectorIssueCodes.SourceObjectNotFound, "Configured CSV source file was not found.", relativePath);
        }

        try
        {
            var delimiter = GetDelimiter(context, selector);
            using var textReader = CreateTextReaderSafe(fullPath);
            using var csv = CreateCsvReaderSafe(textReader, delimiter);
            var header = await ReadHeaderAsync(csv, cancellationToken).ConfigureAwait(false);
            if (header is null)
            {
                return Invalid(context, ConnectorIssueCodes.InvalidCsv, "CSV file does not contain a header row.", relativePath);
            }

            var headerIssue = ValidateHeader(header, selector.IdentityFields, context, relativePath);
            if (headerIssue is not null)
            {
                return headerIssue;
            }

            var info = new FileInfo(fullPath);
            return new SourceInspection(
                SourceInspectionStatus.Valid,
                header.Select((name, index) => new SourceColumn(name, "string", true, index + 1)),
                identityFields: selector.IdentityFields,
                physicalObject: ConnectorPathUtilities.NormalizeRelativePath(relativePath),
                files: 1,
                bytes: info.Length);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ConnectorReadException exception)
        {
            return Invalid(context, exception.Code, exception.Message, relativePath);
        }
        catch
        {
            return Invalid(context, ConnectorIssueCodes.InvalidCsv, "CSV source could not be parsed.", relativePath);
        }
    }

    public async IAsyncEnumerable<RecordEnvelope> ReadAsync(
        ConnectorContext context,
        ArtifactSelector selector,
        ReadOptions options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(options);
        if (options.Partition.Count > 0)
        {
            throw new ConnectorReadException(ConnectorIssueCodes.PartitioningUnsupported,
                "Partitioned source reads are not implemented yet.");
        }

        if (!string.Equals(selector.Kind, "csv", StringComparison.OrdinalIgnoreCase))
        {
            throw new ConnectorReadException(ConnectorIssueCodes.UnsupportedSelector, "CSV connector supports csv selectors only.");
        }

        if (selector.IdentityFields.Count == 0)
        {
            throw new ConnectorReadException(ConnectorIssueCodes.NonDeterministicIdentity, "CSV selector requires one or more identity fields.");
        }

        if (!TryResolveCsvPath(context, selector, out var root, out var relativePath) ||
            !ConnectorPathUtilities.TryResolveContainedPath(root, relativePath, out var fullPath))
        {
            throw new ConnectorReadException(ConnectorIssueCodes.PathOutsideRoot, "CSV path resolves outside the configured endpoint root.");
        }

        if (!File.Exists(fullPath))
        {
            throw new ConnectorReadException(ConnectorIssueCodes.SourceObjectNotFound, "Configured CSV source file was not found.");
        }

        var delimiter = GetDelimiter(context, selector);
        await using var identityIndex = await DiskBackedIdentityIndex.CreateAsync(cancellationToken).ConfigureAwait(false);
        using var textReader = CreateTextReaderSafe(fullPath);
        using var csv = CreateCsvReaderSafe(textReader, delimiter);
        var header = await ReadHeaderAsync(csv, cancellationToken).ConfigureAwait(false)
            ?? throw new ConnectorReadException(ConnectorIssueCodes.InvalidCsv, "CSV file does not contain a header row.");
        var headerIssue = ValidateHeader(header, selector.IdentityFields, context, relativePath);
        if (headerIssue is not null)
        {
            var issue = headerIssue.Issues[0];
            throw new ConnectorReadException(issue.Code, issue.Message);
        }

        while (await ReadNextRowAsync(csv, cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fields = ReadFields(csv, header, context, relativePath);
            var physicalIdentity = FormatIdentity(relativePath, selector.IdentityFields, fields);
            await identityIndex.AddAsync(physicalIdentity, cancellationToken).ConfigureAwait(false);
            yield return CreateRecordEnvelope(context, selector, relativePath, fields, physicalIdentity);
        }
    }

    private static CsvConfiguration CreateConfiguration(string delimiter) => new(CultureInfo.InvariantCulture)
    {
        HasHeaderRecord = true,
        Delimiter = delimiter,
        DetectDelimiter = false,
        DetectColumnCountChanges = true,
        ExceptionMessagesContainRawData = false,
        Mode = CsvMode.RFC4180
    };

    private static CsvReader CreateCsvReader(TextReader textReader, string delimiter) =>
        new(textReader, CreateConfiguration(delimiter));

    private static StreamReader CreateTextReaderSafe(string path)
    {
        try
        {
            return CreateTextReader(path);
        }
        catch
        {
            throw new ConnectorReadException(ConnectorIssueCodes.SourceReadFailed, "CSV source file could not be opened.");
        }
    }

    private static CsvReader CreateCsvReaderSafe(TextReader textReader, string delimiter)
    {
        try
        {
            return CreateCsvReader(textReader, delimiter);
        }
        catch (ConnectorConfigurationException)
        {
            throw;
        }
        catch
        {
            throw new ConnectorReadException(ConnectorIssueCodes.InvalidCsv, "CSV reader could not be initialized.");
        }
    }

    private static StreamReader CreateTextReader(string path) =>
        new(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan),
            new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);

    private static async Task<string[]?> ReadHeaderAsync(CsvReader csv, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (!await csv.ReadAsync().ConfigureAwait(false))
            {
                return null;
            }

            csv.ReadHeader();
            var header = csv.HeaderRecord;
            if (header is null || header.Length == 0 || header.Any(string.IsNullOrWhiteSpace) ||
                header.Distinct(StringComparer.Ordinal).Count() != header.Length)
            {
                throw new ConnectorReadException(ConnectorIssueCodes.InvalidCsv,
                    "CSV header names must be non-empty and unique.");
            }

            return header;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ConnectorReadException)
        {
            throw;
        }
        catch
        {
            throw new ConnectorReadException(ConnectorIssueCodes.InvalidCsv, "CSV header could not be read.");
        }
    }

    private static async Task<bool> ReadNextRowAsync(CsvReader csv, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return await csv.ReadAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            throw new ConnectorReadException(ConnectorIssueCodes.InvalidCsv, "CSV row could not be parsed.");
        }
    }

    private static Dictionary<string, string> ReadFields(
        CsvReader csv,
        string[] header,
        ConnectorContext context,
        string location)
    {
        try
        {
            if (csv.Parser.Count != header.Length)
            {
                throw new ConnectorReadException(ConnectorIssueCodes.InvalidCsv,
                    "CSV row field count does not match its header.");
            }

            var fields = new Dictionary<string, string>(header.Length, StringComparer.Ordinal);
            for (var index = 0; index < header.Length; index++)
            {
                fields.Add(header[index], csv.GetField(index) ?? string.Empty);
            }

            return fields;
        }
        catch (ConnectorReadException)
        {
            throw;
        }
        catch
        {
            throw new ConnectorReadException(ConnectorIssueCodes.InvalidCsv,
                $"CSV row could not be read from '{location}'.");
        }
    }

    private static SourceInspection? ValidateHeader(
        IReadOnlyCollection<string> header,
        IEnumerable<string> identityFields,
        ConnectorContext context,
        string location)
    {
        foreach (var field in identityFields)
        {
            if (!header.Contains(field, StringComparer.Ordinal))
            {
                return Invalid(context, ConnectorIssueCodes.IdentityFieldNotFound,
                    "Configured identity field was not found in the CSV header.", location);
            }
        }

        return null;
    }

    private static RecordEnvelope CreateRecordEnvelope(
        ConnectorContext context,
        ArtifactSelector selector,
        string relativePath,
        Dictionary<string, string> fields,
        string identity)
    {
        var values = fields.Select(pair => new KeyValuePair<string, ValueNode>(pair.Key, new StringValue(pair.Value)));
        var artifact = new ArtifactReference(
            new ArtifactId(ArtifactIdentity.Create(context.SystemKey, context.EndpointKey, "csv-record", identity)),
            new SystemId(context.SystemKey),
            new StorageEndpointId(context.EndpointKey),
            "csv-record",
            identity);
        var provenance = new ProvenanceMetadata(
            new ConnectorId("csv"),
            new StorageEndpointId(context.EndpointKey),
            ConnectorPathUtilities.NormalizeRelativePath(relativePath),
            DateTimeOffset.UtcNow,
            metadata:
            [
                new KeyValuePair<string, string>("identityFields", string.Join(',', selector.IdentityFields)),
                new KeyValuePair<string, string>("observationKind", "read")
            ]);
        return new RecordEnvelope(artifact, context.SemanticType, values, provenance);
    }

    private static string FormatIdentity(string relativePath, IEnumerable<string> identityFields, Dictionary<string, string> values)
    {
        var builder = new StringBuilder();
        builder.Append(Encoding.UTF8.GetByteCount(relativePath).ToString(CultureInfo.InvariantCulture))
            .Append(':').Append(ConnectorPathUtilities.NormalizeRelativePath(relativePath));
        foreach (var field in identityFields)
        {
            if (!values.TryGetValue(field, out var value))
            {
                throw new ConnectorReadException(ConnectorIssueCodes.IdentityFieldNotFound,
                    "Configured identity field is absent from the CSV row.");
            }

            builder.Append(Encoding.UTF8.GetByteCount(field).ToString(CultureInfo.InvariantCulture))
                .Append(':').Append(field)
                .Append('=').Append(Encoding.UTF8.GetByteCount(value).ToString(CultureInfo.InvariantCulture))
                .Append(':').Append(value);
        }

        return builder.ToString();
    }

    private static string GetDelimiter(ConnectorContext context, ArtifactSelector selector)
    {
        if (selector.Properties.TryGetValue("delimiter", out var selectorDelimiter))
        {
            return ValidateDelimiter(selectorDelimiter);
        }

        if (context.Configuration.TryGet("delimiter", out var configuredDelimiter))
        {
            return configuredDelimiter.UseValue(ValidateDelimiter);
        }

        return ",";
    }

    private static async Task<(long Length, long LastWriteTicks, string Sha256)> GetFileStateAsync(
        string path,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new ConnectorReadException(ConnectorIssueCodes.ArtifactChangedDuringCapture, "CSV source file disappeared during capture.");
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        info.Refresh();
        return (info.Length, info.LastWriteTimeUtc.Ticks, Convert.ToHexString(hash).ToLowerInvariant());
    }

    private static string ValidateDelimiter(string delimiter)
    {
        if (delimiter.Length is < 1 or > 4 || delimiter.Contains('\r') || delimiter.Contains('\n') || delimiter.Contains('"'))
        {
            throw new ConnectorConfigurationException(ConnectorIssueCodes.InvalidCsv, "CSV delimiter configuration is invalid.");
        }

        return delimiter;
    }

    private static bool TryResolveCsvPath(
        ConnectorContext context,
        ArtifactSelector selector,
        out string root,
        out string relativePath)
    {
        root = string.Empty;
        relativePath = string.Empty;
        if (!selector.Properties.TryGetValue("path", out relativePath!) || string.IsNullOrWhiteSpace(relativePath))
        {
            return false;
        }

        try
        {
            root = ConnectorPathUtilities.ResolveRoot(context);
            return ConnectorPathUtilities.TryResolveContainedPath(root, relativePath, out _);
        }
        catch (ConnectorConfigurationException)
        {
            return false;
        }
    }

    private static SourceInspection Invalid(ConnectorContext context, string code, string message, string? location = null) =>
        new(SourceInspectionStatus.Invalid, physicalObject: location, issues:
        [
            new ConnectorIssue(code, ConnectorIssueSeverity.Error, message, context.NodeKey, location)
        ]);
}
