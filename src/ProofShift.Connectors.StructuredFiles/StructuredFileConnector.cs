using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using System.Numerics;
using ProofShift.Connectors.Abstractions;
using ProofShift.Connectors.Csv;
using ProofShift.Domain;

namespace ProofShift.Connectors.StructuredFiles;

public sealed class StructuredFileConnector : ICheckpointSourceConnector, IPhysicalDiscoveryConnector, IConnectorConfigurationSchemaProvider
{
    public ConnectorId Id { get; }
    public string Version => "0.10.0";
    public SourceConsistencyGuarantee CheckpointConsistency => SourceConsistencyGuarantee.Observed;
    public ConnectorConfigurationSchema ConfigurationSchema => StructuredFileConfigurationSchemas.Get(Id.Value);

    public StructuredFileConnector(string format)
    {
        if (format is not ("json" or "ndjson" or "xml" or "fixed-width")) throw new ArgumentException("Unsupported structured-file format.", nameof(format));
        Id = new ConnectorId(format);
    }

    public Task<SourceInspection> InspectAsync(ConnectorContext context, ArtifactSelector selector, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = ResolvePath(context, selector);
        Validate(selector);
        return Task.FromResult(new SourceInspection(SourceInspectionStatus.Valid, physicalObject: selector.Properties["path"],
            identityFields: selector.IdentityFields, files: 1, bytes: new FileInfo(path).Length));
    }

    public async IAsyncEnumerable<RecordEnvelope> ReadAsync(ConnectorContext context, ArtifactSelector selector, ReadOptions options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (options.Partition.Count != 0) throw Error(ConnectorIssueCodes.PartitioningUnsupported, "Partitioned file observation is unsupported.");
        Validate(selector);
        var path = ResolvePath(context, selector);
        var relative = ConnectorPathUtilities.NormalizeRelativePath(selector.Properties["path"]);
        await using var hashStream = File.OpenRead(path);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(hashStream, cancellationToken)).ToLowerInvariant();
        await using var identities = await DiskBackedIdentityIndex.CreateAsync(cancellationToken);
        long recordIndex = 0;
        await foreach (var values in ReadValuesAsync(path, selector, cancellationToken).WithCancellation(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            recordIndex++;
            var identityParts = new List<string>();
            foreach (var field in selector.IdentityFields)
            {
                if (!values.TryGetValue(field, out var value) || value is NullValue)
                    throw Error(ConnectorIssueCodes.NonDeterministicIdentity, $"Record {recordIndex}: configured identity is missing or NULL.");
                var encoded = JsonSerializer.Serialize(ValueDocument(value));
                identityParts.Add($"{field.Length}:{field}={encoded.Length}:{encoded}");
            }
            var identity = string.Join('|', identityParts);
            await identities.AddAsync(identity, cancellationToken);
            var artifact = new ArtifactReference(new ArtifactId(StableArtifactIdentity.CreateArtifactId(context.SystemKey,
                context.EndpointKey, selector.Kind, identity)), new SystemId(context.SystemKey), new StorageEndpointId(context.EndpointKey), selector.Kind, identity);
            yield return new RecordEnvelope(artifact, context.SemanticType, values, new ProvenanceMetadata(Id,
                new StorageEndpointId(context.EndpointKey), $"{relative}#{recordIndex.ToString(CultureInfo.InvariantCulture)}", DateTimeOffset.UtcNow,
                sourceHash: hash, metadata: [new("observationKind", "read"), new("recordIndex", recordIndex.ToString(CultureInfo.InvariantCulture))]));
        }
    }

    public async IAsyncEnumerable<RecordEnvelope> ReadForCheckpointAsync(ConnectorContext context, ArtifactSelector selector, ReadOptions options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var path = ResolvePath(context, selector);
        var before = new FileInfo(path);
        var length = before.Length;
        var modified = before.LastWriteTimeUtc;
        await foreach (var record in ReadAsync(context, selector, options, cancellationToken).WithCancellation(cancellationToken)) yield return record;
        var after = new FileInfo(path);
        if (after.Length != length || after.LastWriteTimeUtc != modified)
            throw Error(ConnectorIssueCodes.ArtifactChangedDuringCapture, "Structured file changed during checkpoint capture.");
    }

    public async Task<PhysicalDiscoveryArtifact> DiscoverAsync(ConnectorContext context, IReadOnlyCollection<ArtifactSelector> selectors,
        CancellationToken cancellationToken)
    {
        var objects = new List<PhysicalObject>();
        foreach (var selector in selectors)
        {
            var path = ResolvePath(context, selector);
            if (Id.Value == "fixed-width" && !selector.Properties.ContainsKey("width"))
            {
                objects.Add(await DiscoverFixedWidthWithoutSchemaAsync(path, selector, cancellationToken).ConfigureAwait(false));
                continue;
            }
            if (Id.Value == "xml" && DeclaredFields(selector).Length == 0)
            {
                objects.AddRange(await DiscoverXmlWithoutFieldSchemaAsync(path, selector, cancellationToken).ConfigureAwait(false));
                continue;
            }
            var fields = new Dictionary<string, (string Type, long Nulls)>(StringComparer.Ordinal);
            long count = 0;
            await foreach (var values in ReadValuesAsync(path, selector, cancellationToken).WithCancellation(cancellationToken))
            {
                count++;
                foreach (var pair in values)
                {
                    if (fields.Count > 10000) throw Error(ConnectorIssueCodes.UnsupportedPhysicalType, "Discovery field-path limit exceeded; configure selected fields.");
                    var prior = fields.GetValueOrDefault(pair.Key);
                    var kind = pair.Value.GetType().Name;
                    fields[pair.Key] = (prior.Type is null || prior.Type == kind ? kind : "Mixed", prior.Nulls + (pair.Value is NullValue ? 1 : 0));
                }
            }
            objects.Add(new("", selector.Properties["path"], Id.Value, fields.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select((pair, index) => new PhysicalField(pair.Key, pair.Value.Type, pair.Value.Nulls > 0, index + 1, true, pair.Value.Nulls)).ToArray(),
                selector.IdentityFields.Count == 0 ? [] : [new("configured-identity", true, selector.IdentityFields)], [], count,
                new FileInfo(ResolvePath(context, selector)).Length, selector.Properties));
        }
        return PhysicalDiscovery.Create(context, Id.Value, Version, objects);
    }

    private async Task<PhysicalObject> DiscoverFixedWidthWithoutSchemaAsync(string path, ArtifactSelector selector,
        CancellationToken cancellationToken)
    {
        long count = 0;
        using var reader = new StreamReader(path, EncodingFor(selector), detectEncodingFromByteOrderMarks: true);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            count++;
        }
        return new PhysicalObject("", selector.Properties["path"], Id.Value, [], [], [], count,
            new FileInfo(path).Length, selector.Properties);
    }

    private static async Task<IReadOnlyCollection<PhysicalObject>> DiscoverXmlWithoutFieldSchemaAsync(string path,
        ArtifactSelector selector, CancellationToken cancellationToken)
    {
        var settings = new XmlReaderSettings { Async = true, DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        using var reader = XmlReader.Create(path, settings);
        var rootName = (XName?)null;
        var rootObject = new PhysicalObject("", selector.Properties["path"], "xml-root", [], [], [], null,
            new FileInfo(path).Length, selector.Properties);
        var configuredBoundary = selector.Properties.TryGetValue("recordElement", out var recordElement)
            ? XmlName(recordElement, selector) : null;
        var candidates = new Dictionary<XName, XmlCandidateSummary>();
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.NodeType != XmlNodeType.Element) continue;
            var currentName = XName.Get(reader.LocalName, reader.NamespaceURI);
            if (reader.Depth == 0) rootName = currentName;
            var isCandidate = configuredBoundary is not null
                ? currentName == configuredBoundary
                : reader.Depth == 1;
            if (!isCandidate) continue;
            using var subtree = reader.ReadSubtree();
            var record = await XElement.LoadAsync(subtree, LoadOptions.None, cancellationToken).ConfigureAwait(false);
            if (!candidates.TryGetValue(record.Name, out var candidate))
                candidates.Add(record.Name, candidate = new XmlCandidateSummary(record.Name));
            candidate.AddRecord(record);
        }

        var objects = new List<PhysicalObject>
        {
            rootName is null ? rootObject : rootObject with { Schema = rootName.NamespaceName, Name = rootName.LocalName }
        };
        foreach (var candidate in candidates.Values.Where(candidate => candidate.Count > 1 || configuredBoundary is not null)
            .OrderBy(candidate => candidate.Name.NamespaceName, StringComparer.Ordinal).ThenBy(candidate => candidate.Name.LocalName, StringComparer.Ordinal))
        {
            var fields = candidate.Fields.OrderBy(field => field.Key, StringComparer.Ordinal).Select((field, index) =>
                new PhysicalField(field.Value.Name, field.Value.Type, field.Value.Nulls > 0, index + 1, TypeInferred: true,
                    EmptyValues: field.Value.Nulls)).ToArray();
            var candidateProperties = selector.Properties.Concat([new KeyValuePair<string, string>("recordElement", candidate.Name.ToString())])
                .Concat(candidate.Fields.Select(field => new KeyValuePair<string, string>($"fields.{field.Value.Name}.path", field.Key)));
            var properties = candidateProperties
                .GroupBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.Ordinal);
            objects.Add(new PhysicalObject(candidate.Name.NamespaceName, candidate.Name.LocalName, "xml-record-candidate",
                fields, [], [], candidate.Count, new FileInfo(path).Length, properties));
        }
        return objects;
    }

    private sealed class XmlCandidateSummary(XName name)
    {
        public XName Name { get; } = name;
        public long Count { get; private set; }
        public Dictionary<string, XmlFieldSummary> Fields { get; } = new(StringComparer.Ordinal);

        public void AddRecord(XElement record)
        {
            Count++;
            foreach (var element in record.Descendants().Where(element => !element.HasElements))
                AddField(ElementPath(record, element), element.Value);
            foreach (var attribute in record.DescendantsAndSelf().Attributes().Where(attribute => !attribute.IsNamespaceDeclaration))
                AddField(AttributePath(record, attribute), attribute.Value);
        }

        private void AddField(string path, string value)
        {
            if (path.Length == 0) return;
            if (!Fields.TryGetValue(path, out var summary)) Fields.Add(path, summary = new XmlFieldSummary($"field{Fields.Count + 1:D4}"));
            summary.Add(value);
        }

        private static string ElementPath(XElement record, XElement leaf) => string.Join('/', leaf.AncestorsAndSelf()
            .TakeWhile(element => element != record).Reverse().Select(element => element.Name.ToString()));

        private static string AttributePath(XElement record, XAttribute attribute)
        {
            var owner = attribute.Parent;
            if (owner is null) return "";
            var elementPath = owner == record ? "" : ElementPath(record, owner);
            return elementPath.Length == 0 ? $"@{attribute.Name}" : $"{elementPath}/@{attribute.Name}";
        }
    }

    private sealed class XmlFieldSummary(string name)
    {
        public string Name { get; } = name;
        public string Type { get; private set; } = "";
        public long Nulls { get; private set; }

        public void Add(string value)
        {
            if (value.Length == 0) Nulls++;
            var type = CandidateType(value);
            Type = Type.Length == 0 || Type == type ? type : "Mixed";
        }

        private static string CandidateType(string value)
        {
            if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) return "Integer";
            if (decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) return "Decimal";
            if (bool.TryParse(value, out _)) return "Boolean";
            if (DateOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) return "Date";
            return "String";
        }
    }

    private async IAsyncEnumerable<Dictionary<string, ValueNode>> ReadValuesAsync(string path, ArtifactSelector selector,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (Id.Value == "json")
        {
            if (EncodingFor(selector).CodePage != Encoding.UTF8.CodePage)
                throw Error("PSCONN018", "JSON array input requires UTF-8 encoding.");
            await using var stream = File.OpenRead(path);
            await foreach (var element in JsonSerializer.DeserializeAsyncEnumerable<JsonElement>(stream,
                cancellationToken: cancellationToken).WithCancellation(cancellationToken)) yield return JsonRecord(element, selector);
            yield break;
        }
        if (Id.Value == "xml")
        {
            var boundary = XmlName(Required(selector, "recordElement"), selector);
            var settings = new XmlReaderSettings { Async = true, DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using var reader = XmlReader.Create(path, settings);
            while (await reader.ReadAsync())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (reader.NodeType != XmlNodeType.Element || XName.Get(reader.LocalName, reader.NamespaceURI) != boundary) continue;
                using var subtree = reader.ReadSubtree();
                var record = await XElement.LoadAsync(subtree, LoadOptions.SetLineInfo, cancellationToken);
                var values = new Dictionary<string, ValueNode>(StringComparer.Ordinal);
                foreach (var field in DeclaredFields(selector))
                {
                    var extraction = selector.Properties.GetValueOrDefault($"fields.{field}.path") ?? field;
                    var parts = extraction.Split('/', StringSplitOptions.RemoveEmptyEntries);
                    XElement? current = record;
                    foreach (var part in parts.Where(part => !part.StartsWith('@'))) current = current?.Element(XmlName(part, selector));
                    var text = parts.LastOrDefault() is { } last && last.StartsWith('@') ? current?.Attribute(XmlName(last[1..], selector))?.Value : current?.Value;
                    try { values[field] = text is null ? new NullValue() : ParseText(text, selector, field); }
                    catch (ConnectorReadException exception)
                    {
                        var location = (IXmlLineInfo)record;
                        throw Error(exception.Code, $"{selector.Properties["path"]}: XML record at line {location.LineNumber}, field '{field}': {exception.Message}");
                    }
                }
                yield return values;
            }
            yield break;
        }
        using var textReader = new StreamReader(path, EncodingFor(selector), detectEncodingFromByteOrderMarks: true);
        long line = 0;
        while (await textReader.ReadLineAsync(cancellationToken) is { } text)
        {
            line++;
            cancellationToken.ThrowIfCancellationRequested();
            if (Id.Value == "ndjson")
            {
                JsonDocument record;
                try { record = JsonDocument.Parse(text); }
                catch (JsonException) { throw Error("PSCONN016", $"NDJSON line {line}: invalid object syntax; record values were omitted."); }
                using (record)
                {
                    Dictionary<string, ValueNode> values;
                    try { values = JsonRecord(record.RootElement, selector); }
                    catch (ConnectorReadException exception)
                    { throw Error(exception.Code, $"{selector.Properties["path"]}: NDJSON line {line}: {exception.Message}"); }
                    yield return values;
                }
            }
            else
            {
                var width = int.Parse(Required(selector, "width"), CultureInfo.InvariantCulture);
                if (text.Length != width) throw Error("PSCONN017", $"Fixed-width record {line}: length does not equal declared character width.");
                var values = new Dictionary<string, ValueNode>(StringComparer.Ordinal);
                foreach (var field in DeclaredFields(selector))
                {
                    var start = int.Parse(Required(selector, $"fields.{field}.start"), CultureInfo.InvariantCulture) - 1;
                    var length = int.Parse(Required(selector, $"fields.{field}.length"), CultureInfo.InvariantCulture);
                    try { values[field] = ParseText(text.Substring(start, length), selector, field); }
                    catch (ConnectorReadException exception)
                    { throw Error(exception.Code, $"{selector.Properties["path"]}: fixed-width record {line}, field '{field}': {exception.Message}"); }
                }
                yield return values;
            }
        }
    }

    private static Dictionary<string, ValueNode> JsonRecord(JsonElement element, ArtifactSelector selector)
    {
        if (element.ValueKind != JsonValueKind.Object) throw Error("PSCONN016", "JSON records must be objects.");
        var declared = DeclaredFields(selector);
        var values = new Dictionary<string, ValueNode>(StringComparer.Ordinal);
        if (declared.Length == 0)
            foreach (var property in element.EnumerateObject())
            {
                if (!values.TryAdd(property.Name, JsonValue(property.Value)))
                    throw Error("PSCONN020", "JSON record contains a duplicate property name.");
            }
        else foreach (var field in declared)
        {
            var path = selector.Properties.GetValueOrDefault($"fields.{field}.path") ?? field;
            if (path.Contains('[', StringComparison.Ordinal)) throw Error(ConnectorIssueCodes.UnsupportedSelector, "JSON array paths require explicit collection values; indexing is unsupported.");
            var current = element;
            var exists = true;
            foreach (var part in path.Split('.'))
                if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(part, out current)) { exists = false; break; }
            values[field] = !exists || current.ValueKind == JsonValueKind.Null ? new NullValue() :
                selector.Properties.ContainsKey($"fields.{field}.type")
                    ? DeclaredJsonValue(current, selector, field) : JsonValue(current);
        }
        return values;
    }

    private static ValueNode DeclaredJsonValue(JsonElement value, ArtifactSelector selector, string field)
    {
        if (value.ValueKind == JsonValueKind.String) return ParseText(value.GetString()!, selector, field);
        var type = selector.Properties[$"fields.{field}.type"];
        return (type, value.ValueKind) switch
        {
            ("integer", JsonValueKind.Number) => ParseText(value.GetRawText(), selector, field),
            ("decimal", JsonValueKind.Number) => ParseText(value.GetRawText(), selector, field),
            ("boolean", JsonValueKind.True or JsonValueKind.False) => new BooleanValue(value.GetBoolean()),
            ("object", JsonValueKind.Object) or ("collection", JsonValueKind.Array) => JsonValue(value),
            _ => throw Error(ConnectorIssueCodes.UnsupportedPhysicalType, $"Field '{field}' does not match its declared JSON type; values were omitted.")
        };
    }

    private static ValueNode JsonValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => new NullValue(), JsonValueKind.String => new StringValue(value.GetString()!),
        JsonValueKind.True => new BooleanValue(true), JsonValueKind.False => new BooleanValue(false),
        JsonValueKind.Number when value.TryGetInt64(out var integer) => new IntegerValue(integer),
        JsonValueKind.Number when value.TryGetDecimal(out var number) && DecimalIsExact(value.GetRawText(), number) => new DecimalValue(number),
        JsonValueKind.Array => new CollectionValue(value.EnumerateArray().Select(JsonValue)),
        JsonValueKind.Object => new ObjectValue(value.EnumerateObject().Select(property => new KeyValuePair<string, ValueNode>(property.Name, JsonValue(property.Value)))),
        _ => throw Error(ConnectorIssueCodes.UnsupportedPhysicalType, "JSON numeric precision exceeds the supported exact model.")
    };

    private static ValueNode ParseText(string value, ArtifactSelector selector, string field)
    {
        var trim = selector.Properties.GetValueOrDefault($"fields.{field}.trim") ?? "none";
        value = trim switch { "none" => value, "left" => value.TrimStart(), "right" => value.TrimEnd(), "both" => value.Trim(),
            _ => throw Error("PSCONN017", "Configured trim must be none, left, right, or both.") };
        if (selector.Properties.TryGetValue($"fields.{field}.null", out var sentinel) && value == sentinel) return new NullValue();
        var type = selector.Properties.GetValueOrDefault($"fields.{field}.type") ?? "string";
        try
        {
            return type switch
            {
                "string" => new StringValue(value), "integer" => new IntegerValue(long.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture)),
                "decimal" => new DecimalValue(ParseDecimal(value, selector, field)),
                "boolean" => new BooleanValue(value == (selector.Properties.GetValueOrDefault($"fields.{field}.true") ?? "true") ? true :
                    value == (selector.Properties.GetValueOrDefault($"fields.{field}.false") ?? "false") ? false : throw new FormatException()),
                "date" => new DateValue(DateOnly.ParseExact(value, Required(selector, $"fields.{field}.format"), CultureInfo.InvariantCulture)),
                "datetime" => new LocalDateTimeValue(DateTime.SpecifyKind(DateTime.ParseExact(value, Required(selector, $"fields.{field}.format"),
                    CultureInfo.InvariantCulture, DateTimeStyles.None), DateTimeKind.Unspecified)),
                "offset-datetime" when Required(selector, $"fields.{field}.format").Contains('z') || Required(selector, $"fields.{field}.format").Contains('K') =>
                    new OffsetDateTimeValue(DateTimeOffset.ParseExact(value, Required(selector, $"fields.{field}.format"), CultureInfo.InvariantCulture)),
                _ => throw Error(ConnectorIssueCodes.UnsupportedPhysicalType, "Configured field type is unsupported.")
            };
        }
        catch (Exception exception) when (exception is FormatException or OverflowException)
        { throw Error(ConnectorIssueCodes.UnsupportedPhysicalType, $"Field '{field}' has invalid declared type content; values were omitted."); }
    }

    private static decimal ParseDecimal(string value, ArtifactSelector selector, string field)
    {
        var scaleText = selector.Properties.GetValueOrDefault($"fields.{field}.scale") ?? "0";
        var divisor = Scale(scaleText);
        var parsed = decimal.Parse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent, CultureInfo.InvariantCulture);
        var result = parsed / divisor;
        if (!DecimalIsExact(value, result, int.Parse(scaleText, CultureInfo.InvariantCulture)))
            throw Error(ConnectorIssueCodes.UnsupportedPhysicalType, $"Field '{field}' exceeds supported exact decimal precision; values were omitted.");
        return result;
    }

    private void Validate(ArtifactSelector selector)
    {
        if (selector.Kind != Id.Value || selector.IdentityFields.Count == 0) throw Error(ConnectorIssueCodes.NonDeterministicIdentity, "Selector kind and explicit semantic identity are required.");
        _ = EncodingFor(selector);
        if (Id.Value != "fixed-width") return;
        var width = int.Parse(Required(selector, "width"), CultureInfo.InvariantCulture);
        var fields = DeclaredFields(selector).Select(field => (Start: int.Parse(Required(selector, $"fields.{field}.start"), CultureInfo.InvariantCulture),
            Length: int.Parse(Required(selector, $"fields.{field}.length"), CultureInfo.InvariantCulture))).OrderBy(field => field.Start).ToArray();
        var end = 0;
        foreach (var field in fields)
        {
            if (field.Start < 1 || field.Length < 1 || field.Start <= end || field.Start - 1 + field.Length > width)
                throw Error("PSCONN017", "Fixed-width schema overlaps or exceeds one-based character record width.");
            end = field.Start - 1 + field.Length;
        }
        if (fields.Length == 0) throw Error("PSCONN017", "Fixed-width reading requires declared field boundaries.");
    }

    private static decimal Scale(string text)
    {
        var scale = int.Parse(text, CultureInfo.InvariantCulture);
        if (scale is < 0 or > 28) throw Error("PSCONN017", "Decimal scale must be between 0 and 28.");
        var divisor = 1m;
        for (var index = 0; index < scale; index++) divisor *= 10m;
        return divisor;
    }

    private static XName XmlName(string name, ArtifactSelector selector)
    {
        if (name.StartsWith('{')) return XName.Get(name);
        var separator = name.IndexOf(':');
        if (separator < 0) return XName.Get(name);
        var prefix = name[..separator];
        if (!selector.Properties.TryGetValue($"namespaces.{prefix}", out var uri))
            throw Error("PSCONN019", "XML namespace prefix is not explicitly configured.");
        return XName.Get(name[(separator + 1)..], uri);
    }

    private static bool DecimalIsExact(string token, decimal value, int additionalScale = 0)
    {
        var exponentAt = token.IndexOfAny(['e', 'E']);
        var mantissa = exponentAt < 0 ? token : token[..exponentAt];
        var exponent = exponentAt < 0 ? 0 : int.TryParse(token[(exponentAt + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : int.MaxValue;
        if (exponent is < -1000 or > 1000 || mantissa.Length > 1000) return false;
        var point = mantissa.IndexOf('.');
        var scale = (point < 0 ? 0 : mantissa.Length - point - 1) - exponent + additionalScale;
        var digits = point < 0 ? mantissa : mantissa.Remove(point, 1);
        var original = BigInteger.Parse(digits, CultureInfo.InvariantCulture);
        var bits = decimal.GetBits(value);
        var normalized = (BigInteger)(uint)bits[0] + ((BigInteger)(uint)bits[1] << 32) + ((BigInteger)(uint)bits[2] << 64);
        if (bits[3] < 0) normalized = -normalized;
        var decimalScale = (bits[3] >> 16) & 0xff;
        return scale >= decimalScale ? original == normalized * BigInteger.Pow(10, scale - decimalScale) :
            original * BigInteger.Pow(10, decimalScale - scale) == normalized;
    }
    private static Encoding EncodingFor(ArtifactSelector selector) => (selector.Properties.GetValueOrDefault("encoding") ?? "utf-8") switch
    {
        "utf-8" => new UTF8Encoding(false, true), "utf-16" => new UnicodeEncoding(false, true, true),
        _ => throw Error("PSCONN018", "Unsupported declared encoding; no locale fallback is allowed.")
    };
    private static string[] DeclaredFields(ArtifactSelector selector) => selector.Properties.Keys.Where(key => key.StartsWith("fields.", StringComparison.Ordinal))
        .Select(key => key.Split('.')[1]).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    private static string ResolvePath(ConnectorContext context, ArtifactSelector selector)
    {
        var root = ConnectorPathUtilities.ResolveRoot(context);
        if (!ConnectorPathUtilities.TryResolveContainedPath(root, Required(selector, "path"), out var path) || !File.Exists(path))
            throw Error(ConnectorIssueCodes.PathOutsideRoot, "Structured file is unavailable or escapes its endpoint root.");
        return path;
    }
    private static string Required(ArtifactSelector selector, string key) => selector.Properties.TryGetValue(key, out var value)
        ? value : throw Error(ConnectorIssueCodes.MissingConfiguration, $"Selector property '{key}' is required.");
    private static object? ValueDocument(ValueNode value) => value switch
    {
        StringValue text => text.Value, IntegerValue integer => integer.Value, DecimalValue number => number.Value,
        BooleanValue boolean => boolean.Value, DateValue date => date.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        LocalDateTimeValue local => local.Value.ToString("O", CultureInfo.InvariantCulture), OffsetDateTimeValue offset => offset.Value.ToString("O", CultureInfo.InvariantCulture),
        _ => throw Error(ConnectorIssueCodes.NonDeterministicIdentity, "Identity must be a declared primitive value.")
    };
    private static ConnectorReadException Error(string code, string message) => new(code, message);
}