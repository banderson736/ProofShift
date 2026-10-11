using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using ProofShift.Domain;
using ProofShift.Packs.Abstractions;
using ProofShift.Verification;

namespace ProofShift.Packs.Justice;

public sealed class JusticePack : IDomainPack
{
    public string Id => "proofshift.justice";
    public string Version => "0.10.0";
    public IReadOnlyCollection<VerificationRuleFactory> RuleFactories { get; }
    public DomainPackMetadata Metadata { get; }

    public JusticePack()
    {
        RuleFactories =
        [
            new VerificationRuleFactory("justice-reference-integrity",
                definition => new JusticeReferenceIntegrityRule(definition),
                new RuleDescriptor("justice-reference-integrity", "1", "Validate Justice relationships in independently observed target state.",
                    VerificationScope.Relationship,
                    [
                        new RuleOptionDescriptor("sourceNode", RuleOptionKind.Text, "Configured target node containing the reference.", Required: true),
                        new RuleOptionDescriptor("semanticType", RuleOptionKind.SemanticTypeReference, "Referencing Justice concept.", Required: true),
                        new RuleOptionDescriptor("referenceField", RuleOptionKind.FieldReference, "Reference field on the source artifact.", Required: true),
                        new RuleOptionDescriptor("referenceNode", RuleOptionKind.Text, "Configured target node containing referenced artifacts.", Required: true),
                        new RuleOptionDescriptor("referenceSemanticType", RuleOptionKind.SemanticTypeReference, "Referenced Justice concept.", Required: true),
                        new RuleOptionDescriptor("referenceKeyField", RuleOptionKind.FieldReference, "Referenced key field.", Required: true)
                    ],
                    fieldRequirements:
                    [
                        new RuleFieldRequirement("referenceField", VerificationFieldSide.Target, "semanticType",
                            defaultSemanticType: "Justice.CaseParty", keyRole: VerificationOrderingRole.Grouping),
                        new RuleFieldRequirement("referenceKeyField", VerificationFieldSide.Target, "referenceSemanticType",
                            defaultSemanticType: "Justice.Case", keyRole: VerificationOrderingRole.Lookup)
                    ], partitionExecution: VerificationPartitionExecution.Global)),
            CreateAttributeEqualityFactory(),
            CreateEventOrderFactory()
        ];
        Metadata = new DomainPackMetadata(Id, Version, "Justice Case-Management Assurance",
        [
            new("Justice.Case", "Case", "A court or agency case with a stable identity and lifecycle."),
            new("Justice.CaseParty", "Case party", "A person associated with a case in a configured role."),
            new("Justice.Charge", "Charge", "An offense or charge associated with a case."),
            new("Justice.Disposition", "Disposition", "A recorded outcome for a case or charge."),
            new("Justice.Document", "Document", "A document associated with a justice case."),
            new("Justice.Filing", "Filing", "A filed case artifact with a configured filing date."),
            new("Justice.Hearing", "Hearing", "A case hearing with a configured event timestamp."),
            new("Justice.Person", "Person", "A person represented in the justice system."),
            new("Justice.Sentence", "Sentence", "A sentence record linked to a configured disposition.")
        ],
        [new DomainPackRuleProviderMetadata(Id, Version,
            RuleFactories.Select(factory => new DomainPackRuleMetadata(factory.Type, factory.Descriptor.Version,
                factory.Descriptor.Description)))],
        [new PackConfigurationSchemaContribution("verification.rules",
            RuleConfigurationSchema.Generate(RuleFactories.Select(item => item.Descriptor)).ToJsonString(),
            RuleFactories.Select(item => item.Type))],
        [new("verification.case-integrity", "Case integrity", "Validate Justice references and lifecycle consistency.")],
        [
            new("packs.describe", "Describe pack", "Expose deterministic Justice concepts and rule metadata."),
            new("rules.list", "List rules", "List Justice assurance rules."),
            new("rules.describe", "Describe rules", "Describe Justice rules and versions."),
            new("schema", "Generate schema", "Generate Justice rule configuration schema.")
        ]);
    }

    private static VerificationRuleFactory CreateAttributeEqualityFactory()
    {
        var fields = new[]
        {
            new RuleFieldRequirement("identityField", VerificationFieldSide.Target, "semanticType",
                defaultSemanticType: "Justice.Case", keyRole: VerificationOrderingRole.Grouping),
            new RuleFieldRequirement("attribute", VerificationFieldSide.Target, "semanticType",
                defaultSemanticType: "Justice.Case")
        };
        return new VerificationRuleFactory("justice-attribute-equality",
            definition => new JusticeAttributeEqualityRule(definition),
            new RuleDescriptor("justice-attribute-equality", "1", "Compare configured Justice attributes with checkpoint-and-graph expectations.",
                VerificationScope.Attribute,
                [
                    new RuleOptionDescriptor("targetNode", RuleOptionKind.Text, "Configured target graph node.", Required: true),
                    new RuleOptionDescriptor("semanticType", RuleOptionKind.SemanticTypeReference, "Justice concept to compare.", Required: true),
                    new RuleOptionDescriptor("identityField", RuleOptionKind.FieldReference, "Deterministic target identity field.", Required: true),
                    new RuleOptionDescriptor("attribute", RuleOptionKind.FieldReference, "Configured attribute to compare.", Required: true)
                ], fieldRequirements: fields, partitionExecution: VerificationPartitionExecution.Global));
    }

    private static VerificationRuleFactory CreateEventOrderFactory()
    {
        var fields = new[]
        {
            new RuleFieldRequirement("identityField", VerificationFieldSide.Target, "semanticType",
                defaultSemanticType: "Justice.Hearing", keyRole: VerificationOrderingRole.Grouping),
            new RuleFieldRequirement("earlierField", VerificationFieldSide.Target, "semanticType",
                defaultSemanticType: "Justice.Hearing"),
            new RuleFieldRequirement("laterField", VerificationFieldSide.Target, "semanticType",
                defaultSemanticType: "Justice.Hearing")
        };
        return new VerificationRuleFactory("justice-event-order",
            definition => new JusticeEventOrderRule(definition),
            new RuleDescriptor("justice-event-order", "1", "Check configured case-event timestamps remain in their declared order.",
                VerificationScope.Timeline,
                [
                    new RuleOptionDescriptor("targetNode", RuleOptionKind.Text, "Configured target graph node.", Required: true),
                    new RuleOptionDescriptor("semanticType", RuleOptionKind.SemanticTypeReference, "Justice event concept to inspect.", Required: true),
                    new RuleOptionDescriptor("identityField", RuleOptionKind.FieldReference, "Deterministic event identity field.", Required: true),
                    new RuleOptionDescriptor("earlierField", RuleOptionKind.FieldReference, "Earlier configured event timestamp.", Required: true),
                    new RuleOptionDescriptor("laterField", RuleOptionKind.FieldReference, "Later configured event timestamp.", Required: true)
                ], fieldRequirements: fields, partitionExecution: VerificationPartitionExecution.Global));
    }
}

public sealed class JusticeReferenceIntegrityRule(VerificationRuleDefinition definition) : VerificationRuleBase(definition)
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
        var sourceOrder = new VerificationOrderingKey(semanticType, referenceField, VerificationOrderingRole.Grouping);
        var referenceOrder = new VerificationOrderingKey(referenceSemanticType, referenceKeyField, VerificationOrderingRole.Lookup);
        await using var sources = context.Workspace.ReadArtifactRecordsByKeysAsync(VerificationArtifactRole.ActualTarget,
            sourceNode, semanticType, [sourceOrder], cancellationToken).GetAsyncEnumerator(cancellationToken);
        await using var references = context.Workspace.ReadArtifactRecordsByKeysAsync(VerificationArtifactRole.ActualTarget,
            referenceNode, referenceSemanticType, [referenceOrder], cancellationToken).GetAsyncEnumerator(cancellationToken);
        var hasReference = await references.MoveNextAsync().ConfigureAwait(false);
        var findings = 0;
        while (await sources.MoveNextAsync().ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = sources.Current;
            var key = Text(source, referenceField);
            while (hasReference && StringComparer.Ordinal.Compare(Text(references.Current, referenceKeyField), key) < 0)
                hasReference = await references.MoveNextAsync().ConfigureAwait(false);
            if (key.Length > 0 && hasReference && StringComparer.Ordinal.Equals(Text(references.Current, referenceKeyField), key))
                continue;

            findings++;
            yield return Finding(context, EvidenceType.Relationship, EvidenceResult.Fail, "JusticeReferenceMissing",
                $"{Id.Value}:{source.NodeKey}:{Fingerprint(key)}",
                "A configured case-management relationship does not resolve in observed target state.",
                [ArtifactInput(context, source.NodeKey, source.Artifact)],
                new EvidenceValue(new StringValue("resolvable")), new EvidenceValue(new StringValue("missing")));
        }

        if (findings == 0)
            yield return Finding(context, EvidenceType.Relationship, EvidenceResult.Pass, "JusticeReferencesResolved",
                $"{Id.Value}:complete", "All configured Justice references resolve in observed target state.", context.BindingReferences);
    }

    private static string Text(VerificationArtifactRecord record, string field)
    {
        record.RequireDeclaredField(field);
        if (!record.Values.TryGetValue(field, out var value)) return string.Empty;
        return value switch
        {
            NullValue => string.Empty,
            StringValue text => text.Value,
            IntegerValue integer => integer.Value.ToString(CultureInfo.InvariantCulture),
            DecimalValue number => number.Value.ToString("G29", CultureInfo.InvariantCulture),
            _ => string.Empty
        };
    }

    private static string Fingerprint(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

public sealed class JusticeAttributeEqualityRule(VerificationRuleDefinition definition) : VerificationRuleBase(definition)
{
    public override VerificationScope Scope => VerificationScope.Attribute;
    public override IReadOnlyCollection<VerificationOrderingKey> RequiredOrderingKeys =>
        [new VerificationOrderingKey(Option("semanticType"), Option("identityField"), VerificationOrderingRole.Grouping)];

    public override async IAsyncEnumerable<VerificationFinding> EvaluateAsync(VerificationExecutionContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var node = Option("targetNode");
        var semanticType = Option("semanticType");
        var identityField = Option("identityField");
        var field = Option("attribute");
        await using var expected = context.Workspace.ReadArtifactRecordsByKeysAsync(VerificationArtifactRole.ExpectedTarget,
            node, semanticType, RequiredOrderingKeys, cancellationToken).GetAsyncEnumerator(cancellationToken);
        await using var actual = context.Workspace.ReadArtifactRecordsByKeysAsync(VerificationArtifactRole.ActualTarget,
            node, semanticType, RequiredOrderingKeys, cancellationToken).GetAsyncEnumerator(cancellationToken);
        var hasExpected = await expected.MoveNextAsync().ConfigureAwait(false);
        var hasActual = await actual.MoveNextAsync().ConfigureAwait(false);
        var mismatches = 0;
        while (hasExpected && hasActual)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var expectedRecord = expected.Current;
            var actualRecord = actual.Current;
            expectedRecord.RequireDeclaredField(identityField);
            actualRecord.RequireDeclaredField(identityField);
            var expectedKey = ReadText(expectedRecord, identityField);
            var actualKey = ReadText(actualRecord, identityField);
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
            expectedRecord.RequireDeclaredField(field);
            actualRecord.RequireDeclaredField(field);
            var expectedValue = expectedRecord.Values.GetValueOrDefault(field, new NullValue());
            var actualValue = actualRecord.Values.GetValueOrDefault(field, new NullValue());
            if (!Equals(expectedValue, actualValue))
            {
                mismatches++;
                yield return Finding(context, EvidenceType.Comparison, EvidenceResult.Fail, "JusticeAttributeMismatch",
                    $"{Id.Value}:{Fingerprint(expectedKey)}:{field}",
                    "The observed Justice attribute differs from the configured checkpoint-and-graph transformation.",
                    [ArtifactInput(context, node, expectedRecord.Artifact), ArtifactInput(context, node, actualRecord.Artifact)],
                    SafeValue(expectedValue), SafeValue(actualValue));
            }
            hasExpected = await expected.MoveNextAsync().ConfigureAwait(false);
            hasActual = await actual.MoveNextAsync().ConfigureAwait(false);
        }
        if (mismatches == 0)
            yield return Finding(context, EvidenceType.Comparison, EvidenceResult.Pass, "JusticeAttributeMatch",
                $"{Id.Value}:complete", "The configured Justice attribute matches for all compared identities.", context.BindingReferences);
    }

    private static string ReadText(VerificationArtifactRecord record, string field) =>
        record.Values.TryGetValue(field, out var value) ? value switch
        {
            StringValue text => text.Value,
            IntegerValue integer => integer.Value.ToString(CultureInfo.InvariantCulture),
            _ => string.Empty
        } : string.Empty;

    private static EvidenceValue SafeValue(ValueNode value) => value switch
    {
        StringValue text => new EvidenceValue(new StringValue($"sha256:{Fingerprint(text.Value)}")),
        IntegerValue or DecimalValue or BooleanValue or DateValue or InstantValue or OffsetDateTimeValue or LocalDateTimeValue => new EvidenceValue(value),
        _ => new EvidenceValue(new StringValue("[redacted]"))
    };

    private static string Fingerprint(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

public sealed class JusticeEventOrderRule(VerificationRuleDefinition definition) : VerificationRuleBase(definition)
{
    public override VerificationScope Scope => VerificationScope.Timeline;
    public override IReadOnlyCollection<VerificationOrderingKey> RequiredOrderingKeys =>
        [new VerificationOrderingKey(Option("semanticType"), Option("identityField"), VerificationOrderingRole.Grouping)];

    public override async IAsyncEnumerable<VerificationFinding> EvaluateAsync(VerificationExecutionContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var node = Option("targetNode");
        var semanticType = Option("semanticType");
        var identityField = Option("identityField");
        var earlierField = Option("earlierField");
        var laterField = Option("laterField");
        var failures = 0;
        await foreach (var record in context.Workspace.ReadArtifactRecordsByKeysAsync(VerificationArtifactRole.ActualTarget,
            node, semanticType, RequiredOrderingKeys, cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            record.RequireDeclaredField(identityField);
            record.RequireDeclaredField(earlierField);
            record.RequireDeclaredField(laterField);
            if (!TryReadDate(record, earlierField, out var earlier) || !TryReadDate(record, laterField, out var later) || earlier > later)
            {
                failures++;
                yield return Finding(context, EvidenceType.Timeline, EvidenceResult.Fail, "JusticeEventOrderMismatch",
                    $"{Id.Value}:{Fingerprint(ReadText(record, identityField))}:{earlierField}:{laterField}",
                    "The configured fixture event timestamps are missing, invalid, or out of order.",
                    [ArtifactInput(context, node, record.Artifact)], DateEvidence(record, earlierField), DateEvidence(record, laterField));
            }
        }
        if (failures == 0)
            yield return Finding(context, EvidenceType.Timeline, EvidenceResult.Pass, "JusticeEventOrder",
                $"{Id.Value}:complete", "All configured fixture event timestamps are present and ordered.", context.BindingReferences);
    }

    private static bool TryReadDate(VerificationArtifactRecord record, string field, out DateTime value)
    {
        if (!record.Values.TryGetValue(field, out var item)) { value = default; return false; }
        switch (item)
        {
            case DateValue date: value = date.Value.ToDateTime(TimeOnly.MinValue); return true;
            case InstantValue instant: value = instant.Value.UtcDateTime; return true;
            case OffsetDateTimeValue offset: value = offset.Value.UtcDateTime; return true;
            case LocalDateTimeValue local: value = local.Value; return true;
            case StringValue text when DateTime.TryParse(text.Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed): value = parsed; return true;
            default: value = default; return false;
        }
    }

    private static EvidenceValue DateEvidence(VerificationArtifactRecord record, string field) =>
        record.Values.TryGetValue(field, out var value) ? new EvidenceValue(value) : new EvidenceValue(new NullValue());

    private static string ReadText(VerificationArtifactRecord record, string field) =>
        record.Values.TryGetValue(field, out var value) && value is StringValue text ? text.Value : string.Empty;

    private static string Fingerprint(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
