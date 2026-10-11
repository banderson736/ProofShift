using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using ProofShift.Domain;
using ProofShift.Packs.Abstractions;
using ProofShift.Verification;

namespace ProofShift.Packs.Utility;

public sealed class UtilityPack : IDomainPack
{
    public string Id => "proofshift.utility";
    public string Version => "0.10.0";

    public IReadOnlyCollection<VerificationRuleFactory> RuleFactories { get; } =
    [
        Factory("utility-attribute-equality", definition => new UtilityAttributeEqualityRule(definition),
            ["targetNode", "semanticType", "attribute", "identityField"],
            [
                Field("attribute", "semanticType", null, ["status"]),
                Field("identityField", "semanticType", VerificationOrderingRole.Grouping)
            ], VerificationScope.Attribute),
        Factory("utility-reference-integrity", definition => new UtilityReferenceIntegrityRule(definition),
            ["sourceNode", "semanticType", "referenceField", "referenceNode", "referenceSemanticType", "referenceKeyField"],
            [
                Field("referenceField", "semanticType", VerificationOrderingRole.Grouping),
                Field("referenceKeyField", "referenceSemanticType", VerificationOrderingRole.Lookup)
            ], VerificationScope.Relationship),
        Factory("utility-meter-read-sequence", definition => new UtilityMeterReadSequenceRule(definition),
            ["targetNode", "semanticType", "readKeyField", "meterField", "sequenceField", "timestampField", "usageField", "unitField"],
            [
                Field("readKeyField", "semanticType", VerificationOrderingRole.Grouping, ["read_id"]),
                Field("meterField", "semanticType", VerificationOrderingRole.Grouping, ["meter_id"]),
                Field("sequenceField", "semanticType", VerificationOrderingRole.Ordering, ["sequence"]),
                Field("timestampField", "semanticType", null, ["read_at"]),
                Field("usageField", "semanticType", null, ["usage"]),
                Field("unitField", "semanticType", null, ["unit"])
            ], VerificationScope.Timeline),
        Factory("utility-invoice-reconciliation", definition => new UtilityInvoiceReconciliationRule(definition),
            ["targetNode", "semanticType", "amountField", "groupBy", "tolerance"],
            [
                Field("amountField", "semanticType", null, ["invoice_total"]),
                Field("groupBy", "semanticType", VerificationOrderingRole.Grouping, ["account_id", "billing_period"])
            ], VerificationScope.Aggregate),
        Factory("utility-payment-reconciliation", definition => new UtilityInvoiceReconciliationRule(definition),
            ["targetNode", "semanticType", "amountField", "groupBy", "tolerance"],
            [
                Field("amountField", "semanticType", null, ["payment_amount"]),
                Field("groupBy", "semanticType", VerificationOrderingRole.Grouping, ["account_id", "invoice_id"])
            ], VerificationScope.Aggregate),
        Factory("utility-adjustment-reconciliation", definition => new UtilityInvoiceReconciliationRule(definition),
            ["targetNode", "semanticType", "amountField", "groupBy", "tolerance"],
            [
                Field("amountField", "semanticType", null, ["adjustment_amount"]),
                Field("groupBy", "semanticType", VerificationOrderingRole.Grouping, ["account_id"])
            ], VerificationScope.Aggregate)
    ];

    public DomainPackMetadata Metadata { get; }

    public UtilityPack()
    {
        Metadata = new DomainPackMetadata(Id, Version, "Utility CIS and Billing Assurance",
        [
            new("Utility.Account", "Account", "A customer billing account."),
            new("Utility.Adjustment", "Adjustment", "An account-level billing adjustment."),
            new("Utility.Customer", "Customer", "A utility customer or responsible party."),
            new("Utility.Document", "Document", "A document associated with a utility account."),
            new("Utility.Invoice", "Invoice", "A billing-period invoice with exact decimal totals."),
            new("Utility.Meter", "Meter", "A metering device assigned to a service point."),
            new("Utility.MeterRead", "Meter read", "An ordered meter observation with exact value and unit."),
            new("Utility.Payment", "Payment", "A payment allocated to an account or invoice."),
            new("Utility.RateAssignment", "Rate assignment", "A versioned rate code assignment."),
            new("Utility.ServicePoint", "Service point", "A service location associated with an account.")
        ],
        [new DomainPackRuleProviderMetadata(Id, Version, RuleFactories.Select(factory =>
            new DomainPackRuleMetadata(factory.Type, factory.Descriptor.Version, factory.Descriptor.Description)))],
        [new PackConfigurationSchemaContribution("verification.rules",
            RuleConfigurationSchema.Generate(RuleFactories.Select(factory => factory.Descriptor)).ToJsonString(),
            RuleFactories.Select(factory => factory.Type))],
        [
            new("verification.billing-invariants", "Billing invariants", "Validate utility relationships, ordered reads, and exact billing aggregates."),
            new("verification.external-target", "Independent target observation", "Verify independently prepared target state.")
        ],
        [
            new("packs.describe", "Describe pack", "Expose deterministic utility concepts and rule metadata."),
            new("rules.list", "List rules", "List Utility assurance rules."),
            new("rules.describe", "Describe rules", "Describe Utility rules and versions."),
            new("schema", "Generate schema", "Generate Utility rule configuration schema.")
        ]);
    }

    private static VerificationRuleFactory Factory(string type,
        Func<VerificationRuleDefinition, ProofShift.Verification.IVerificationRule> create, string[] optionNames,
        RuleFieldRequirement[] fieldRequirements, VerificationScope scope) =>
        new(type, create, new RuleDescriptor(type, "1", $"Utility assurance: {type[8..].Replace('-', ' ')}.", scope,
            optionNames.Select(name => name switch
            {
                "groupBy" => new RuleOptionDescriptor(name, RuleOptionKind.Sequence,
                    "Ordered grouping fields.", ItemKind: RuleOptionKind.FieldReference),
                "tolerance" => new RuleOptionDescriptor(name, RuleOptionKind.Number,
                    "Exact decimal tolerance; zero requires exact equality.", Default: new DecimalValue(0m), Minimum: 0),
                _ when name.EndsWith("SemanticType", StringComparison.Ordinal) || name == "semanticType" =>
                    new RuleOptionDescriptor(name, RuleOptionKind.SemanticTypeReference, "Configured utility semantic type."),
                _ when name == "attribute" || name.EndsWith("Field", StringComparison.Ordinal) =>
                    new RuleOptionDescriptor(name, RuleOptionKind.FieldReference, "Configured target field."),
                _ => new RuleOptionDescriptor(name, RuleOptionKind.Text, "Explicit graph-node scope.")
            }), fieldRequirements: fieldRequirements));

    private static RuleFieldRequirement Field(string optionName, string semanticTypeOption,
        VerificationOrderingRole? keyRole, string[]? defaultFields = null) =>
        new(optionName, VerificationFieldSide.Target, semanticTypeOption, defaultFields: defaultFields, keyRole: keyRole);
}

public abstract class UtilityRuleBase(VerificationRuleDefinition definition) : VerificationRuleBase(definition)
{
    protected string Node => Option("targetNode");
    protected string SemanticType => Option("semanticType");

    protected static string Text(VerificationArtifactRecord record, string field)
    {
        record.RequireDeclaredField(field);
        return record.Values.TryGetValue(field, out var value) ? value switch
        {
            NullValue => string.Empty,
            StringValue text => text.Value,
            IntegerValue integer => integer.Value.ToString(CultureInfo.InvariantCulture),
            DecimalValue number => number.Value.ToString("G29", CultureInfo.InvariantCulture),
            BooleanValue boolean => boolean.Value ? "true" : "false",
            DateValue date => date.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            InstantValue instant => instant.Value.ToString("O", CultureInfo.InvariantCulture),
            OffsetDateTimeValue offset => offset.Value.ToString("O", CultureInfo.InvariantCulture),
            LocalDateTimeValue local => local.Value.ToString("O", CultureInfo.InvariantCulture),
            _ => string.Empty
        } : string.Empty;
    }

    protected static decimal? ReadDecimal(VerificationArtifactRecord record, string field)
    {
        record.RequireDeclaredField(field);
        if (!record.Values.TryGetValue(field, out var value)) return null;
        return value switch
        {
            DecimalValue number => number.Value,
            IntegerValue integer => integer.Value,
            StringValue text when decimal.TryParse(text.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };
    }

    protected static string Fingerprint(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    protected static string CanonicalKey(IEnumerable<string> values)
    {
        var builder = new StringBuilder();
        foreach (var value in values)
            builder.Append(Encoding.UTF8.GetByteCount(value).ToString(CultureInfo.InvariantCulture)).Append(':').Append(value).Append(';');
        return builder.ToString();
    }
}

public sealed class UtilityAttributeEqualityRule(VerificationRuleDefinition definition) : UtilityRuleBase(definition)
{
    public override VerificationScope Scope => VerificationScope.Attribute;
    public override IReadOnlyCollection<VerificationOrderingKey> RequiredOrderingKeys =>
        [new VerificationOrderingKey(SemanticType, Option("identityField"), VerificationOrderingRole.Grouping)];

    public override async IAsyncEnumerable<VerificationFinding> EvaluateAsync(VerificationExecutionContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var identityFields = new[] { Option("identityField") };
        var identityKeys = RequiredOrderingKeys;
        var attribute = Option("attribute");
        await using var expected = context.Workspace.ReadArtifactRecordsByKeysAsync(VerificationArtifactRole.ExpectedTarget,
            Node, SemanticType, identityKeys, cancellationToken).GetAsyncEnumerator(cancellationToken);
        await using var actual = context.Workspace.ReadArtifactRecordsByKeysAsync(VerificationArtifactRole.ActualTarget,
            Node, SemanticType, identityKeys, cancellationToken).GetAsyncEnumerator(cancellationToken);
        var hasExpected = await expected.MoveNextAsync().ConfigureAwait(false);
        var hasActual = await actual.MoveNextAsync().ConfigureAwait(false);
        var mismatches = 0;
        while (hasExpected && hasActual)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var expectedRecord = expected.Current;
            var actualRecord = actual.Current;
            var expectedKey = Identity(expectedRecord, identityFields);
            var actualKey = Identity(actualRecord, identityFields);
            var order = StringComparer.Ordinal.Compare(expectedKey, actualKey);
            if (order < 0)
            {
                hasExpected = await expected.MoveNextAsync().ConfigureAwait(false);
                continue;
            }
            if (order > 0)
            {
                hasActual = await actual.MoveNextAsync().ConfigureAwait(false);
                continue;
            }

            expectedRecord.RequireDeclaredField(attribute);
            actualRecord.RequireDeclaredField(attribute);
            var expectedValue = expectedRecord.Values.GetValueOrDefault(attribute, new NullValue());
            var actualValue = actualRecord.Values.GetValueOrDefault(attribute, new NullValue());
            if (!Equals(expectedValue, actualValue))
            {
                mismatches++;
                yield return Finding(context, EvidenceType.Comparison, EvidenceResult.Fail, "UtilityAttributeMismatch",
                    $"{Id.Value}:{expectedRecord.NodeKey}:{Fingerprint(expectedKey)}:{attribute}",
                    "An independently observed Utility attribute differs from the checkpoint-and-graph expectation.",
                    [ArtifactInput(context, expectedRecord.NodeKey, expectedRecord.Artifact),
                     ArtifactInput(context, actualRecord.NodeKey, actualRecord.Artifact)],
                    SafeAttribute(expectedValue), SafeAttribute(actualValue));
            }

            hasExpected = await expected.MoveNextAsync().ConfigureAwait(false);
            hasActual = await actual.MoveNextAsync().ConfigureAwait(false);
        }

        if (mismatches == 0)
            yield return Finding(context, EvidenceType.Comparison, EvidenceResult.Pass, "UtilityAttributeMatch",
                $"{Id.Value}:complete", "The configured Utility attribute matches for all expected and observed identities.",
                context.BindingReferences);
    }

    private static string Identity(VerificationArtifactRecord record, IReadOnlyCollection<string> fields) =>
        CanonicalKey(fields.Select(field => Text(record, field)));

    private static EvidenceValue SafeAttribute(ValueNode value) => value switch
    {
        DecimalValue or IntegerValue or BooleanValue or DateValue or InstantValue or OffsetDateTimeValue or LocalDateTimeValue => new EvidenceValue(value),
        StringValue text => new EvidenceValue(new StringValue($"sha256:{Fingerprint(text.Value)}")),
        _ => new EvidenceValue(new StringValue("[redacted]"))
    };
}

public sealed class UtilityReferenceIntegrityRule(VerificationRuleDefinition definition) : UtilityRuleBase(definition)
{
    public override VerificationScope Scope => VerificationScope.Relationship;

    public override async IAsyncEnumerable<VerificationFinding> EvaluateAsync(VerificationExecutionContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var sourceNode = Option("sourceNode");
        var semanticType = Option("semanticType");
        var referenceField = Option("referenceField");
        var referenceNode = Option("referenceNode");
        var referenceSemanticType = Option("referenceSemanticType");
        var referenceKeyField = Option("referenceKeyField");
        var sourceOrder = new VerificationOrderingKey(semanticType, referenceField, VerificationOrderingRole.Grouping);
        var referenceOrder = new VerificationOrderingKey(referenceSemanticType, referenceKeyField, VerificationOrderingRole.Lookup);
        await using var sources = context.Workspace.ReadArtifactRecordsByKeysAsync(VerificationArtifactRole.ActualTarget,
            sourceNode, semanticType, [sourceOrder], cancellationToken).GetAsyncEnumerator(cancellationToken);
        await using var references = context.Workspace.ReadArtifactRecordsByKeysAsync(VerificationArtifactRole.ActualTarget,
            referenceNode, referenceSemanticType, [referenceOrder], cancellationToken).GetAsyncEnumerator(cancellationToken);
        var hasReference = await references.MoveNextAsync().ConfigureAwait(false);
        var failures = 0;
        while (await sources.MoveNextAsync().ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = sources.Current;
            var key = Text(source, referenceField);
            while (hasReference && StringComparer.Ordinal.Compare(Text(references.Current, referenceKeyField), key) < 0)
                hasReference = await references.MoveNextAsync().ConfigureAwait(false);
            if (key.Length != 0 && hasReference && StringComparer.Ordinal.Equals(Text(references.Current, referenceKeyField), key)) continue;

            failures++;
            yield return Finding(context, EvidenceType.Relationship, EvidenceResult.Fail, "UtilityReferenceMissing",
                $"{Id.Value}:{source.NodeKey}:{Fingerprint(key)}", "A configured Utility reference does not resolve within the observed target scope.",
                [ArtifactInput(context, source.NodeKey, source.Artifact)],
                new EvidenceValue(new StringValue("resolvable")), new EvidenceValue(new StringValue("missing")));
        }

        if (failures == 0)
            yield return Finding(context, EvidenceType.Relationship, EvidenceResult.Pass, "UtilityReferencesResolved",
                $"{Id.Value}:complete", "All configured Utility references resolve within the observed target scope.", context.BindingReferences);
    }
}

public sealed class UtilityMeterReadSequenceRule(VerificationRuleDefinition definition) : UtilityRuleBase(definition)
{
    private readonly record struct TimestampKey(bool OffsetAware, long Ticks);

    public override VerificationScope Scope => VerificationScope.Timeline;

    public override async IAsyncEnumerable<VerificationFinding> EvaluateAsync(VerificationExecutionContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var readKeyField = Option("readKeyField");
        var meterField = Option("meterField");
        var sequenceField = Option("sequenceField");
        var timestampField = Option("timestampField");
        var usageField = Option("usageField");
        var unitField = Option("unitField");
        await foreach (var finding in CompareExpectedReadsAsync(context, readKeyField, timestampField, usageField,
            unitField, cancellationToken).ConfigureAwait(false))
            yield return finding;

        var order = new[]
        {
            new VerificationOrderingKey(SemanticType, meterField, VerificationOrderingRole.Grouping),
            new VerificationOrderingKey(SemanticType, sequenceField, VerificationOrderingRole.Ordering)
        };
        string? currentMeter = null;
        long previousSequence = long.MinValue;
        TimestampKey? previousTimestamp = null;
        var failures = 0;
        await foreach (var record in context.Workspace.ReadArtifactRecordsByKeysAsync(VerificationArtifactRole.ActualTarget,
            Node, SemanticType, order, cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var meter = Text(record, meterField);
            var sequenceText = Text(record, sequenceField);
            var usage = ReadDecimal(record, usageField);
            var unit = Text(record, unitField);
            var sequenceValid = long.TryParse(sequenceText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sequence);
            var timestampValid = TryReadTimestamp(record, timestampField, out var timestamp);
            if (!string.Equals(currentMeter, meter, StringComparison.Ordinal))
            {
                currentMeter = meter;
                previousSequence = long.MinValue;
                previousTimestamp = null;
            }

            var invalidSequence = !sequenceValid || sequence <= previousSequence;
            var invalidTimestamp = !timestampValid || previousTimestamp is { } previous &&
                (timestamp.OffsetAware != previous.OffsetAware || timestamp.Ticks <= previous.Ticks);
            var invalidUsage = usage is null or < 0;
            var invalidUnit = unit.Length == 0;
            if (invalidSequence || invalidTimestamp || invalidUsage || invalidUnit)
            {
                failures++;
                var code = invalidSequence || invalidTimestamp ? "UtilityMeterReadChronology" :
                    invalidUsage ? "UtilityMeterReadUsage" : "UtilityMeterReadUnit";
                yield return Finding(context, EvidenceType.Timeline, EvidenceResult.Fail, code,
                    $"{Id.Value}:{record.NodeKey}:{record.Artifact.Id.Value}",
                    "A meter-read sequence, timestamp, non-negative usage, or unit invariant is violated.",
                    [ArtifactInput(context, record.NodeKey, record.Artifact)],
                    new EvidenceValue(new StringValue("ordered-read-with-valid-unit")),
                    new EvidenceValue(new StringValue("invalid-meter-read")));
            }
            if (sequenceValid) previousSequence = sequence;
            if (timestampValid) previousTimestamp = timestamp;
        }

        if (failures == 0)
            yield return Finding(context, EvidenceType.Timeline, EvidenceResult.Pass, "UtilityMeterReadSequence",
                $"{Id.Value}:complete", "Meter reads are ordered by sequence and preserve valid timestamps, usage values, and units.",
                context.BindingReferences);
    }

    private static bool TryReadTimestamp(VerificationArtifactRecord record, string field, out TimestampKey timestamp)
    {
        record.RequireDeclaredField(field);
        if (!record.Values.TryGetValue(field, out var value))
        {
            timestamp = default;
            return false;
        }

        switch (value)
        {
            case InstantValue instant:
                timestamp = new TimestampKey(true, instant.Value.UtcDateTime.Ticks);
                return true;
            case OffsetDateTimeValue offset:
                timestamp = new TimestampKey(true, offset.Value.UtcDateTime.Ticks);
                return true;
            case LocalDateTimeValue local:
                timestamp = new TimestampKey(false, local.Value.Ticks);
                return true;
            case DateValue date:
                timestamp = new TimestampKey(false, date.Value.ToDateTime(TimeOnly.MinValue).Ticks);
                return true;
            case StringValue text when HasExplicitOffset(text.Value) &&
                DateTimeOffset.TryParse(text.Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var offset):
                timestamp = new TimestampKey(true, offset.UtcDateTime.Ticks);
                return true;
            case StringValue text when DateTime.TryParse(text.Value, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var local) && local.Kind == DateTimeKind.Unspecified:
                timestamp = new TimestampKey(false, local.Ticks);
                return true;
            default:
                timestamp = default;
                return false;
        }
    }

    private async IAsyncEnumerable<VerificationFinding> CompareExpectedReadsAsync(VerificationExecutionContext context,
        string readKeyField, string timestampField, string usageField, string unitField,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var key = new VerificationOrderingKey(SemanticType, readKeyField, VerificationOrderingRole.Grouping);
        await using var expected = context.Workspace.ReadArtifactRecordsByKeysAsync(VerificationArtifactRole.ExpectedTarget,
            Node, SemanticType, [key], cancellationToken).GetAsyncEnumerator(cancellationToken);
        await using var actual = context.Workspace.ReadArtifactRecordsByKeysAsync(VerificationArtifactRole.ActualTarget,
            Node, SemanticType, [key], cancellationToken).GetAsyncEnumerator(cancellationToken);
        var hasExpected = await expected.MoveNextAsync().ConfigureAwait(false);
        var hasActual = await actual.MoveNextAsync().ConfigureAwait(false);
        while (hasExpected && hasActual)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var expectedRecord = expected.Current;
            var actualRecord = actual.Current;
            var expectedKey = Text(expectedRecord, readKeyField);
            var actualKey = Text(actualRecord, readKeyField);
            var comparison = StringComparer.Ordinal.Compare(expectedKey, actualKey);
            if (comparison < 0)
            {
                hasExpected = await expected.MoveNextAsync().ConfigureAwait(false);
                continue;
            }
            if (comparison > 0)
            {
                hasActual = await actual.MoveNextAsync().ConfigureAwait(false);
                continue;
            }

            var expectedUsage = ReadDecimal(expectedRecord, usageField);
            var actualUsage = ReadDecimal(actualRecord, usageField);
            var expectedTimestampValid = TryReadTimestamp(expectedRecord, timestampField, out var expectedTimestamp);
            var actualTimestampValid = TryReadTimestamp(actualRecord, timestampField, out var actualTimestamp);
            var expectedUnit = Text(expectedRecord, unitField);
            var actualUnit = Text(actualRecord, unitField);
            if (expectedUsage != actualUsage || !expectedTimestampValid || !actualTimestampValid ||
                expectedTimestamp != actualTimestamp || !string.Equals(expectedUnit, actualUnit, StringComparison.Ordinal))
            {
                yield return Finding(context, EvidenceType.Comparison, EvidenceResult.Fail, "UtilityMeterReadMismatch",
                    $"{Id.Value}:{expectedRecord.NodeKey}:{Fingerprint(expectedKey)}",
                    "Observed meter-read value, unit, or timestamp differs from checkpoint-and-graph expectation.",
                    [ArtifactInput(context, expectedRecord.NodeKey, expectedRecord.Artifact),
                     ArtifactInput(context, actualRecord.NodeKey, actualRecord.Artifact)],
                    MeterReadValue(expectedUsage, expectedUnit, expectedTimestampValid ? expectedTimestamp : null),
                    MeterReadValue(actualUsage, actualUnit, actualTimestampValid ? actualTimestamp : null));
            }

            hasExpected = await expected.MoveNextAsync().ConfigureAwait(false);
            hasActual = await actual.MoveNextAsync().ConfigureAwait(false);
        }
    }

    private static EvidenceValue MeterReadValue(decimal? usage, string unit, TimestampKey? timestamp) =>
        new(new ObjectValue([
            new KeyValuePair<string, ValueNode>("usage", usage is { } amount ? new DecimalValue(amount) : new NullValue()),
            new KeyValuePair<string, ValueNode>("unit", new StringValue(unit)),
            new KeyValuePair<string, ValueNode>("timestamp", timestamp is { } value
                ? new StringValue($"{(value.OffsetAware ? "offset" : "local")}:{value.Ticks.ToString(CultureInfo.InvariantCulture)}")
                : new NullValue())
        ]));

    private static bool HasExplicitOffset(string value)
    {
        var separator = value.IndexOf('T');
        if (separator < 0) separator = value.IndexOf(' ');
        if (separator < 0 || separator == value.Length - 1) return false;
        var time = value[(separator + 1)..];
        return time.EndsWith('Z') || time.EndsWith('z') || time.Contains('+') || time.Contains('-');
    }
}

public sealed class UtilityInvoiceReconciliationRule(VerificationRuleDefinition definition) : UtilityRuleBase(definition)
{
    private sealed record InvoiceGroup(string[] Values, string Fingerprint, long Count, decimal Total,
        VerificationArtifactRecord FirstRecord);

    public override VerificationScope Scope => VerificationScope.Aggregate;

    public override async IAsyncEnumerable<VerificationFinding> EvaluateAsync(VerificationExecutionContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var amountField = Option("amountField");
        var groupFields = OptionList("groupBy", ["account_id", "billing_period"]);
        var toleranceText = Option("tolerance", "0");
        if (!decimal.TryParse(toleranceText, NumberStyles.Number, CultureInfo.InvariantCulture, out var tolerance) || tolerance < 0)
            throw new VerificationRuleException(VerificationIssueCodes.UnknownRuleType, "Utility invoice reconciliation requires non-negative decimal tolerance.");

        await using var expected = ReadGroupsAsync(VerificationArtifactRole.ExpectedTarget, context.Workspace,
            amountField, groupFields, cancellationToken).GetAsyncEnumerator(cancellationToken);
        await using var actual = ReadGroupsAsync(VerificationArtifactRole.ActualTarget, context.Workspace,
            amountField, groupFields, cancellationToken).GetAsyncEnumerator(cancellationToken);
        var hasExpected = await expected.MoveNextAsync().ConfigureAwait(false);
        var hasActual = await actual.MoveNextAsync().ConfigureAwait(false);
        var mismatches = 0;
        while (hasExpected || hasActual)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InvoiceGroup? expectedGroup = null;
            InvoiceGroup? actualGroup = null;
            if (!hasActual || hasExpected && CompareGroups(expected.Current.Values, actual.Current.Values) < 0)
            {
                expectedGroup = expected.Current;
                hasExpected = await expected.MoveNextAsync().ConfigureAwait(false);
            }
            else if (!hasExpected || CompareGroups(expected.Current.Values, actual.Current.Values) > 0)
            {
                actualGroup = actual.Current;
                hasActual = await actual.MoveNextAsync().ConfigureAwait(false);
            }
            else
            {
                expectedGroup = expected.Current;
                actualGroup = actual.Current;
                hasExpected = await expected.MoveNextAsync().ConfigureAwait(false);
                hasActual = await actual.MoveNextAsync().ConfigureAwait(false);
            }

            if (expectedGroup is not null && actualGroup is not null &&
                expectedGroup.Count == actualGroup.Count && Math.Abs(expectedGroup.Total - actualGroup.Total) <= tolerance)
                continue;
            mismatches++;
            var inputs = new List<EvidenceReference>();
            if (expectedGroup is not null) inputs.Add(ArtifactInput(context, expectedGroup.FirstRecord.NodeKey, expectedGroup.FirstRecord.Artifact));
            if (actualGroup is not null) inputs.Add(ArtifactInput(context, actualGroup.FirstRecord.NodeKey, actualGroup.FirstRecord.Artifact));
            yield return Finding(context, EvidenceType.Aggregate, EvidenceResult.Fail, FailureCode,
                $"{Id.Value}:{expectedGroup?.Fingerprint ?? actualGroup!.Fingerprint}",
                "Expected and independently observed record counts or exact decimal totals differ for a configured Utility billing group.",
                inputs, AggregateValue(expectedGroup), AggregateValue(actualGroup));
        }

        if (mismatches == 0)
            yield return Finding(context, EvidenceType.Aggregate, EvidenceResult.Pass, PassCode,
                $"{Id.Value}:complete", "All configured Utility record counts and exact decimal totals reconcile by billing group.",
                context.BindingReferences);
    }

    private string FailureCode => Definition.Type switch
    {
        "utility-invoice-reconciliation" => "UtilityInvoiceAggregateMismatch",
        "utility-payment-reconciliation" => "UtilityPaymentAggregateMismatch",
        "utility-adjustment-reconciliation" => "UtilityAdjustmentAggregateMismatch",
        _ => throw new VerificationRuleException(VerificationIssueCodes.UnknownRuleType, "Unknown Utility aggregate rule type.")
    };

    private string PassCode => Definition.Type switch
    {
        "utility-invoice-reconciliation" => "UtilityInvoiceReconciled",
        "utility-payment-reconciliation" => "UtilityPaymentsReconciled",
        "utility-adjustment-reconciliation" => "UtilityAdjustmentsReconciled",
        _ => throw new VerificationRuleException(VerificationIssueCodes.UnknownRuleType, "Unknown Utility aggregate rule type.")
    };

    private async IAsyncEnumerable<InvoiceGroup> ReadGroupsAsync(VerificationArtifactRole role, IVerificationWorkspace workspace,
        string amountField, IReadOnlyCollection<string> groupFields,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var keys = groupFields.Select(field => new VerificationOrderingKey(SemanticType, field,
            VerificationOrderingRole.Grouping)).ToArray();
        string[]? currentValues = null;
        long count = 0;
        decimal total = 0;
        VerificationArtifactRecord? first = null;
        await foreach (var record in workspace.ReadArtifactRecordsByKeysAsync(role, Node, SemanticType, keys, cancellationToken)
            .WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var values = groupFields.Select(field => Text(record, field)).ToArray();
            var amount = ReadDecimal(record, amountField) ?? throw new VerificationRuleException(VerificationIssueCodes.ContextMismatch,
                "Utility invoice aggregate encountered a non-decimal amount.");
            if (currentValues is not null && CompareGroups(currentValues, values) != 0)
            {
                yield return CreateGroup(currentValues, count, total, first!);
                count = 0;
                total = 0;
                first = null;
            }
            currentValues = values;
            first ??= record;
            count++;
            total = checked(total + amount);
        }
        if (currentValues is not null) yield return CreateGroup(currentValues, count, total, first!);
    }

    private static InvoiceGroup CreateGroup(string[] values, long count, decimal total, VerificationArtifactRecord first)
    {
        var canonical = CanonicalKey(values);
        return new InvoiceGroup(values, Fingerprint(canonical), count, total, first);
    }

    private static int CompareGroups(string[] left, string[] right)
    {
        for (var index = 0; index < Math.Min(left.Length, right.Length); index++)
        {
            var compared = StringComparer.Ordinal.Compare(left[index], right[index]);
            if (compared != 0) return compared;
        }
        return left.Length.CompareTo(right.Length);
    }

    private static EvidenceValue? AggregateValue(InvoiceGroup? group) => group is null ? null :
        new EvidenceValue(new ObjectValue([
            new KeyValuePair<string, ValueNode>("recordCount", new IntegerValue(group.Count)),
            new KeyValuePair<string, ValueNode>("amountTotal", new DecimalValue(group.Total))
        ]));
}