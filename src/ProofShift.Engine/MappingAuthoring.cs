using System.Globalization;
using System.Text.Json;
using CsvHelper;
using CsvHelper.Configuration;
using ProofShift.Connectors.Abstractions;

namespace ProofShift.Engine;

public sealed record AuthoredMapping(string SourceEntity, string SourceField, string? TargetEntity,
    string? TargetField, string Transformation, bool Required, string ReviewStatus,
    string Notes, IReadOnlyList<string> Reasons)
{
    public string Role { get; init; } = "attribute";
    public IReadOnlyList<AuthoredTransformationStep> Pipeline { get; init; } = [];
}

public sealed record AuthoredTransformationStep(string Type, string Version,
    IReadOnlyDictionary<string, string> Parameters);
public sealed record MappingReview(string Format, string SourceFingerprint, string TargetFingerprint,
    IReadOnlyList<AuthoredMapping> Mappings);
public sealed record MappingImportIssue(string Code, int Row, string Column, string Message);
public sealed record MappingImportResult(IReadOnlyList<AuthoredMapping> Mappings, IReadOnlyList<MappingImportIssue> Issues);

public interface IMappingImporter
{
    Task<MappingImportResult> ImportAsync(TextReader reader, PhysicalDiscoveryArtifact source,
        PhysicalDiscoveryArtifact target, CancellationToken cancellationToken);
}

public static class MappingScaffold
{
    public const string Format = "proofshift-mapping-scaffold-v1";

    public static MappingReview Create(PhysicalDiscoveryArtifact source, PhysicalDiscoveryArtifact target)
    {
        if (!PhysicalDiscovery.Verify(source) || !PhysicalDiscovery.Verify(target)) throw new InvalidDataException("Discovery artifact is invalid.");
        var mappings = new List<AuthoredMapping>();
        foreach (var entity in source.Objects.OrderBy(item => item.Schema, StringComparer.Ordinal).ThenBy(item => item.Name, StringComparer.Ordinal))
        {
            var entityMatches = target.Objects.Where(candidate => Normalize(candidate.Name) == Normalize(entity.Name)).ToArray();
            if (entityMatches.Length == 0)
            {
                var sourceKey = entity.Keys.FirstOrDefault(key => key.Primary);
                if (sourceKey is not null)
                    entityMatches = target.Objects.Where(candidate => candidate.Keys.Any(key => key.Primary &&
                        key.Fields.Select(Normalize).SequenceEqual(sourceKey.Fields.Select(Normalize)))).ToArray();
            }
            foreach (var field in entity.Fields.OrderBy(item => item.Ordinal))
            {
                var matches = entityMatches.SelectMany(candidate => candidate.Fields.Where(targetField =>
                    Normalize(field.Name) == Normalize(targetField.Name) && Compatible(field.NativeType, targetField.NativeType))
                    .Select(targetField => (Entity: candidate, Field: targetField))).ToArray();
                if (matches.Length != 1)
                {
                    mappings.Add(new AuthoredMapping(Name(entity), field.Name, null, null, "copy", true,
                        matches.Length > 1 ? "ambiguous" : "unmapped", "", [])
                    {
                        Role = entity.Keys.Any(key => key.Primary && key.Fields.Contains(field.Name, StringComparer.Ordinal))
                            ? "identity" : "attribute"
                    });
                    continue;
                }
                var match = matches[0];
                var reasons = new List<string> { field.Name == match.Field.Name ? "exact-name-match" : "normalized-name-match", "compatible-type" };
                var sourceIdentity = entity.Keys.Any(key => key.Primary && key.Fields.Contains(field.Name, StringComparer.Ordinal));
                var targetIdentity = match.Entity.Keys.Any(key => key.Primary && key.Fields.Contains(match.Field.Name, StringComparer.Ordinal));
                if (sourceIdentity) reasons.Add("source-primary-key");
                if (targetIdentity) reasons.Add("target-primary-key");
                mappings.Add(new AuthoredMapping(Name(entity), field.Name, Name(match.Entity), match.Field.Name,
                    "copy", true, "suggested", "", reasons)
                {
                    Role = sourceIdentity && targetIdentity ? "identity" : "attribute"
                });
            }
        }
        return new(Format, source.Fingerprint, target.Fingerprint, mappings);
    }

    public static string Name(PhysicalObject physical) => string.IsNullOrEmpty(physical.Schema) ? physical.Name : $"{physical.Schema}.{physical.Name}";
    public static string Normalize(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    public static bool Compatible(string left, string right) => Family(left) == Family(right);
    private static string Family(string type)
    {
        var value = type.ToLowerInvariant();
        if (value.StartsWith("int", StringComparison.Ordinal) || value.StartsWith("bigint", StringComparison.Ordinal) || value.StartsWith("smallint", StringComparison.Ordinal)) return "integer";
        if (value.StartsWith("decimal", StringComparison.Ordinal) || value.StartsWith("numeric", StringComparison.Ordinal)) return "decimal";
        if (value.Contains("char", StringComparison.Ordinal) || value is "string" or "text") return "text";
        if (value is "boolean" or "bool" or "bit") return "boolean";
        return value;
    }
}

public sealed class CsvMappingImporter : IMappingImporter
{
    private static readonly string[] Columns = ["Source Entity", "Source Field", "Target Entity", "Target Field", "Transformation", "Required", "Notes", "Review Status"];
    private static readonly HashSet<string> SupportedPipelineSteps = new(StringComparer.Ordinal)
    {
        "copy", "trim", "normalize-string", "normalize-date", "code-map", "concatenate", "split"
    };

    public async Task<MappingImportResult> ImportAsync(TextReader reader, PhysicalDiscoveryArtifact source,
        PhysicalDiscoveryArtifact target, CancellationToken cancellationToken)
    {
        if (!PhysicalDiscovery.Verify(source) || !PhysicalDiscovery.Verify(target)) throw new InvalidDataException("Discovery artifact is invalid.");
        using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture) { Delimiter = "," }, leaveOpen: true);
        var result = new List<AuthoredMapping>();
        var issues = new List<MappingImportIssue>();
        var mappings = new HashSet<(string SourceEntity, string SourceField, string TargetEntity, string TargetField)>();
        var targetIdentities = new HashSet<(string Entity, string Field)>();
        if (!await csv.ReadAsync().ConfigureAwait(false)) return new([], [new("PSIMPORT001", 1, "header", "Mapping CSV requires a header.")]);
        csv.ReadHeader();
        if ((csv.HeaderRecord ?? []).Distinct(StringComparer.Ordinal).Count() != (csv.HeaderRecord ?? []).Length)
            issues.Add(new("PSIMPORT001", 1, "header", "Mapping headers must be unique."));
        foreach (var column in Columns.Where(column => column != "Review Status"))
            if (!(csv.HeaderRecord ?? []).Contains(column, StringComparer.Ordinal)) issues.Add(new("PSIMPORT001", 1, column, "Required mapping header is absent."));
        if (issues.Count > 0) return new([], issues);
        while (await csv.ReadAsync().ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = csv.Parser.Row;
            string Get(string name) => csv.TryGetField<string>(name, out var value) ? value ?? "" : "";
            var sourceEntity = Get("Source Entity");
            var sourceField = Get("Source Field");
            var targetEntity = Get("Target Entity");
            var targetField = Get("Target Field");
            var transform = Get("Transformation");
            var role = string.IsNullOrEmpty(Get("Role")) ? "attribute" : Get("Role");
            var review = string.IsNullOrEmpty(Get("Review Status")) ? "suggested" : Get("Review Status");
            if (!mappings.Add((sourceEntity, sourceField, targetEntity, targetField)))
                issues.Add(new("PSIMPORT002", row, "Source Field", "The same source-to-target field mapping is duplicated."));
            if (!targetIdentities.Add((targetEntity, targetField))) issues.Add(new("PSIMPORT002", row, "Target Field", "Target field mapping is duplicated or ambiguous."));
            if (!source.Objects.Any(item => MappingScaffold.Name(item) == sourceEntity && item.Fields.Any(field => field.Name == sourceField)))
                issues.Add(new("PSIMPORT003", row, "Source Field", "Source object or field is absent from discovery."));
            if (!target.Objects.Any(item => MappingScaffold.Name(item) == targetEntity && item.Fields.Any(field => field.Name == targetField)))
                issues.Add(new("PSIMPORT004", row, "Target Field", "Target object or field is absent from discovery."));
            if (transform is not ("copy" or "trim" or "normalize-string"))
                issues.Add(new("PSIMPORT005", row, "Transformation", "Transformation is unsupported; provide a supported deterministic Pipeline instead."));
            if (!bool.TryParse(Get("Required"), out var required)) issues.Add(new("PSIMPORT006", row, "Required", "Required must be true or false."));
            if (review is not ("confirmed" or "suggested" or "unmapped")) issues.Add(new("PSIMPORT007", row, "Review Status", "Review Status must be confirmed, suggested, or unmapped."));
            if (role is not ("identity" or "attribute"))
                issues.Add(new("PSIMPORT009", row, "Role", "Role must be identity or attribute."));
            var pipeline = ParsePipeline(Get("Pipeline"), transform, row, issues);
            if (role == "identity" && pipeline.Any(step => step.Type != "copy"))
                issues.Add(new("PSIMPORT010", row, "Pipeline", "Identity mappings cannot apply value-changing transformations."));
            result.Add(new AuthoredMapping(sourceEntity, sourceField, targetEntity, targetField,
                transform, required, review, Get("Notes"), [])
            {
                Role = role,
                Pipeline = pipeline
            });
        }
        return new(result, issues);
    }

    private static List<AuthoredTransformationStep> ParsePipeline(string source, string fallback,
        int row, List<MappingImportIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            if (fallback == "copy") return [];
            return [new AuthoredTransformationStep(fallback, "1",
                new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(new Dictionary<string, string>()))];
        }

        try
        {
            using var document = JsonDocument.Parse(source);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new JsonException();
            var result = new List<AuthoredTransformationStep>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty("type", out var typeValue) || typeValue.ValueKind != JsonValueKind.String)
                    throw new JsonException();
                var propertyNames = item.EnumerateObject().Select(property => property.Name).ToArray();
                if (propertyNames.Distinct(StringComparer.Ordinal).Count() != propertyNames.Length ||
                    propertyNames.Any(name => name is not ("type" or "version" or "parameters")))
                    throw new JsonException();
                var type = typeValue.GetString()!;
                var version = "1";
                if (item.TryGetProperty("version", out var versionValue))
                {
                    if (versionValue.ValueKind != JsonValueKind.String) throw new JsonException();
                    version = versionValue.GetString()!;
                }
                if (!SupportedPipelineSteps.Contains(type) || version != "1")
                {
                    issues.Add(new("PSIMPORT008", row, "Pipeline", "Pipeline steps must use a supported deterministic type at version 1."));
                    continue;
                }

                var parameters = new SortedDictionary<string, string>(StringComparer.Ordinal);
                if (item.TryGetProperty("parameters", out var parameterValues))
                {
                    if (parameterValues.ValueKind != JsonValueKind.Object) throw new JsonException();
                    foreach (var parameter in parameterValues.EnumerateObject())
                    {
                        if (parameter.Value.ValueKind != JsonValueKind.String ||
                            !parameters.TryAdd(parameter.Name, parameter.Value.GetString()!))
                            throw new JsonException();
                    }
                }
                if (type == "normalize-date" &&
                        (!parameters.TryGetValue("format", out var format) || string.IsNullOrWhiteSpace(format)) ||
                    type == "split" && (!parameters.TryGetValue("separator", out var separator) || separator.Length == 0) ||
                    type == "concatenate" && !parameters.TryGetValue("fields", out var fields))
                {
                    issues.Add(new("PSIMPORT008", row, "Pipeline", $"Pipeline step '{type}' is missing required parameters."));
                    continue;
                }
                result.Add(new AuthoredTransformationStep(type, version,
                    new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(parameters)));
            }
            return result;
        }
        catch (JsonException)
        {
            issues.Add(new("PSIMPORT008", row, "Pipeline", "Pipeline must be a JSON array of supported step objects with string parameters."));
            return [];
        }
    }

}