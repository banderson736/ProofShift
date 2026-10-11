using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using ProofShift.Domain;
using ProofShift.Packs.Abstractions;
using ProofShift.Verification;

namespace ProofShift.Packs.Hcm;

public sealed class HcmPack : IDomainPack
{
    public string Id => "proofshift.hcm";
    public string Version => "0.10.0";
    public IReadOnlyCollection<VerificationRuleFactory> RuleFactories { get; }
    public DomainPackMetadata Metadata { get; }

    public HcmPack()
    {
        RuleFactories =
        [
            CreateReferenceFactory(),
            CreateCurrentEmploymentFactory(),
            CreateExactValueFactory(),
            CreatePayrollFactory()
        ];
        Metadata = new DomainPackMetadata(Id, Version, "ERP and HCM Assurance",
        [
            new("HCM.BenefitEnrollment", "Benefit enrollment", "A worker's configured benefit selection and effective period."),
            new("HCM.Compensation", "Compensation", "An effective-dated exact compensation amount and currency."),
            new("HCM.CostCenter", "Cost center", "An independently identified financial organization unit."),
            new("HCM.Employment", "Employment", "An effective-dated worker employment segment."),
            new("HCM.LeaveBalance", "Leave balance", "An exact worker leave balance by configured leave type."),
            new("HCM.Organization", "Organization", "An independently identified organization node."),
            new("HCM.PayrollResult", "Payroll result", "A period result with exact gross, deduction and net values."),
            new("HCM.Position", "Position", "A worker position linked to organization and cost center."),
            new("HCM.Worker", "Worker", "A person with a stable HCM business identity.")
        ],
        [new DomainPackRuleProviderMetadata(Id, Version, RuleFactories.Select(factory =>
            new DomainPackRuleMetadata(factory.Type, factory.Descriptor.Version, factory.Descriptor.Description)))],
        [new PackConfigurationSchemaContribution("verification.rules",
            RuleConfigurationSchema.Generate(RuleFactories.Select(factory => factory.Descriptor)).ToJsonString(),
            RuleFactories.Select(factory => factory.Type))],
        [
            new("verification.effective-dated-employment", "Effective-dated employment", "Validate configured current-employment derivation over preserved interval history."),
            new("verification.exact-compensation-payroll", "Exact compensation and payroll", "Preserve exact configured decimal values and reconcile gross less deductions to net."),
            new("verification.organization-integrity", "Organization integrity", "Resolve configured worker, position, organization and cost-center relationships.")
        ],
        [
            new("packs.describe", "Describe pack", "Expose deterministic HCM concepts and rule metadata."),
            new("rules.list", "List rules", "List HCM assurance rules."),
            new("rules.describe", "Describe rules", "Describe HCM rules and versions."),
            new("schema", "Generate schema", "Generate HCM rule configuration schema.")
        ]);
    }

    private static VerificationRuleFactory CreateReferenceFactory()
    {
        var fields = new[]
        {
            Field("referenceField", "semanticType", VerificationOrderingRole.Grouping),
            Field("referenceKeyField", "referenceSemanticType", VerificationOrderingRole.Lookup)
        };
        return Factory("hcm-reference-integrity", definition => new HcmReferenceIntegrityRule(definition),
            "Validate configured worker and organizational references in independently observed target state.", VerificationScope.Relationship,
            [
                TextOption("sourceNode", "Target node containing the reference."),
                SemanticOption("semanticType", "Referencing HCM concept."),
                FieldOption("referenceField", "Configured relationship field."),
                TextOption("referenceNode", "Target node containing referenced records."),
                SemanticOption("referenceSemanticType", "Referenced HCM concept."),
                FieldOption("referenceKeyField", "Referenced business key field.")
            ], fields);
    }

    private static VerificationRuleFactory CreateCurrentEmploymentFactory()
    {
        var fields = new[]
        {
            Field("workerField", "semanticType", VerificationOrderingRole.Grouping),
            Field("startField", "semanticType", VerificationOrderingRole.Ordering),
            Field("endField", "semanticType", null)
        };
        return Factory("hcm-current-employment", definition => new HcmCurrentEmploymentRule(definition),
            "Derive the configured as-of current employment interval from effective-dated history.", VerificationScope.Timeline,
            [
                TextOption("targetNode", "Target employment graph node."),
                SemanticOption("semanticType", "Employment concept to inspect."),
                FieldOption("workerField", "Worker business key."),
                FieldOption("startField", "Inclusive effective start date."),
                FieldOption("endField", "Inclusive effective end date; null denotes open ended."),
                TextOption("asOfDate", "ISO date at which current employment is derived.")
            ], fields);
    }

    private static VerificationRuleFactory CreateExactValueFactory()
    {
        var fields = new[]
        {
            Field("identityField", "semanticType", VerificationOrderingRole.Grouping),
            Field("amountField", "semanticType", null)
        };
        return Factory("hcm-exact-value-fidelity", definition => new HcmExactValueFidelityRule(definition),
            "Compare configured HCM compensation or balance measures using exact decimal values.", VerificationScope.Attribute,
            [
                TextOption("targetNode", "Target HCM graph node."),
                SemanticOption("semanticType", "HCM measure concept."),
                FieldOption("identityField", "Measure record identity field."),
                FieldOption("amountField", "Exact decimal measure field.")
            ], fields);
    }

    private static VerificationRuleFactory CreatePayrollFactory()
    {
        var fields = new[]
        {
            Field("identityField", "semanticType", VerificationOrderingRole.Grouping),
            Field("workerField", "semanticType", null),
            Field("periodField", "semanticType", null),
            Field("currencyField", "semanticType", null),
            Field("grossField", "semanticType", null),
            Field("deductionsField", "semanticType", null),
            Field("netField", "semanticType", null)
        };
        return Factory("hcm-payroll-reconciliation", definition => new HcmPayrollReconciliationRule(definition),
            "Verify exact payroll fields and the configured gross minus deductions equals net invariant.", VerificationScope.Aggregate,
            [
                TextOption("targetNode", "Target payroll graph node."),
                SemanticOption("semanticType", "Payroll result concept."),
                FieldOption("identityField", "Payroll result identity field."),
                FieldOption("workerField", "Worker business key field."),
                FieldOption("periodField", "Payroll period field."),
                FieldOption("currencyField", "Currency field."),
                FieldOption("grossField", "Exact gross decimal field."),
                FieldOption("deductionsField", "Exact deductions decimal field."),
                FieldOption("netField", "Exact net decimal field.")
            ], fields);
    }

    private static VerificationRuleFactory Factory(string type,
        Func<VerificationRuleDefinition, ProofShift.Verification.IVerificationRule> create,
        string description, VerificationScope scope, RuleOptionDescriptor[] options, RuleFieldRequirement[] fields) =>
        new(type, create, new RuleDescriptor(type, "1", description, scope, options, fieldRequirements: fields,
            partitionExecution: VerificationPartitionExecution.Global));

    private static RuleOptionDescriptor TextOption(string name, string description) =>
        new(name, RuleOptionKind.Text, description, Required: true);
    private static RuleOptionDescriptor SemanticOption(string name, string description) =>
        new(name, RuleOptionKind.SemanticTypeReference, description, Required: true);
    private static RuleOptionDescriptor FieldOption(string name, string description) =>
        new(name, RuleOptionKind.FieldReference, description, Required: true);
    private static RuleFieldRequirement Field(string option, string semanticOption, VerificationOrderingRole? role) =>
        new(option, VerificationFieldSide.Target, semanticOption, keyRole: role);
}

public abstract class HcmRuleBase(VerificationRuleDefinition definition) : VerificationRuleBase(definition)
{
    protected string Node => Option("targetNode");
    protected string Semantic => Option("semanticType");

    protected static string Text(VerificationArtifactRecord record, string field)
    {
        record.RequireDeclaredField(field);
        if (!record.Values.TryGetValue(field, out var value)) return string.Empty;
        return value switch
        {
            NullValue => string.Empty,
            StringValue text => text.Value,
            IntegerValue number => number.Value.ToString(CultureInfo.InvariantCulture),
            DecimalValue number => number.Value.ToString("G29", CultureInfo.InvariantCulture),
            DateValue date => date.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            LocalDateTimeValue local => local.Value.ToString("O", CultureInfo.InvariantCulture),
            InstantValue instant => instant.Value.ToString("O", CultureInfo.InvariantCulture),
            OffsetDateTimeValue offset => offset.Value.ToString("O", CultureInfo.InvariantCulture),
            _ => string.Empty
        };
    }

    protected static decimal? ReadDecimal(VerificationArtifactRecord record, string field)
    {
        record.RequireDeclaredField(field);
        if (!record.Values.TryGetValue(field, out var value)) return null;
        return value switch
        {
            DecimalValue number => number.Value,
            IntegerValue number => number.Value,
            StringValue text when decimal.TryParse(text.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };
    }

    protected static DateOnly? Date(VerificationArtifactRecord record, string field)
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

    protected static EvidenceValue DateEvidence(DateOnly? value) =>
        new(value is { } date ? new DateValue(date) : new NullValue());

    protected static EvidenceValue ExactAmountEvidence(decimal? value) =>
        new(value is { } amount ? new DecimalValue(amount) : new NullValue());

    protected static EvidenceValue PayrollEvidence(VerificationArtifactRecord record, string grossField,
        string deductionsField, string netField, string currencyField) => new(new ObjectValue([
            new KeyValuePair<string, ValueNode>("currency", new StringValue(Text(record, currencyField))),
            new KeyValuePair<string, ValueNode>("gross", ReadDecimal(record, grossField) is { } gross ? new DecimalValue(gross) : new NullValue()),
            new KeyValuePair<string, ValueNode>("deductions", ReadDecimal(record, deductionsField) is { } deductions ? new DecimalValue(deductions) : new NullValue()),
            new KeyValuePair<string, ValueNode>("net", ReadDecimal(record, netField) is { } net ? new DecimalValue(net) : new NullValue())
        ]));

    protected static string Fingerprint(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

public sealed class HcmReferenceIntegrityRule(VerificationRuleDefinition definition) : HcmRuleBase(definition)
{
    public override VerificationScope Scope => VerificationScope.Relationship;
    public override IReadOnlyCollection<VerificationOrderingKey> RequiredOrderingKeys =>
    [
        new VerificationOrderingKey(Option("semanticType"), Option("referenceField"), VerificationOrderingRole.Grouping),
        new VerificationOrderingKey(Option("referenceSemanticType"), Option("referenceKeyField"), VerificationOrderingRole.Lookup)
    ];

    public override async IAsyncEnumerable<VerificationFinding> EvaluateAsync(VerificationExecutionContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var sourceNode = Option("sourceNode");
        var semanticType = Option("semanticType");
        var referenceField = Option("referenceField");
        var referenceNode = Option("referenceNode");
        var referenceSemanticType = Option("referenceSemanticType");
        var referenceKeyField = Option("referenceKeyField");
        var sourceKey = new VerificationOrderingKey(semanticType, referenceField, VerificationOrderingRole.Grouping);
        var targetKey = new VerificationOrderingKey(referenceSemanticType, referenceKeyField, VerificationOrderingRole.Lookup);
        await using var sources = context.Workspace.ReadArtifactRecordsByKeysAsync(VerificationArtifactRole.ActualTarget,
            sourceNode, semanticType, [sourceKey], cancellationToken).GetAsyncEnumerator(cancellationToken);
        await using var references = context.Workspace.ReadArtifactRecordsByKeysAsync(VerificationArtifactRole.ActualTarget,
            referenceNode, referenceSemanticType, [targetKey], cancellationToken).GetAsyncEnumerator(cancellationToken);
        var hasReference = await references.MoveNextAsync().ConfigureAwait(false);
        var failures = 0;
        while (await sources.MoveNextAsync().ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = sources.Current;
            var key = Text(source, referenceField);
            while (hasReference && StringComparer.Ordinal.Compare(Text(references.Current, referenceKeyField), key) < 0)
                hasReference = await references.MoveNextAsync().ConfigureAwait(false);
            if (key.Length > 0 && hasReference && StringComparer.Ordinal.Equals(Text(references.Current, referenceKeyField), key)) continue;
            failures++;
            yield return Finding(context, EvidenceType.Relationship, EvidenceResult.Fail, "HcmReferenceMissing",
                $"{Id.Value}:{source.NodeKey}:{Fingerprint(key)}:{Fingerprint(source.Artifact.Identity)}",
                "A configured worker or organizational reference does not resolve in the independently observed target.",
                [ArtifactInput(context, source.NodeKey, source.Artifact)],
                new EvidenceValue(new StringValue("resolvable")), new EvidenceValue(new StringValue("missing")));
        }
        if (failures == 0)
            yield return Finding(context, EvidenceType.Relationship, EvidenceResult.Pass, "HcmReferencesResolved",
                $"{Id.Value}:complete", "All configured HCM references resolve in observed target state.", context.BindingReferences);
    }
}

public sealed class HcmCurrentEmploymentRule(VerificationRuleDefinition definition) : HcmRuleBase(definition)
{
    public override VerificationScope Scope => VerificationScope.Timeline;
    public override IReadOnlyCollection<VerificationOrderingKey> RequiredOrderingKeys =>
        [new VerificationOrderingKey(Semantic, Option("workerField"), VerificationOrderingRole.Grouping),
         new VerificationOrderingKey(Semantic, Option("startField"), VerificationOrderingRole.Ordering)];

    public override async IAsyncEnumerable<VerificationFinding> EvaluateAsync(VerificationExecutionContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var workerField = Option("workerField");
        var startField = Option("startField");
        var endField = Option("endField");
        if (!DateOnly.TryParseExact(Option("asOfDate"), "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var asOfDate))
            throw new VerificationRuleException("PSRULE003", "HCM asOfDate must use the ISO yyyy-MM-dd format.");
        string? worker = null;
        var currentCount = 0;
        var currentRecord = (VerificationArtifactRecord?)null;
        var representativeRecord = (VerificationArtifactRecord?)null;
        var failures = 0;
        await foreach (var record in context.Workspace.ReadArtifactRecordsByKeysAsync(VerificationArtifactRole.ActualTarget,
            Node, Semantic, RequiredOrderingKeys, cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var nextWorker = Text(record, workerField);
            if (worker is not null && !string.Equals(worker, nextWorker, StringComparison.Ordinal))
            {
                if (currentCount != 1)
                {
                    failures++;
                    yield return CurrentFinding(context, worker, currentCount, representativeRecord!, asOfDate);
                }
                currentCount = 0;
                currentRecord = null;
                representativeRecord = null;
            }
            worker = nextWorker;
            representativeRecord ??= record;
            var start = Date(record, startField);
            var end = Date(record, endField);
            if (start is { } from && from <= asOfDate && (end is null || end >= asOfDate))
            {
                currentCount++;
                currentRecord = record;
            }
        }
        if (worker is not null && currentCount != 1)
        {
            failures++;
            yield return CurrentFinding(context, worker, currentCount, representativeRecord!, asOfDate);
        }
        if (failures == 0)
            yield return Finding(context, EvidenceType.Timeline, EvidenceResult.Pass, "HcmCurrentEmploymentDerived",
                $"{Id.Value}:complete", $"Exactly one current interval was derived per worker as of {asOfDate:yyyy-MM-dd}.", context.BindingReferences);
    }

    private VerificationFinding CurrentFinding(VerificationExecutionContext context, string worker, int count,
        VerificationArtifactRecord record, DateOnly asOfDate) =>
        Finding(context, EvidenceType.Timeline, EvidenceResult.Fail, "HcmCurrentEmploymentCardinality",
            $"{Id.Value}:{Fingerprint(worker)}:{asOfDate:yyyy-MM-dd}",
            "Current employment must be derived from effective-dated history, with exactly one interval covering the configured as-of date.",
            [ArtifactInput(context, Node, record.Artifact)],
            new EvidenceValue(new IntegerValue(1)), new EvidenceValue(new IntegerValue(count)));
}

public sealed class HcmExactValueFidelityRule(VerificationRuleDefinition definition) : HcmRuleBase(definition)
{
    public override VerificationScope Scope => VerificationScope.Attribute;
    public override IReadOnlyCollection<VerificationOrderingKey> RequiredOrderingKeys =>
        [new VerificationOrderingKey(Semantic, Option("identityField"), VerificationOrderingRole.Grouping)];

    public override async IAsyncEnumerable<VerificationFinding> EvaluateAsync(VerificationExecutionContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var identityField = Option("identityField");
        var amountField = Option("amountField");
        await using var expected = context.Workspace.ReadArtifactRecordsByKeysAsync(VerificationArtifactRole.ExpectedTarget,
            Node, Semantic, RequiredOrderingKeys, cancellationToken).GetAsyncEnumerator(cancellationToken);
        await using var actual = context.Workspace.ReadArtifactRecordsByKeysAsync(VerificationArtifactRole.ActualTarget,
            Node, Semantic, RequiredOrderingKeys, cancellationToken).GetAsyncEnumerator(cancellationToken);
        var hasExpected = await expected.MoveNextAsync().ConfigureAwait(false);
        var hasActual = await actual.MoveNextAsync().ConfigureAwait(false);
        var failures = 0;
        while (hasExpected && hasActual)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var expectedRecord = expected.Current;
            var actualRecord = actual.Current;
            var expectedId = Text(expectedRecord, identityField);
            var actualId = Text(actualRecord, identityField);
            var comparison = StringComparer.Ordinal.Compare(expectedId, actualId);
            if (comparison < 0) { hasExpected = await expected.MoveNextAsync().ConfigureAwait(false); continue; }
            if (comparison > 0) { hasActual = await actual.MoveNextAsync().ConfigureAwait(false); continue; }
            var expectedAmount = ReadDecimal(expectedRecord, amountField);
            var actualAmount = ReadDecimal(actualRecord, amountField);
            if (expectedAmount != actualAmount)
            {
                failures++;
                yield return Finding(context, EvidenceType.Comparison, EvidenceResult.Fail, "HcmExactValueMismatch",
                    $"{Id.Value}:{Fingerprint(expectedId)}:{amountField}",
                    "The independently observed HCM amount differs from the exact checkpoint-and-graph decimal value.",
                    [ArtifactInput(context, Node, expectedRecord.Artifact), ArtifactInput(context, Node, actualRecord.Artifact)],
                    ExactAmountEvidence(expectedAmount), ExactAmountEvidence(actualAmount));
            }
            hasExpected = await expected.MoveNextAsync().ConfigureAwait(false);
            hasActual = await actual.MoveNextAsync().ConfigureAwait(false);
        }
        if (failures == 0)
            yield return Finding(context, EvidenceType.Comparison, EvidenceResult.Pass, "HcmExactValuesMatch",
                $"{Id.Value}:complete", "All configured HCM decimal measures match exactly.", context.BindingReferences);
    }
}

public sealed class HcmPayrollReconciliationRule(VerificationRuleDefinition definition) : HcmRuleBase(definition)
{
    public override VerificationScope Scope => VerificationScope.Aggregate;
    public override IReadOnlyCollection<VerificationOrderingKey> RequiredOrderingKeys =>
        [new VerificationOrderingKey(Semantic, Option("identityField"), VerificationOrderingRole.Grouping)];

    public override async IAsyncEnumerable<VerificationFinding> EvaluateAsync(VerificationExecutionContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var identityField = Option("identityField");
        var workerField = Option("workerField");
        var periodField = Option("periodField");
        var currencyField = Option("currencyField");
        var grossField = Option("grossField");
        var deductionsField = Option("deductionsField");
        var netField = Option("netField");
        await using var expected = context.Workspace.ReadArtifactRecordsByKeysAsync(VerificationArtifactRole.ExpectedTarget,
            Node, Semantic, RequiredOrderingKeys, cancellationToken).GetAsyncEnumerator(cancellationToken);
        await using var actual = context.Workspace.ReadArtifactRecordsByKeysAsync(VerificationArtifactRole.ActualTarget,
            Node, Semantic, RequiredOrderingKeys, cancellationToken).GetAsyncEnumerator(cancellationToken);
        var hasExpected = await expected.MoveNextAsync().ConfigureAwait(false);
        var hasActual = await actual.MoveNextAsync().ConfigureAwait(false);
        var failures = 0;
        while (hasExpected && hasActual)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var expectedRecord = expected.Current;
            var actualRecord = actual.Current;
            var expectedId = Text(expectedRecord, identityField);
            var actualId = Text(actualRecord, identityField);
            var comparison = StringComparer.Ordinal.Compare(expectedId, actualId);
            if (comparison < 0) { hasExpected = await expected.MoveNextAsync().ConfigureAwait(false); continue; }
            if (comparison > 0) { hasActual = await actual.MoveNextAsync().ConfigureAwait(false); continue; }
            var expectedGross = ReadDecimal(expectedRecord, grossField);
            var expectedDeductions = ReadDecimal(expectedRecord, deductionsField);
            var expectedNet = ReadDecimal(expectedRecord, netField);
            var actualGross = ReadDecimal(actualRecord, grossField);
            var actualDeductions = ReadDecimal(actualRecord, deductionsField);
            var actualNet = ReadDecimal(actualRecord, netField);
            var expectedEquation = expectedGross is { } eg && expectedDeductions is { } ed && expectedNet == eg - ed;
            var actualEquation = actualGross is { } ag && actualDeductions is { } ad && actualNet == ag - ad;
            var exactValuesMatch = expectedGross == actualGross && expectedDeductions == actualDeductions &&
                expectedNet == actualNet && Text(expectedRecord, workerField) == Text(actualRecord, workerField) &&
                Text(expectedRecord, periodField) == Text(actualRecord, periodField) &&
                Text(expectedRecord, currencyField) == Text(actualRecord, currencyField);
            if (!exactValuesMatch || !expectedEquation || !actualEquation)
            {
                failures++;
                yield return Finding(context, EvidenceType.Aggregate, EvidenceResult.Fail, "HcmPayrollReconciliationMismatch",
                    $"{Id.Value}:{Fingerprint(expectedId)}",
                    "Payroll amounts must preserve exact decimals and satisfy the configured gross minus deductions equals net equation.",
                    [ArtifactInput(context, Node, expectedRecord.Artifact), ArtifactInput(context, Node, actualRecord.Artifact)],
                    PayrollEvidence(expectedRecord, grossField, deductionsField, netField, currencyField),
                    PayrollEvidence(actualRecord, grossField, deductionsField, netField, currencyField));
            }
            hasExpected = await expected.MoveNextAsync().ConfigureAwait(false);
            hasActual = await actual.MoveNextAsync().ConfigureAwait(false);
        }
        if (failures == 0)
            yield return Finding(context, EvidenceType.Aggregate, EvidenceResult.Pass, "HcmPayrollReconciled",
                $"{Id.Value}:complete", "Payroll amounts and the configured exact net equation reconcile.", context.BindingReferences);
    }
}
