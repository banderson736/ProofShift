using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using ProofShift.Domain;
using ProofShift.Packs.Abstractions;
using ProofShift.Recovery;
using ProofShift.Verification;

namespace ProofShift.Packs.Fhir;

public sealed class FhirPack : IDomainPack
{
    public string Id => "proofshift.fhir";
    public string Version => "0.10.0";
    public IReadOnlyCollection<VerificationRuleFactory> RuleFactories { get; }
    public DomainPackMetadata Metadata { get; }

    public FhirPack()
    {
        RuleFactories =
        [
            new VerificationRuleFactory("fhir-typed-reference-integrity",
                definition => new FhirTypedReferenceIntegrityRule(definition),
                new RuleDescriptor("fhir-typed-reference-integrity", "1",
                    "Resolve configured R4-style typed references in independently observed target state.", VerificationScope.Relationship,
                    [
                        new RuleOptionDescriptor("sourceNode", RuleOptionKind.Text, "Referencing resource target node.", Required: true),
                        new RuleOptionDescriptor("semanticType", RuleOptionKind.SemanticTypeReference, "Referencing resource concept.", Required: true),
                        new RuleOptionDescriptor("referenceField", RuleOptionKind.FieldReference, "Canonical type/id reference field.", Required: true),
                        new RuleOptionDescriptor("expectedResourceType", RuleOptionKind.Text, "Required relative resource type.", Required: true),
                        new RuleOptionDescriptor("referenceNode", RuleOptionKind.Text, "Referenced resource target node.", Required: true),
                        new RuleOptionDescriptor("referenceSemanticType", RuleOptionKind.SemanticTypeReference, "Referenced resource concept.", Required: true),
                        new RuleOptionDescriptor("referenceKeyField", RuleOptionKind.FieldReference, "Referenced resource id field.", Required: true)
                    ],
                    fieldRequirements:
                    [
                        new RuleFieldRequirement("referenceField", VerificationFieldSide.Target, "semanticType",
                            defaultSemanticType: "Fhir.Observation", keyRole: VerificationOrderingRole.Grouping),
                        new RuleFieldRequirement("referenceKeyField", VerificationFieldSide.Target, "referenceSemanticType",
                            defaultSemanticType: "Fhir.Patient", keyRole: VerificationOrderingRole.Lookup)
                    ], partitionExecution: VerificationPartitionExecution.Global)),
            new VerificationRuleFactory("fhir-instant-fidelity",
                definition => new FhirInstantFidelityRule(definition),
                new RuleDescriptor("fhir-instant-fidelity", "1",
                    "Preserve configured clinical event instants and their UTC offsets exactly.", VerificationScope.Timeline,
                    [
                        new RuleOptionDescriptor("targetNode", RuleOptionKind.Text, "Configured resource target node.", Required: true),
                        new RuleOptionDescriptor("semanticType", RuleOptionKind.SemanticTypeReference, "Resource concept containing the instant.", Required: true),
                        new RuleOptionDescriptor("identityField", RuleOptionKind.FieldReference, "Deterministic resource identity.", Required: true),
                        new RuleOptionDescriptor("instantField", RuleOptionKind.FieldReference, "Configured offset-bearing instant field.", Required: true)
                    ],
                    fieldRequirements:
                    [
                        new RuleFieldRequirement("identityField", VerificationFieldSide.Target, "semanticType",
                            defaultSemanticType: "Fhir.Observation", keyRole: VerificationOrderingRole.Grouping),
                        new RuleFieldRequirement("instantField", VerificationFieldSide.Target, "semanticType",
                            defaultSemanticType: "Fhir.Observation")
                        ], partitionExecution: VerificationPartitionExecution.Global)),
                    new VerificationRuleFactory("fhir-quantity-fidelity",
                definition => new FhirQuantityFidelityRule(definition),
                new RuleDescriptor("fhir-quantity-fidelity", "1",
                    "Preserve exact configured quantity values and unit identity fields.", VerificationScope.Attribute,
                    [
                        new RuleOptionDescriptor("targetNode", RuleOptionKind.Text, "Configured Observation target node.", Required: true),
                        new RuleOptionDescriptor("semanticType", RuleOptionKind.SemanticTypeReference, "Quantity-bearing resource concept.", Required: true),
                        new RuleOptionDescriptor("identityField", RuleOptionKind.FieldReference, "Deterministic resource identity.", Required: true),
                        new RuleOptionDescriptor("valueField", RuleOptionKind.FieldReference, "Exact quantity value.", Required: true),
                        new RuleOptionDescriptor("unitField", RuleOptionKind.FieldReference, "Quantity display unit.", Required: true),
                        new RuleOptionDescriptor("unitSystemField", RuleOptionKind.FieldReference, "Quantity unit system URI.", Required: true),
                        new RuleOptionDescriptor("unitCodeField", RuleOptionKind.FieldReference, "Quantity unit code.", Required: true)
                    ],
                    fieldRequirements:
                    [
                        new RuleFieldRequirement("identityField", VerificationFieldSide.Target, "semanticType",
                            defaultSemanticType: "Fhir.Observation", keyRole: VerificationOrderingRole.Grouping),
                        new RuleFieldRequirement("valueField", VerificationFieldSide.Target, "semanticType", defaultSemanticType: "Fhir.Observation"),
                        new RuleFieldRequirement("unitField", VerificationFieldSide.Target, "semanticType", defaultSemanticType: "Fhir.Observation"),
                        new RuleFieldRequirement("unitSystemField", VerificationFieldSide.Target, "semanticType", defaultSemanticType: "Fhir.Observation"),
                        new RuleFieldRequirement("unitCodeField", VerificationFieldSide.Target, "semanticType", defaultSemanticType: "Fhir.Observation")
                    ], partitionExecution: VerificationPartitionExecution.Global))
        ];
        Metadata = new DomainPackMetadata(Id, Version, "FHIR-Style Healthcare Assurance",
        [
            new("Fhir.Patient", "Patient", "A synthetic patient identity used by the assurance scenario."),
            new("Fhir.Encounter", "Encounter", "A synthetic healthcare encounter with a patient relationship."),
            new("Fhir.Observation", "Observation", "A synthetic coded quantity observation with patient and encounter references."),
            new("Fhir.Condition", "Condition", "A synthetic coded condition with patient and encounter references."),
            new("Fhir.Consent", "Consent", "A synthetic patient consent state and effective interval."),
            new("Fhir.DocumentReference", "Document reference", "A synthetic reference to an independently stored binary document."),
            new("Fhir.Binary", "Binary", "An independently stored synthetic document payload.")
        ],
        [new DomainPackRuleProviderMetadata(Id, Version,
            RuleFactories.Select(factory => new DomainPackRuleMetadata(factory.Type, factory.Descriptor.Version,
                factory.Descriptor.Description)))],
        [new PackConfigurationSchemaContribution("verification.rules",
            RuleConfigurationSchema.Generate(RuleFactories.Select(item => item.Descriptor)).ToJsonString(),
            RuleFactories.Select(item => item.Type))],
        [new("fhir.r4-style-assurance", "FHIR R4-style assurance",
            "Configured resource accounting, selected coded values, quantities, temporal fidelity and reference integrity; not a conformance certification.")],
        [
            new("packs.describe", "Describe pack", "Expose deterministic FHIR-style concepts and rule metadata."),
            new("rules.list", "List rules", "List FHIR-style assurance rules."),
            new("rules.describe", "Describe rules", "Describe FHIR-style rules and versions."),
            new("schema", "Generate schema", "Generate FHIR-style rule configuration schema.")
        ]);
    }
}

public sealed class FhirTypedReferenceIntegrityRule(VerificationRuleDefinition definition) : VerificationRuleBase(definition)
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
        var expectedResourceType = Option("expectedResourceType");
        var referenceNode = Option("referenceNode");
        var referenceSemanticType = Option("referenceSemanticType");
        var referenceKeyField = Option("referenceKeyField");
        var referenceOrder = new VerificationOrderingKey(referenceSemanticType, referenceKeyField, VerificationOrderingRole.Lookup);
        await using var sources = context.Workspace.ReadArtifactRecordsByKeysAsync(VerificationArtifactRole.ActualTarget,
            sourceNode, semanticType, [RequiredOrderingKeys.First()], cancellationToken).GetAsyncEnumerator(cancellationToken);
        await using var references = context.Workspace.ReadArtifactRecordsByKeysAsync(VerificationArtifactRole.ActualTarget,
            referenceNode, referenceSemanticType, [referenceOrder], cancellationToken).GetAsyncEnumerator(cancellationToken);
        var hasReference = await references.MoveNextAsync().ConfigureAwait(false);
        var failures = 0;
        while (await sources.MoveNextAsync().ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = sources.Current;
            source.RequireDeclaredField(referenceField);
            var rawReference = ReadText(source, referenceField);
            var separator = rawReference.IndexOf('/');
            var resourceType = separator > 0 && separator == rawReference.LastIndexOf('/') ? rawReference[..separator] : string.Empty;
            var resourceId = resourceType.Length == 0 ? string.Empty : rawReference[(separator + 1)..];
            var resolves = StringComparer.Ordinal.Equals(resourceType, expectedResourceType) && resourceId.Length > 0;
            while (resolves && hasReference && StringComparer.Ordinal.Compare(ReadText(references.Current, referenceKeyField), resourceId) < 0)
                hasReference = await references.MoveNextAsync().ConfigureAwait(false);
            resolves &= hasReference && StringComparer.Ordinal.Equals(ReadText(references.Current, referenceKeyField), resourceId);
            if (resolves) continue;

            failures++;
            yield return Finding(context, EvidenceType.Relationship, EvidenceResult.Fail, "FhirReferenceMissing",
                $"{Id.Value}:{source.NodeKey}:{Fingerprint(rawReference)}",
                "A configured typed resource reference does not resolve to the required resource type and observed target identity.",
                [ArtifactInput(context, source.NodeKey, source.Artifact)],
                new EvidenceValue(new StringValue("resolvable typed reference")),
                new EvidenceValue(new StringValue("missing or wrong resource type")));
        }

        if (failures == 0)
            yield return Finding(context, EvidenceType.Relationship, EvidenceResult.Pass, "FhirReferencesResolved",
                $"{Id.Value}:complete:{semanticType}:{referenceField}",
                "All configured typed resource references resolve in independently observed target state.", context.BindingReferences);
    }

    private static string ReadText(VerificationArtifactRecord record, string field)
    {
        record.RequireDeclaredField(field);
        if (!record.Values.TryGetValue(field, out var value)) return string.Empty;
        return value switch
        {
            NullValue => string.Empty,
            StringValue text => text.Value,
            IntegerValue integer => integer.Value.ToString(CultureInfo.InvariantCulture),
            _ => string.Empty
        };
    }

    private static string Fingerprint(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

public sealed class FhirInstantFidelityRule(VerificationRuleDefinition definition) : VerificationRuleBase(definition)
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
        var instantField = Option("instantField");
        await using var expected = context.Workspace.ReadArtifactRecordsByKeysAsync(VerificationArtifactRole.ExpectedTarget,
            node, semanticType, RequiredOrderingKeys, cancellationToken).GetAsyncEnumerator(cancellationToken);
        await using var actual = context.Workspace.ReadArtifactRecordsByKeysAsync(VerificationArtifactRole.ActualTarget,
            node, semanticType, RequiredOrderingKeys, cancellationToken).GetAsyncEnumerator(cancellationToken);
        var hasExpected = await expected.MoveNextAsync().ConfigureAwait(false);
        var hasActual = await actual.MoveNextAsync().ConfigureAwait(false);
        var failures = 0;
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

            expectedRecord.RequireDeclaredField(instantField);
            actualRecord.RequireDeclaredField(instantField);
            var expectedValue = ReadText(expectedRecord, instantField);
            var actualValue = ReadText(actualRecord, instantField);
            if (!HasExplicitOffset(expectedValue) || !HasExplicitOffset(actualValue) ||
                !StringComparer.Ordinal.Equals(expectedValue, actualValue))
            {
                failures++;
                yield return Finding(context, EvidenceType.Comparison, EvidenceResult.Fail, "FhirInstantMismatch",
                    $"{Id.Value}:{Fingerprint(expectedKey)}:{instantField}",
                    "The observed resource instant is missing an explicit offset or differs from the checkpoint-and-graph value.",
                    [ArtifactInput(context, node, expectedRecord.Artifact), ArtifactInput(context, node, actualRecord.Artifact)],
                    new EvidenceValue(new StringValue($"sha256:{Fingerprint(expectedValue)}")),
                    new EvidenceValue(new StringValue($"sha256:{Fingerprint(actualValue)}")));
            }
            hasExpected = await expected.MoveNextAsync().ConfigureAwait(false);
            hasActual = await actual.MoveNextAsync().ConfigureAwait(false);
        }

        if (failures == 0)
            yield return Finding(context, EvidenceType.Comparison, EvidenceResult.Pass, "FhirInstantsPreserved",
                $"{Id.Value}:complete:{semanticType}:{instantField}",
                "Configured offset-bearing resource instants match checkpoint-and-graph values exactly.", context.BindingReferences);
    }

    private static string ReadText(VerificationArtifactRecord record, string field)
    {
        if (!record.Values.TryGetValue(field, out var value)) return string.Empty;
        return value switch { NullValue => string.Empty, StringValue text => text.Value, _ => string.Empty };
    }

    private static bool HasExplicitOffset(string value) =>
        value.Contains('T', StringComparison.Ordinal) &&
        ((value.EndsWith('Z') && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _)) ||
         (value.Length >= 6 && (value[^6] is '+' or '-') && value[^3] == ':' &&
          DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _)));

    private static string Fingerprint(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

public sealed class FhirQuantityFidelityRule(VerificationRuleDefinition definition) : VerificationRuleBase(definition)
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
        var fields = new[] { Option("valueField"), Option("unitField"), Option("unitSystemField"), Option("unitCodeField") };
        await using var expected = context.Workspace.ReadArtifactRecordsByKeysAsync(VerificationArtifactRole.ExpectedTarget,
            node, semanticType, RequiredOrderingKeys, cancellationToken).GetAsyncEnumerator(cancellationToken);
        await using var actual = context.Workspace.ReadArtifactRecordsByKeysAsync(VerificationArtifactRole.ActualTarget,
            node, semanticType, RequiredOrderingKeys, cancellationToken).GetAsyncEnumerator(cancellationToken);
        var hasExpected = await expected.MoveNextAsync().ConfigureAwait(false);
        var hasActual = await actual.MoveNextAsync().ConfigureAwait(false);
        var failures = 0;
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

            var differs = false;
            foreach (var field in fields)
            {
                expectedRecord.RequireDeclaredField(field);
                actualRecord.RequireDeclaredField(field);
                if (!Equals(expectedRecord.Values.GetValueOrDefault(field, new NullValue()),
                        actualRecord.Values.GetValueOrDefault(field, new NullValue()))) differs = true;
            }
            if (differs)
            {
                failures++;
                yield return Finding(context, EvidenceType.Comparison, EvidenceResult.Fail, "FhirQuantityMismatch",
                    $"{Id.Value}:{Fingerprint(expectedKey)}",
                    "The observed exact quantity value or configured unit identity differs from checkpoint-and-graph expectations.",
                    [ArtifactInput(context, node, expectedRecord.Artifact), ArtifactInput(context, node, actualRecord.Artifact)],
                    new EvidenceValue(new StringValue("sha256:" + Fingerprint(string.Join('|', fields.Select(field =>
                        expectedRecord.Values.GetValueOrDefault(field, new NullValue()).ToString()))))),
                    new EvidenceValue(new StringValue("sha256:" + Fingerprint(string.Join('|', fields.Select(field =>
                        actualRecord.Values.GetValueOrDefault(field, new NullValue()).ToString()))))));
            }
            hasExpected = await expected.MoveNextAsync().ConfigureAwait(false);
            hasActual = await actual.MoveNextAsync().ConfigureAwait(false);
        }

        if (failures == 0)
            yield return Finding(context, EvidenceType.Comparison, EvidenceResult.Pass, "FhirQuantitiesPreserved",
                $"{Id.Value}:complete:{semanticType}",
                "Configured exact quantity values and unit identity fields match checkpoint-and-graph expectations.", context.BindingReferences);
    }

    private static string ReadText(VerificationArtifactRecord record, string field) => record.Values.TryGetValue(field, out var value)
        ? value switch { StringValue text => text.Value, IntegerValue integer => integer.Value.ToString(CultureInfo.InvariantCulture), _ => string.Empty }
        : string.Empty;

    private static string Fingerprint(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

public sealed record FhirAssuranceReport
{
    public const string FormatVersion = "proofshift-fhir-assurance-report-v1";
    public const string ClaimBoundary = "Not FHIR/profile/US Core certification, terminology validation, clinical correctness or medical appropriateness.";

    public string Format { get; }
    public string Project { get; }
    public string Title { get; }
    public string Scope { get; }
    public string ClaimBoundaryText { get; }
    public string VerificationOutcome { get; }
    public string RecoveryOutcome { get; }
    public string RecoveryRehearsalOutcome { get; }
    public string Qualification { get; }
    public int VerificationFailureCount { get; }
    public int UnaccountedSourceCount { get; }
    public int UnexplainedTargetCount { get; }
    public IReadOnlyDictionary<string, int> FindingsByRuleAndCode { get; }
    public IReadOnlyList<string> Concepts { get; }
    public IReadOnlyList<string> RuleTypes { get; }

    private FhirAssuranceReport(string project, VerificationResult verification, RecoveryRunResult recovery)
    {
        Format = FormatVersion;
        Project = string.IsNullOrWhiteSpace(project) ? throw new ArgumentException("Project name is required.", nameof(project)) : project.Trim();
        Title = "FHIR-Style Healthcare Assurance";
        Scope = "Synthetic selected-field migration assurance";
        ClaimBoundaryText = ClaimBoundary;
        VerificationOutcome = verification.Run.Outcome.ToString();
        RecoveryOutcome = recovery.Assessment.Outcome.ToString();
        RecoveryRehearsalOutcome = recovery.Rehearsal.Outcome.ToString();
        Qualification = recovery.Qualification.Status.ToString().ToUpperInvariant();
        var failures = verification.Findings.Where(finding => finding.Result == EvidenceResult.Fail).ToArray();
        VerificationFailureCount = failures.Length;
        UnaccountedSourceCount = failures.Count(finding => finding.Code == "UnaccountedArtifact");
        UnexplainedTargetCount = failures.Count(finding => finding.Code is "MissingLineage" or "UnexpectedTarget");
        FindingsByRuleAndCode = new ReadOnlyDictionary<string, int>(failures
            .GroupBy(finding => $"{finding.RuleId.Value}:{finding.Code}", StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal));
        var pack = new FhirPack();
        Concepts = pack.Metadata.Concepts.Select(concept => concept.SemanticType).Order(StringComparer.Ordinal).ToArray();
        RuleTypes = pack.RuleFactories.Select(factory => factory.Type).Order(StringComparer.Ordinal).ToArray();
    }

    public static FhirAssuranceReport Create(string project, VerificationResult verification, RecoveryRunResult recovery)
    {
        ArgumentNullException.ThrowIfNull(verification);
        ArgumentNullException.ThrowIfNull(recovery);
        return new FhirAssuranceReport(project, verification, recovery);
    }
}
