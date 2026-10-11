using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ProofShift.Domain;

namespace ProofShift.Verification;

public sealed class GenericVerificationRuleProvider : IVerificationRuleProvider
{
    public string Id => "proofshift.verification.generic";
    public string Version => "1";
    public IReadOnlyCollection<VerificationRuleFactory> RuleFactories { get; } =
    [
        new("source-artifact-accounting", definition => new SourceDispositionRule(definition),
            new("source-artifact-accounting", "1", "Account for every checkpoint source through explicit graph dispositions.", VerificationScope.Accounting)),
        new("target-lineage", definition => new TargetLineageRule(definition),
            new("target-lineage", "1", "Require graph-scoped provenance for every observed target.", VerificationScope.Accounting)),
        new("target-presence", definition => new TargetPresenceRule(definition),
            new("target-presence", "1", "Compare graph-derived expected and observed target presence.", VerificationScope.Entity,
                partitionExecution: VerificationPartitionExecution.PartitionPartialWithGlobalMerge,
                partitionKey: new VerificationPartitionKeyDefinition(VerificationPartitionBasis.ArtifactIdentity,
                    VerificationArtifactRole.ActualTarget))),
        new("unexpected-target", definition => new UnexpectedTargetRule(definition),
            new("unexpected-target", "1", "Reject observed targets not explained by the migration graph.", VerificationScope.Entity,
                partitionExecution: VerificationPartitionExecution.PartitionLocal,
                partitionKey: new VerificationPartitionKeyDefinition(VerificationPartitionBasis.ArtifactIdentity,
                    VerificationArtifactRole.ActualTarget))),
        new("attribute-comparison", definition => new AttributeComparisonRule(definition),
            new("attribute-comparison", "1", "Compare configured mapped attribute values against independent target observations.", VerificationScope.Attribute,
                [new("attribute", RuleOptionKind.FieldReference, "Mapped target field; omitted means all mapped attributes."),
                 new("targetNode", RuleOptionKind.Text, "Target graph node."), new("semanticType", RuleOptionKind.SemanticTypeReference, "Semantic type scope.")],
                fieldRequirements: [new RuleFieldRequirement("attribute", VerificationFieldSide.Target,
                    includeMappedTargetFieldsWhenUnset: true, targetNodeOption: "targetNode")],
                partitionExecution: VerificationPartitionExecution.PartitionPartialWithGlobalMerge,
                partitionKey: new VerificationPartitionKeyDefinition(VerificationPartitionBasis.ArtifactIdentity,
                    VerificationArtifactRole.ActualTarget))),
        new("effective-dated-interval", definition => new EffectiveDatedIntervalRule(definition),
            new("effective-dated-interval", "1", "Validate configured date-valued intervals, overlap, and optional continuity by owner.", VerificationScope.Timeline,
                [new("targetNode", RuleOptionKind.Text, "Target graph node containing intervals.", Required: true),
                 new("semanticType", RuleOptionKind.SemanticTypeReference, "Interval semantic type.", Required: true),
                 new("identityField", RuleOptionKind.FieldReference, "Interval identity field.", Required: true),
                 new("ownerField", RuleOptionKind.FieldReference, "Business owner used to group intervals.", Required: true),
                 new("startField", RuleOptionKind.FieldReference, "Inclusive interval start date.", Required: true),
                 new("endField", RuleOptionKind.FieldReference, "Inclusive interval end date; null denotes open-ended.", Required: true),
                 new("requireContinuous", RuleOptionKind.Logical, "Require adjacent intervals to meet on consecutive dates.", Default: new BooleanValue(false))],
                fieldRequirements:
                [
                    new RuleFieldRequirement("identityField", VerificationFieldSide.Target),
                    new RuleFieldRequirement("ownerField", VerificationFieldSide.Target, keyRole: VerificationOrderingRole.Grouping),
                    new RuleFieldRequirement("startField", VerificationFieldSide.Target, keyRole: VerificationOrderingRole.Ordering),
                    new RuleFieldRequirement("endField", VerificationFieldSide.Target)
                ], partitionExecution: VerificationPartitionExecution.Global)),
        new("entity-uniqueness", definition => new EntityUniquenessRule(definition),
            new("entity-uniqueness", "1", "Require unique target identities in the configured graph scope.", VerificationScope.Entity,
                [new("targetNode", RuleOptionKind.Text, "Target graph node."), new("semanticType", RuleOptionKind.SemanticTypeReference, "Semantic type scope.")],
                partitionExecution: VerificationPartitionExecution.PartitionLocal,
                partitionKey: new VerificationPartitionKeyDefinition(VerificationPartitionBasis.ArtifactIdentity,
                    VerificationArtifactRole.ActualTarget)))
    ];
}

public sealed class EffectiveDatedIntervalRule(VerificationRuleDefinition definition) : VerificationRuleBase(definition)
{
    public override VerificationScope Scope => VerificationScope.Timeline;
    public override IReadOnlyCollection<VerificationOrderingKey> RequiredOrderingKeys =>
    [
        new VerificationOrderingKey(Option("semanticType"), Option("ownerField"), VerificationOrderingRole.Grouping),
        new VerificationOrderingKey(Option("semanticType"), Option("startField"), VerificationOrderingRole.Ordering)
    ];

    public override async IAsyncEnumerable<VerificationFinding> EvaluateAsync(VerificationExecutionContext context,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var identityField = Option("identityField");
        var ownerField = Option("ownerField");
        var startField = Option("startField");
        var endField = Option("endField");
        var requireContinuous = string.Equals(Option("requireContinuous", "false"), "true", StringComparison.OrdinalIgnoreCase);
        string? currentOwner = null;
        DateOnly? previousEnd = null;
        VerificationArtifactRecord? previousRecord = null;
        var previousIsOpenEnded = false;
        var failures = 0;
        await foreach (var record in context.Workspace.ReadArtifactRecordsByKeysAsync(VerificationArtifactRole.ActualTarget,
            Option("targetNode"), Option("semanticType"), RequiredOrderingKeys, cancellationToken)
            .WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var owner = ReadText(record, ownerField);
            var identity = ReadText(record, identityField);
            var start = ReadDate(record, startField);
            var end = ReadDate(record, endField);
            if (owner.Length == 0 || identity.Length == 0 || !EffectiveDatedIntervalSemantics.IsValid(start, end))
            {
                failures++;
                yield return Finding(context, EvidenceType.Timeline, EvidenceResult.Fail, "EffectiveIntervalInvalid",
                    $"{Id.Value}:invalid:{Fingerprint(identity)}",
                    "A configured effective interval requires an owner, identity, valid date start, and a null or non-earlier end date.",
                    [ArtifactInput(context, record.NodeKey, record.Artifact)], IntervalValue(start, end),
                    new EvidenceValue(new StringValue("invalid-interval")));
            }
            if (!string.Equals(currentOwner, owner, StringComparison.Ordinal))
            {
                currentOwner = owner;
                previousEnd = null;
                previousRecord = null;
                previousIsOpenEnded = false;
            }
            if (previousRecord is not null && EffectiveDatedIntervalSemantics.Overlaps(previousIsOpenEnded, previousEnd, start))
            {
                failures++;
                yield return Finding(context, EvidenceType.Timeline, EvidenceResult.Fail, "EffectiveIntervalOverlap",
                    $"{Id.Value}:overlap:{Fingerprint(owner)}:{Fingerprint(identity)}",
                    "Configured inclusive effective-date intervals overlap or follow an open-ended interval for one owner.",
                    [ArtifactInput(context, record.NodeKey, previousRecord.Artifact), ArtifactInput(context, record.NodeKey, record.Artifact)],
                    IntervalValue(ReadDate(previousRecord, startField), previousEnd), IntervalValue(start, end));
            }
            else if (EffectiveDatedIntervalSemantics.HasGap(requireContinuous, previousEnd, start))
            {
                failures++;
                var lastDate = previousEnd!.Value;
                var nextStart = start!.Value;
                yield return Finding(context, EvidenceType.Timeline, EvidenceResult.Fail, "EffectiveIntervalGap",
                    $"{Id.Value}:gap:{Fingerprint(owner)}:{Fingerprint(identity)}",
                    "Configured sequential intervals contain a gap between inclusive effective dates.",
                    [ArtifactInput(context, record.NodeKey, previousRecord!.Artifact), ArtifactInput(context, record.NodeKey, record.Artifact)],
                    new EvidenceValue(new DateValue(lastDate.AddDays(1))), new EvidenceValue(new DateValue(nextStart)));
            }
            previousRecord = record;
            previousEnd = end;
            previousIsOpenEnded = end is null;
        }
        if (failures == 0)
            yield return Finding(context, EvidenceType.Timeline, EvidenceResult.Pass, "EffectiveIntervalsValid",
                $"{Id.Value}:complete", "Configured date-valued intervals are valid and satisfy owner-level overlap/continuity policy.", context.BindingReferences);
    }

    private static string ReadText(VerificationArtifactRecord record, string field)
    {
        record.RequireDeclaredField(field);
        return record.Values.TryGetValue(field, out var value) ? value switch
        {
            NullValue => string.Empty,
            StringValue text => text.Value,
            IntegerValue integer => integer.Value.ToString(CultureInfo.InvariantCulture),
            _ => string.Empty
        } : string.Empty;
    }

    private static DateOnly? ReadDate(VerificationArtifactRecord record, string field)
    {
        record.RequireDeclaredField(field);
        if (!record.Values.TryGetValue(field, out var value) || value is NullValue) return null;
        return value switch
        {
            DateValue date => date.Value,
            LocalDateTimeValue local => DateOnly.FromDateTime(local.Value),
            StringValue text when DateOnly.TryParseExact(text.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsed) => parsed,
            _ => null
        };
    }

    private static EvidenceValue IntervalValue(DateOnly? start, DateOnly? end) => new(new ObjectValue([
        new KeyValuePair<string, ValueNode>("start", start is { } from ? new DateValue(from) : new NullValue()),
        new KeyValuePair<string, ValueNode>("end", end is { } to ? new DateValue(to) : new NullValue())
    ]));

    private static string Fingerprint(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

internal static class EffectiveDatedIntervalSemantics
{
    public static bool IsValid(DateOnly? start, DateOnly? end) =>
        start is not null && (end is null || end >= start);

    public static bool Overlaps(bool previousIsOpenEnded, DateOnly? previousEnd, DateOnly? currentStart) =>
        previousIsOpenEnded || previousEnd is { } end && currentStart is { } start && start <= end;

    public static bool HasGap(bool requireContinuous, DateOnly? previousEnd, DateOnly? currentStart) =>
        requireContinuous && previousEnd is { } end && currentStart is { } start && start > end.AddDays(1);
}

public abstract class VerificationRuleBase : IVerificationRule
{
    protected VerificationRuleDefinition Definition { get; }
    public RuleId Id => Definition.Id;
    public string Version => Definition.Version;
    public abstract VerificationScope Scope { get; }
    public virtual IReadOnlyCollection<VerificationOrderingKey> RequiredOrderingKeys => [];
    public abstract IAsyncEnumerable<VerificationFinding> EvaluateAsync(
        VerificationExecutionContext context,
        CancellationToken cancellationToken);

    protected VerificationRuleBase(VerificationRuleDefinition definition) =>
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));

    protected VerificationFinding Finding(VerificationExecutionContext context, EvidenceType type, EvidenceResult result,
        string code, string stableKey, string explanation, IEnumerable<EvidenceReference>? inputs = null,
        EvidenceValue? expected = null, EvidenceValue? actual = null) =>
        new(type, result, Definition.Severity, code, explanation, stableKey,
            IncludeBindings(context, inputs), expected, actual, Id, Version);

    private static List<EvidenceReference> IncludeBindings(VerificationExecutionContext context,
        IEnumerable<EvidenceReference>? inputs)
    {
        var references = context.BindingReferences.ToList();
        if (inputs is not null) references.AddRange(inputs);
        return references;
    }

    protected string Option(string name, string? defaultValue = null)
    {
        if (!Definition.StructuredOptions.TryGetValue(name, out var value)) return defaultValue ?? string.Empty;
        return value switch
        {
            StringValue text => text.Value,
            BooleanValue boolean => boolean.Value ? "true" : "false",
            IntegerValue integer => integer.Value.ToString(CultureInfo.InvariantCulture),
            DecimalValue number => number.Value.ToString("G29", CultureInfo.InvariantCulture),
            _ => throw new VerificationRuleException("PSRULE003", $"Rule option '{name}' must be scalar.")
        };
    }

    protected string[] OptionList(string name, string[] defaults)
    {
        if (!Definition.StructuredOptions.TryGetValue(name, out var value)) return defaults;
        if (value is CollectionValue sequence) return sequence.Values.Cast<StringValue>().Select(item => item.Value).ToArray();
        if (!Definition.UsesStructuredOptions && value is StringValue text)
            return string.IsNullOrWhiteSpace(text.Value) ? defaults :
                text.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        throw new VerificationRuleException("PSRULE003", $"Rule option '{name}' must be a list of field references.");
    }

    protected static EvidenceValue HashValue(string fingerprint) => new(new StringValue($"sha256:{fingerprint}"));

    protected static EvidenceReference ArtifactInput(VerificationExecutionContext context, string nodeKey, ArtifactReference artifact)
    {
        var node = context.Graph.Nodes.SingleOrDefault(candidate => candidate.Name == nodeKey)
            ?? throw new VerificationRuleException(VerificationIssueCodes.ContextMismatch, "Evidence references an artifact in an unknown graph node.");
        return new EvidenceReference(artifactId: artifact.Id, graphNodeId: node.Id);
    }
}

public sealed class SourceDispositionRule(VerificationRuleDefinition definition) : VerificationRuleBase(definition)
{
    public override VerificationScope Scope => VerificationScope.Accounting;

    public override async IAsyncEnumerable<VerificationFinding> EvaluateAsync(VerificationExecutionContext context,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        long count = 0;
        long migrated = 0;
        long excluded = 0;
        long failures = 0;
        var sourceFacts = context.ExternalObservation is null
            ? context.Workspace.ReadSourceFactsAsync(cancellationToken)
            : context.Workspace.ReadGraphDerivedSourceFactsAsync(cancellationToken);
        await foreach (var source in sourceFacts.ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            count++;
            var hasProduced = source.ProducedEntries > 0;
            var hasExcluded = source.ExcludedEntries > 0;
            var mixed = hasProduced && hasExcluded || source.FailedEntries > 0 && (hasProduced || hasExcluded);
            var passed = !mixed && (hasProduced ^ hasExcluded) && source.FailedEntries == 0;
            var code = mixed ? "DuplicateDisposition" : passed ? "SourceDisposition" : "UnaccountedSource";
            var disposition = hasProduced ? "materialized" : hasExcluded ? "excluded" : "unaccounted";
            if (passed)
            {
                if (hasProduced) migrated++;
                else excluded++;
                continue;
            }

            failures++;
            yield return Finding(context, EvidenceType.Accounting, EvidenceResult.Fail, code,
                $"{Id.Value}:{source.NodeKey}:{source.Artifact.Id.Value}",
                mixed ? "Source artifact has conflicting or failed projection journal dispositions."
                    : "Source artifact has no successful materialized or explicit excluded disposition.",
                [ArtifactInput(context, source.NodeKey, source.Artifact), .. source.EdgeIds.Select(edge => new EvidenceReference(migrationEdgeId: edge))],
                new EvidenceValue(new StringValue("exactly-one-final-disposition")),
                new EvidenceValue(new StringValue(disposition)));
        }

        if (count == 0)
            yield return Finding(context, EvidenceType.Accounting, EvidenceResult.Fail, "UnaccountedSource",
                $"{Id.Value}:empty-source-set", "The checkpoint contains no source artifacts relevant to verification.");
        else if (failures == 0)
            yield return Finding(context, EvidenceType.Accounting, EvidenceResult.Pass, "SourceDisposition",
                $"{Id.Value}:population-complete",
                $"All {count.ToString(CultureInfo.InvariantCulture)} source artifacts have exactly one explainable disposition ({migrated.ToString(CultureInfo.InvariantCulture)} materialized, {excluded.ToString(CultureInfo.InvariantCulture)} explicitly excluded).",
                context.BindingReferences,
                new EvidenceValue(new IntegerValue(count)),
                new EvidenceValue(new ObjectValue([
                    new KeyValuePair<string, ValueNode>("materialized", new IntegerValue(migrated)),
                    new KeyValuePair<string, ValueNode>("excluded", new IntegerValue(excluded))])));
    }
}

public sealed class TargetLineageRule(VerificationRuleDefinition definition) : VerificationRuleBase(definition)
{
    public override VerificationScope Scope => VerificationScope.Accounting;

    public override async IAsyncEnumerable<VerificationFinding> EvaluateAsync(VerificationExecutionContext context,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var failures = 0;
        var missingLineage = context.ExternalObservation is null
            ? context.Workspace.ReadTargetsWithoutLineageAsync(cancellationToken)
            : context.Workspace.ReadTargetsWithoutGraphDerivedLineageAsync(cancellationToken);
        await foreach (var target in missingLineage.ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            failures++;
            yield return Finding(context, EvidenceType.Accounting, EvidenceResult.Fail, "MissingLineage",
                $"{Id.Value}:{target.NodeKey}:{target.Artifact.Id.Value}", "Observed target artifact has no graph-derived source mapping and migration-edge lineage.",
                        [ArtifactInput(context, target.NodeKey, target.Artifact)]);
        }

        if (failures == 0)
            yield return Finding(context, EvidenceType.Accounting, EvidenceResult.Pass, "TargetLineage",
                $"{Id.Value}:complete", context.ExternalObservation is null
                    ? "Every observed materialized target has at least one execution-observed source lineage path."
                    : "Every observed target has at least one graph-derived expected lineage path; external execution lineage was not observed.",
                context.BindingReferences);
    }
}

public sealed class TargetPresenceRule(VerificationRuleDefinition definition) : VerificationRuleBase(definition), IVerificationPartitionFindingMerger
{
    public override VerificationScope Scope => VerificationScope.Entity;

    public override async IAsyncEnumerable<VerificationFinding> EvaluateAsync(VerificationExecutionContext context,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var missing = 0;
        var observed = 0;
        var expectedTargets = context.ExternalObservation is null
            ? context.Workspace.ReadMaterializedJournalTargetsAsync(cancellationToken)
            : context.Workspace.ReadGraphDerivedTargetFactsAsync(cancellationToken);
        await foreach (var expected in expectedTargets.ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            observed++;
            if (expected.ActualCount > 0) continue;
            missing++;
            yield return Finding(context, EvidenceType.Comparison, EvidenceResult.Fail, "MissingTarget",
                $"{Id.Value}:{expected.NodeKey}:{expected.Artifact.Id.Value}", context.ExternalObservation is null
                    ? "Projection journal marks this target materialized, but physical shadow read-back did not find it."
                    : "The source checkpoint and migration graph imply this target, but external target observation did not find it.",
                        [ArtifactInput(context, expected.NodeKey, expected.Artifact), .. expected.Sources.Select(source => ArtifactInput(context, source.NodeKey, source.Artifact)),
                         .. expected.EdgeIds.Select(edge => new EvidenceReference(migrationEdgeId: edge))],
                new EvidenceValue(new StringValue("present")), new EvidenceValue(new StringValue("missing")));
        }

        if (context.EvaluationPartition is { } partitionIndex)
        {
            yield return Finding(context, EvidenceType.Comparison, EvidenceResult.Pass, "TargetPresencePartitionPartial",
                $"{Id.Value}:partition:{partitionIndex.ToString(CultureInfo.InvariantCulture)}", "Partition-local target-presence summary.",
                context.BindingReferences, new EvidenceValue(new IntegerValue(observed)), new EvidenceValue(new IntegerValue(missing)));
        }
        else if (observed == 0)
            yield return Finding(context, EvidenceType.Comparison, EvidenceResult.Fail, "MissingTarget",
                $"{Id.Value}:no-materialized-ancestry", "Projection journal contains no materialized target ancestry.");
        else if (missing == 0)
            yield return Finding(context, EvidenceType.Comparison, EvidenceResult.Pass, "TargetPresence",
                $"{Id.Value}:complete", "Every journal-materialized target artifact is present in physical shadow read-back.");
    }

    public IReadOnlyCollection<VerificationFinding> MergePartitionFindings(VerificationExecutionContext context,
        IReadOnlyList<IReadOnlyList<VerificationFinding>> partitionFindings)
    {
        var all = partitionFindings.SelectMany(items => items).ToArray();
        var partials = all.Where(finding => finding.Code == "TargetPresencePartitionPartial").ToArray();
        var observed = partials.Sum(finding => ((IntegerValue)finding.Expected!.Value).Value);
        var missing = partials.Sum(finding => ((IntegerValue)finding.Actual!.Value).Value);
        var failures = all.Where(finding => finding.Code == "MissingTarget" && finding.Result == EvidenceResult.Fail)
            .OrderBy(finding => finding.StableKey, StringComparer.Ordinal).ToArray();
        if (observed == 0)
            return [Finding(context, EvidenceType.Comparison, EvidenceResult.Fail, "MissingTarget",
                $"{Id.Value}:no-materialized-ancestry", "Projection journal contains no materialized target ancestry.")];
        if (missing > 0) return failures;
        return [Finding(context, EvidenceType.Comparison, EvidenceResult.Pass, "TargetPresence",
            $"{Id.Value}:complete", "Every journal-materialized target artifact is present in physical shadow read-back.")];
    }
}

public sealed class UnexpectedTargetRule(VerificationRuleDefinition definition) : VerificationRuleBase(definition)
{
    public override VerificationScope Scope => VerificationScope.Entity;

    public override async IAsyncEnumerable<VerificationFinding> EvaluateAsync(VerificationExecutionContext context,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var unexpected = 0;
        var unexpectedTargets = context.ExternalObservation is null
            ? context.Workspace.ReadUnexpectedTargetFactsAsync(cancellationToken)
            : context.Workspace.ReadUnexpectedGraphTargetFactsAsync(cancellationToken);
        await foreach (var target in unexpectedTargets.ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            unexpected++;
            yield return Finding(context, EvidenceType.Accounting, EvidenceResult.Fail, "UnexpectedTarget",
                $"{Id.Value}:{target.NodeKey}:{target.Artifact.Id.Value}", context.ExternalObservation is null
                    ? "Physical shadow state contains a target artifact absent from successful materialized journal ancestry."
                    : "Observed target state contains an artifact absent from the checkpoint/graph-derived expected target set.",
                [ArtifactInput(context, target.NodeKey, target.Artifact)],
                new EvidenceValue(new StringValue("journal-backed")), new EvidenceValue(new StringValue("unexplained")));
        }

        if (unexpected == 0)
            yield return Finding(context, EvidenceType.Accounting, EvidenceResult.Pass, "UnexpectedTarget",
                $"{Id.Value}:none", "No unexplained target artifact was observed.");
    }
}

public sealed class EntityUniquenessRule(VerificationRuleDefinition definition) : VerificationRuleBase(definition)
{
    public override VerificationScope Scope => VerificationScope.Entity;

    public override async IAsyncEnumerable<VerificationFinding> EvaluateAsync(VerificationExecutionContext context,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var duplicates = 0;
        var semanticType = Option("semanticType");
        var nodeKey = Option("targetNode");
        await foreach (var target in context.Workspace.ReadDuplicateTargetFactsAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (semanticType.Length > 0 && target.SemanticType != semanticType || nodeKey.Length > 0 && target.NodeKey != nodeKey) continue;
            duplicates++;
            yield return Finding(context, EvidenceType.Comparison, EvidenceResult.Fail, "DuplicateTarget",
                $"{Id.Value}:{target.NodeKey}:{target.Artifact.Id.Value}", "More than one physical target artifact has the same graph-node identity.",
                [ArtifactInput(context, target.NodeKey, target.Artifact)],
                new EvidenceValue(new IntegerValue(1)), new EvidenceValue(new IntegerValue(target.ActualCount)));
        }

        if (duplicates == 0)
            yield return Finding(context, EvidenceType.Comparison, EvidenceResult.Pass, "EntityUniqueness",
                $"{Id.Value}:unique", "No duplicate target identity was observed for the configured entity scope.");
    }
}

public sealed class AttributeComparisonRule(VerificationRuleDefinition definition) : VerificationRuleBase(definition), IVerificationPartitionFindingMerger
{
    public override VerificationScope Scope => VerificationScope.Attribute;

    public override async IAsyncEnumerable<VerificationFinding> EvaluateAsync(VerificationExecutionContext context,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var compared = 0;
        var failures = 0;
        long matching = 0;
        var field = Option("attribute");
        var nodeKey = Option("targetNode");
        var semanticType = Option("semanticType");
        await foreach (var comparison in context.Workspace.ReadAttributeComparisonsAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (field.Length > 0 && comparison.Field != field || nodeKey.Length > 0 && comparison.NodeKey != nodeKey ||
                semanticType.Length > 0 && comparison.SemanticType != semanticType) continue;
            compared++;
            if (comparison.Matches)
            {
                matching++;
                continue;
            }

            failures++;
            yield return Finding(context, EvidenceType.Comparison, EvidenceResult.Fail, "AttributeMismatch",
                $"{Id.Value}:{comparison.NodeKey}:{comparison.Source.Id.Value}:{comparison.Field}",
                $"Target attribute '{comparison.Field}' differs from the graph-derived transformed source value; raw values are redacted.",
                        [ArtifactInput(context, comparison.SourceNodeKey, comparison.Source),
                         ArtifactInput(context, comparison.NodeKey, comparison.ExpectedTarget),
                         ArtifactInput(context, comparison.NodeKey, comparison.ActualTarget),
                         new EvidenceReference(migrationEdgeId: comparison.EdgeId)],
                HashValue(comparison.ExpectedFingerprint), HashValue(comparison.ActualFingerprint));
        }

        if (context.EvaluationPartition is { } partitionIndex)
        {
            yield return Finding(context, EvidenceType.Comparison, EvidenceResult.Pass, "AttributeComparisonPartitionPartial",
                $"{Id.Value}:partition:{partitionIndex.ToString(CultureInfo.InvariantCulture)}", "Partition-local attribute-comparison summary.",
                context.BindingReferences, new EvidenceValue(new IntegerValue(compared)), new EvidenceValue(new IntegerValue(matching)));
        }
        else if (compared == 0)
            yield return Finding(context, EvidenceType.Comparison, EvidenceResult.NotApplicable, "AttributeNotApplicable",
                $"{Id.Value}:no-comparisons", "No expected/actual attributes matched the configured rule scope.");
        else if (matching > 0)
            yield return Finding(context, EvidenceType.Comparison, EvidenceResult.Pass, "AttributeComparison",
                $"{Id.Value}:population", $"{matching.ToString(CultureInfo.InvariantCulture)} of {compared.ToString(CultureInfo.InvariantCulture)} configured attribute comparisons matched.",
                context.BindingReferences, new EvidenceValue(new IntegerValue(compared)),
                new EvidenceValue(new IntegerValue(matching)));
    }

    public IReadOnlyCollection<VerificationFinding> MergePartitionFindings(VerificationExecutionContext context,
        IReadOnlyList<IReadOnlyList<VerificationFinding>> partitionFindings)
    {
        var all = partitionFindings.SelectMany(items => items).ToArray();
        var partials = all.Where(finding => finding.Code == "AttributeComparisonPartitionPartial").ToArray();
        var compared = partials.Sum(finding => ((IntegerValue)finding.Expected!.Value).Value);
        var matching = partials.Sum(finding => ((IntegerValue)finding.Actual!.Value).Value);
        var merged = all.Where(finding => finding.Code != "AttributeComparisonPartitionPartial").ToList();
        if (compared == 0)
            merged.Add(Finding(context, EvidenceType.Comparison, EvidenceResult.NotApplicable, "AttributeNotApplicable",
                $"{Id.Value}:no-comparisons", "No expected/actual attributes matched the configured rule scope."));
        else if (matching > 0)
            merged.Add(Finding(context, EvidenceType.Comparison, EvidenceResult.Pass, "AttributeComparison",
                $"{Id.Value}:population", $"{matching.ToString(CultureInfo.InvariantCulture)} of {compared.ToString(CultureInfo.InvariantCulture)} configured attribute comparisons matched.",
                context.BindingReferences, new EvidenceValue(new IntegerValue(compared)),
                new EvidenceValue(new IntegerValue(matching))));
        return merged.OrderBy(finding => finding.StableKey, StringComparer.Ordinal).ToArray();
    }
}

internal static class GenericRuleFactories
{
    public static IReadOnlyCollection<VerificationRuleFactory> Create() =>
    [
        new("source-artifact-accounting", definition => new SourceDispositionRule(definition)),
        new("target-lineage", definition => new TargetLineageRule(definition)),
        new("target-presence", definition => new TargetPresenceRule(definition)),
        new("unexpected-target", definition => new UnexpectedTargetRule(definition)),
        new("attribute-comparison", definition => new AttributeComparisonRule(definition)),
        new("entity-uniqueness", definition => new EntityUniquenessRule(definition))
    ];
}

internal static class FindingReferences
{
    public static EvidenceReference[] Bindings(VerificationExecutionContext context) => context.BindingReferences.ToArray();
}
