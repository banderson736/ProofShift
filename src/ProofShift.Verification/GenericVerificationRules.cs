using System.Globalization;
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
        new("entity-uniqueness", definition => new EntityUniquenessRule(definition),
            new("entity-uniqueness", "1", "Require unique target identities in the configured graph scope.", VerificationScope.Entity,
                [new("targetNode", RuleOptionKind.Text, "Target graph node."), new("semanticType", RuleOptionKind.SemanticTypeReference, "Semantic type scope.")],
                partitionExecution: VerificationPartitionExecution.PartitionLocal,
                partitionKey: new VerificationPartitionKeyDefinition(VerificationPartitionBasis.ArtifactIdentity,
                    VerificationArtifactRole.ActualTarget)))
    ];
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

public sealed class MemberAccountingRule(VerificationRuleDefinition definition) : VerificationRuleBase(definition)
{
    public override VerificationScope Scope => VerificationScope.Accounting;

    public override async IAsyncEnumerable<VerificationFinding> EvaluateAsync(VerificationExecutionContext context,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var semanticType = Option("semanticType", "Pension.Member");
        var memberSources = 0;
        var unaccounted = 0;
        var accountedCount = 0;
        await foreach (var source in context.Workspace.ReadSourceFactsAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (source.SemanticType != semanticType) continue;
            memberSources++;
            var isAccounted = source.FailedEntries == 0 &&
                ((source.ProducedEntries > 0) ^ (source.ExcludedEntries > 0));
            if (isAccounted)
            {
                accountedCount++;
                continue;
            }
            unaccounted++;
            yield return Finding(context, EvidenceType.Accounting, EvidenceResult.Fail, "UnaccountedSource",
                $"{Id.Value}:{source.NodeKey}:{source.Artifact.Id.Value}",
                "Pension member source is unaccounted or has conflicting dispositions.",
                [ArtifactInput(context, source.NodeKey, source.Artifact)]);
        }

        if (memberSources == 0)
            yield return Finding(context, EvidenceType.Accounting, EvidenceResult.NotApplicable, "MemberAccountingNotApplicable",
                $"{Id.Value}:none", "No source artifacts match the configured pension member semantic type.");
        else if (accountedCount > 0)
            yield return Finding(context, EvidenceType.Accounting, EvidenceResult.Pass, "MemberAccounting",
            $"{Id.Value}:population", $"{accountedCount.ToString(CultureInfo.InvariantCulture)} of {memberSources.ToString(CultureInfo.InvariantCulture)} pension member source artifacts are accounted for.",
            context.BindingReferences, new EvidenceValue(new IntegerValue(memberSources)),
            new EvidenceValue(new IntegerValue(accountedCount)));
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
