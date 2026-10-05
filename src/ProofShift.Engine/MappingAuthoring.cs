using System.Globalization;
using System.Text.Json;
using CsvHelper;
using CsvHelper.Configuration;
using ProofShift.Connectors.Abstractions;

namespace ProofShift.Engine;

public sealed record AuthoredMapping(string SourceEntity, string SourceField, string? TargetEntity,
    string? TargetField, string Transformation, bool Required, string ReviewStatus,
    string Notes, IReadOnlyList<string> Reasons);
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
                    mappings.Add(new(Name(entity), field.Name, null, null, "copy", true,
                        matches.Length > 1 ? "ambiguous" : "unmapped", "", []));
                    continue;
                }
                var match = matches[0];
                var reasons = new List<string> { field.Name == match.Field.Name ? "exact-name-match" : "normalized-name-match", "compatible-type" };
                if (entity.Keys.Any(key => key.Primary && key.Fields.Contains(field.Name, StringComparer.Ordinal))) reasons.Add("source-primary-key");
                if (match.Entity.Keys.Any(key => key.Primary && key.Fields.Contains(match.Field.Name, StringComparer.Ordinal))) reasons.Add("target-primary-key");
                mappings.Add(new(Name(entity), field.Name, Name(match.Entity), match.Field.Name, "copy", true, "suggested", "", reasons));
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

    public async Task<MappingImportResult> ImportAsync(TextReader reader, PhysicalDiscoveryArtifact source,
        PhysicalDiscoveryArtifact target, CancellationToken cancellationToken)
    {
        if (!PhysicalDiscovery.Verify(source) || !PhysicalDiscovery.Verify(target)) throw new InvalidDataException("Discovery artifact is invalid.");
        using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture) { Delimiter = "," }, leaveOpen: true);
        var result = new List<AuthoredMapping>();
        var issues = new List<MappingImportIssue>();
        var identities = new HashSet<(string Entity, string Field)>();
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
            var review = string.IsNullOrEmpty(Get("Review Status")) ? "suggested" : Get("Review Status");
            if (!identities.Add((sourceEntity, sourceField))) issues.Add(new("PSIMPORT002", row, "Source Field", "Source field mapping is duplicated or ambiguous."));
            if (!targetIdentities.Add((targetEntity, targetField))) issues.Add(new("PSIMPORT002", row, "Target Field", "Target field mapping is duplicated or ambiguous."));
            if (!source.Objects.Any(item => MappingScaffold.Name(item) == sourceEntity && item.Fields.Any(field => field.Name == sourceField)))
                issues.Add(new("PSIMPORT003", row, "Source Field", "Source object or field is absent from discovery."));
            if (!target.Objects.Any(item => MappingScaffold.Name(item) == targetEntity && item.Fields.Any(field => field.Name == targetField)))
                issues.Add(new("PSIMPORT004", row, "Target Field", "Target object or field is absent from discovery."));
            if (transform is not ("copy" or "trim" or "normalize-string")) issues.Add(new("PSIMPORT005", row, "Transformation", "Transformation is unsupported; explicit artifact exclusions belong in the migration graph."));
            if (!bool.TryParse(Get("Required"), out var required)) issues.Add(new("PSIMPORT006", row, "Required", "Required must be true or false."));
            if (review is not ("confirmed" or "suggested" or "unmapped")) issues.Add(new("PSIMPORT007", row, "Review Status", "Review Status must be confirmed, suggested, or unmapped."));
            result.Add(new(sourceEntity, sourceField, targetEntity, targetField, transform, required, review, Get("Notes"), []));
        }
        return new(result, issues);
    }

}