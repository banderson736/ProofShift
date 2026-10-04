using System.Runtime.CompilerServices;
using ProofShift.Configuration;
using ProofShift.Domain;
using ProofShift.Evidence;
using ProofShift.Packs.Pension;
using ProofShift.Recovery;
using ProofShift.Verification;
using Xunit;

namespace ProofShift.EndToEnd.Tests;

public sealed class PensionSemanticRuleTests
{
    [Fact]
    public async Task PensionRulesVerifyExternalTargetWithoutProjectionExecutionAndDetectExactDefectCorpus()
    {
        var source = PensionSyntheticDatasetGenerator.Generate().ToArray();
        var expected = PensionTargetDataModel.Transform(source).ToArray();
        var defective = PensionDefectInjector.InjectTargetDefects(expected).ToArray();
        var definitions = RuleDefinitions();

        var cleanFindings = await EvaluateAsync(source, expected, expected, definitions);
        Assert.DoesNotContain(cleanFindings, finding => finding.Result == EvidenceResult.Fail);

        var memberOneCredits = expected.Where(item => item.Kind == PensionRecordKind.ServiceCredit &&
            ((StringValue)item.Values["participant_id"]).Value == "M00000001").Take(2).ToArray();
        var mergedCredit = memberOneCredits[0].With("service_credit", new DecimalValue(
            ((DecimalValue)memberOneCredits[0].Values["service_credit"]).Value +
            ((DecimalValue)memberOneCredits[1].Values["service_credit"]).Value));
        var regroupedTarget = expected.Where(item => item.Kind != PensionRecordKind.ServiceCredit ||
            item.Identity != memberOneCredits[0].Identity && item.Identity != memberOneCredits[1].Identity)
            .Append(mergedCredit).ToArray();
        var serviceCreditOnly = await EvaluateAsync(source, expected, regroupedTarget,
            [Rule("service-credit-representation", "pension-service-credit-total", ("targetNode", "target-servicecredit"),
             ("semanticType", "Pension.ServiceCredit"), ("amountField", "service_credit"), ("groupBy", "participant_id"), ("tolerance", "0.01"))]);
        Assert.DoesNotContain(serviceCreditOnly, finding => finding.Result == EvidenceResult.Fail);

        var findings = await EvaluateAsync(source, expected, defective, definitions);
        var failures = findings.Where(finding => finding.Result == EvidenceResult.Fail)
            .GroupBy(finding => finding.Code, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var expectedFailures = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["MissingMember"] = 7,
            ["DuplicateMember"] = 4,
            ["WrongMemberStatus"] = 3,
            ["MissingEmploymentPeriod"] = 12,
            ["IncorrectEmploymentDate"] = 8,
            ["IncorrectServiceCreditTotal"] = 17,
            ["MissingContribution"] = 39,
            ["DuplicateContribution"] = 8,
            ["IncorrectContributionAmount"] = 6,
            ["BenefitPaymentAmountMismatch"] = 11,
            ["BrokenBeneficiaryRelationship"] = 6,
            ["WrongMemberBeneficiary"] = 2,
            ["RetirementElectionMappingMismatch"] = 3,
            ["CodeTransformationMismatch"] = 5,
            ["MissingDocument"] = 9,
            ["WrongMemberDocument"] = 4,
            ["MissingHistoricalExport"] = 3
        };

        foreach (var (code, count) in expectedFailures)
            Assert.True(count == failures[code], $"Finding code {code}: expected {count}, detected {failures[code]}.");
        var aggregateCodes = failures.Keys.Where(code => code.EndsWith("TotalMismatch", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["BenefitPaymentTotalMismatch", "ContributionPeriodTotalMismatch"], aggregateCodes);
        Assert.Equal(expectedFailures.Keys.Order(StringComparer.Ordinal),
            failures.Keys.Except(aggregateCodes, StringComparer.Ordinal).Order(StringComparer.Ordinal));
        Assert.Equal(2, PensionDefectCounts.V1.FalseReversibleTransformations);
    }

    [Fact]
    public void VersionedFalseReverseDeclarationsAreRejectedByTheGenericRecoveryAnalyzer()
    {
        var sourceNode = new MigrationNodeId(Guid.NewGuid());
        var targetNode = new MigrationNodeId(Guid.NewGuid());
        var edges = new[]
        {
            new MigrationEdge(new MigrationEdgeId(Guid.NewGuid()), PensionDefectInjector.FalseReverseDeclarations[0].EdgeName,
                [sourceNode], [targetNode], new MigrationOperation(MigrationOperationType.Transform,
                    fields: [new TransformationFieldDefinition("name", "NAME",
                        [new TransformationStep(TransformationStepType.Trim, "1"),
                         new TransformationStep(TransformationStepType.NormalizeString, "1")])]), "1",
                new RecoveryDefinition(RecoveryMode.Reverse)),
            new MigrationEdge(new MigrationEdgeId(Guid.NewGuid()), PensionDefectInjector.FalseReverseDeclarations[1].EdgeName,
                [sourceNode], [targetNode], new MigrationOperation(MigrationOperationType.Transform,
                    fields: [new TransformationFieldDefinition("status", "STATUS",
                        [new TransformationStep(TransformationStepType.CodeMap, "1",
                            [new KeyValuePair<string, string>("A", "ACTIVE"), new KeyValuePair<string, string>("C", "ACTIVE")])])]), "1",
                new RecoveryDefinition(RecoveryMode.Reverse))
        };

        Assert.Equal(PensionDefectCounts.V1.FalseReversibleTransformations, edges.Length);
        Assert.Equal(PensionDefectInjector.FalseReverseDeclarations.Select(item => item.EdgeName), edges.Select(edge => edge.Name));
        Assert.All(edges, edge => Assert.False(TransformationLossAnalyzer.Analyze(edge).IsReversible));
    }

    private static async Task<List<VerificationFinding>> EvaluateAsync(PensionSyntheticRecord[] source,
        PensionSyntheticRecord[] expected, PensionSyntheticRecord[] actual, VerificationRuleDefinition[] definitions)
    {
        var nodes = Enum.GetValues<PensionRecordKind>().SelectMany(kind => new[]
        {
            MakeNode(kind, VerificationArtifactRole.Source),
            MakeNode(kind, VerificationArtifactRole.ExpectedTarget)
        }).ToArray();
        var graph = new MigrationGraph(new MigrationGraphId(Guid.NewGuid()), nodes, [], new string('a', 64), "test-v1");
        var configuration = new LoadedProjectConfiguration(
            new RootConfigurationDto(1, new ProjectConfigurationDto("synthetic-ps09", "Pension Rules"), null, [], null, null, null),
            [], [], [], "synthetic-configuration", new string('b', 64));
        var binding = new ProjectionVerificationBinding(new RunId(Guid.NewGuid()), new string('b', 64), graph.GraphHash,
            new CheckpointId(Guid.NewGuid()), new string('c', 64), new string('d', 64), new string('e', 64), "test-v1",
            source.LongLength, expected.LongLength, "succeeded", new string('f', 64), "external-fixture", []);
        var workspace = new FixtureVerificationWorkspace(
            ConvertRecords(source, VerificationArtifactRole.Source),
            ConvertRecords(expected, VerificationArtifactRole.ExpectedTarget),
            ConvertRecords(actual, VerificationArtifactRole.ActualTarget));
        var context = new VerificationExecutionContext(configuration, graph, binding, new RunId(Guid.NewGuid()), workspace);
        var rules = new VerificationRuleRegistry([new PensionPack()]).Resolve(definitions);
        var findings = new List<VerificationFinding>();
        foreach (var rule in rules.Rules)
        {
            await foreach (var finding in rule.EvaluateAsync(context, TestContext.Current.CancellationToken)
                .WithCancellation(TestContext.Current.CancellationToken).ConfigureAwait(false))
                findings.Add(finding);
        }
        var evidence = new EvidenceGraph(context.VerificationRunId, findings.Select(finding => new EvidenceRecord(
            new EvidenceId(Guid.NewGuid()), context.VerificationRunId, finding.Type, finding.RuleId,
            finding.RuleVersion, finding.Result, finding.Inputs, finding.Explanation, DateTimeOffset.UnixEpoch,
            finding.Expected, finding.Actual, finding.Severity, finding.Code)));
        Assert.Equal(findings.Count, evidence.Records.Count);
        return findings;
    }

    private static MigrationNode MakeNode(PensionRecordKind kind, VerificationArtifactRole role)
    {
        var source = role == VerificationArtifactRole.Source;
        var name = NodeName(kind, source);
        var nodeId = new MigrationNodeId(Guid.NewGuid());
        var semanticType = PensionSyntheticDatasetGenerator.Generate().First(record => record.Kind == kind).SemanticType;
        return new MigrationNode(nodeId, name, source ? MigrationNodeType.Source : MigrationNodeType.Target,
            semanticType, new SystemId(source ? "legacy" : "external-target"),
            new StorageEndpointId(source ? "legacy-endpoint" : "target-endpoint"),
            new ArtifactSelector("fixture", [new KeyValuePair<string, string>("name", name)], ["identity"]));
    }

    private static VerificationArtifactRecord[] ConvertRecords(
        IEnumerable<PensionSyntheticRecord> records, VerificationArtifactRole role) => records.Select(record =>
    {
        var source = role == VerificationArtifactRole.Source;
        var node = NodeName(record.Kind, source);
        var artifact = new ArtifactReference(new ArtifactId($"{role}:{record.Kind}:{record.Identity}"),
            new SystemId(source ? "legacy" : "external-target"),
            new StorageEndpointId(source ? "legacy-endpoint" : "target-endpoint"), "synthetic-record", record.Identity);
        return new VerificationArtifactRecord(node, role, record.SemanticType, artifact, record.Values);
    }).ToArray();

    private static string NodeName(PensionRecordKind kind, bool source) =>
        $"{(source ? "source" : "target")}-{kind.ToString().ToLowerInvariant()}";

    private static VerificationRuleDefinition[] RuleDefinitions() =>
    [
        Rule("member-presence", "pension-member-presence", ("targetNode", "target-member"), ("businessKey", "member_id")),
        Rule("member-uniqueness", "pension-member-uniqueness", ("targetNode", "target-member"), ("businessKey", "member_id")),
        Rule("member-status", "pension-member-status", ("targetNode", "target-member"), ("businessKey", "member_id"), ("attribute", "status")),
        Rule("employment-timeline", "pension-employment-timeline", ("sourceNode", "source-employment"), ("targetNode", "target-employment"),
            ("sourceMemberField", "member_id"), ("sourceStartField", "effective_from"), ("sourceEndField", "effective_to"),
            ("sourceStatusField", "status"), ("targetMemberField", "participant_id"), ("targetEventField", "event_code"), ("targetDateField", "event_date")),
        Rule("contribution-accounting", "pension-contribution-accounting", ("targetNode", "target-contribution"),
            ("transactionField", "contribution_id"), ("amountField", "contribution_amount"),
            ("compareFields", "contribution_id,participant_id,payroll_period,contribution_kind"), ("tolerance", "0.01")),
        Rule("contribution-total", "pension-contribution-total", ("targetNode", "target-contribution"),
            ("amountField", "contribution_amount"), ("groupBy", "participant_id,payroll_period,contribution_kind"), ("tolerance", "0.01")),
        Rule("service-credit-total", "pension-service-credit-total", ("targetNode", "target-servicecredit"),
            ("semanticType", "Pension.ServiceCredit"), ("amountField", "service_credit"), ("groupBy", "participant_id"), ("tolerance", "0.01")),
        Rule("beneficiary-relationship", "pension-beneficiary-relationship", ("targetNode", "target-beneficiary"),
            ("beneficiaryField", "beneficiary_id"), ("memberField", "participant_id"),
            ("memberNode", "target-member"), ("memberBusinessKey", "member_id"),
            ("relationshipField", "relationship_type"), ("allocationField", "allocation_pct"), ("allocationTolerance", "0.01")),
        Rule("retirement-election", "pension-retirement-election", ("targetNode", "target-retirementelection"),
            ("electionField", "election_id"), ("compareFields", "participant_id,option_code,effective_date")),
        Rule("benefit-payment", "pension-benefit-payment", ("targetNode", "target-benefitpayment"),
            ("paymentField", "payment_id"), ("amountField", "paid_amount"), ("tolerance", "0.01")),
        Rule("benefit-payment-total", "pension-benefit-payment-total", ("targetNode", "target-benefitpayment"),
            ("amountField", "paid_amount"), ("groupBy", "participant_id,payment_period"), ("tolerance", "0.01")),
        Rule("document-accounting", "pension-document-accounting", ("targetNode", "target-document"),
            ("documentField", "document_id"), ("contentHashField", "content_hash")),
        Rule("document-relationship", "pension-document-relationship", ("targetNode", "target-document"),
            ("documentField", "document_id"), ("memberField", "participant_id")),
        Rule("historical-export-accounting", "pension-document-accounting", ("targetNode", "target-historicalexport"),
            ("semanticType", "Pension.HistoricalExport"), ("documentField", "export_id"), ("contentHashField", "content_hash"),
            ("missingCode", "MissingHistoricalExport")),
        Rule("code-transformations", "pension-code-transformation", ("targetNode", "target-servicecredit"),
            ("semanticType", "Pension.ServiceCredit"), ("businessKey", "service_period_id"), ("attribute", "credit_code"))
    ];

    private static VerificationRuleDefinition Rule(string id, string type, params (string Key, string Value)[] options) =>
        new(new RuleId(id), type, "1", EvidenceSeverity.Critical,
            options.Select(option => new KeyValuePair<string, string>(option.Key, option.Value)));

    private sealed class FixtureVerificationWorkspace(
        IReadOnlyCollection<VerificationArtifactRecord> sourceRecords,
        IReadOnlyCollection<VerificationArtifactRecord> expectedRecords,
        IReadOnlyCollection<VerificationArtifactRecord> actualRecords) : IVerificationWorkspace
    {
        public long SourceArtifactCount => sourceRecords.Count;
        public long ExpectedTargetCount => expectedRecords.Count;
        public long ActualTargetCount => actualRecords.Count;

        public async IAsyncEnumerable<VerificationArtifactRecord> ReadArtifactRecordsAsync(VerificationArtifactRole role,
            string? nodeKey, string? semanticType, [EnumeratorCancellation] CancellationToken cancellationToken,
            IReadOnlyCollection<string>? orderByFields = null)
        {
            await Task.Yield();
            IEnumerable<VerificationArtifactRecord> records = role switch
            {
                VerificationArtifactRole.Source => sourceRecords,
                VerificationArtifactRole.ExpectedTarget => expectedRecords,
                VerificationArtifactRole.ActualTarget => actualRecords,
                _ => throw new ArgumentOutOfRangeException(nameof(role))
            };
            if (orderByFields is { Count: > 0 })
                records = records.OrderBy(record => string.Join('\u001f', orderByFields.Select(field =>
                    record.Values.TryGetValue(field, out var value) ? value switch
                    {
                        StringValue text => text.Value,
                        DecimalValue number => number.Value.ToString("G29", System.Globalization.CultureInfo.InvariantCulture),
                        IntegerValue number => number.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        DateValue date => date.Value.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                        _ => string.Empty
                    } : string.Empty)), StringComparer.Ordinal).ThenBy(record => record.Artifact.Identity, StringComparer.Ordinal);
            foreach (var record in records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (nodeKey is not null && record.NodeKey != nodeKey || semanticType is not null && record.SemanticType != semanticType) continue;
                yield return record;
            }
        }

        public IAsyncEnumerable<VerificationSourceFact> ReadSourceFactsAsync(CancellationToken cancellationToken) => Empty<VerificationSourceFact>(cancellationToken);
        public IAsyncEnumerable<VerificationTargetFact> ReadMaterializedJournalTargetsAsync(CancellationToken cancellationToken) => Empty<VerificationTargetFact>(cancellationToken);
        public IAsyncEnumerable<VerificationTargetFact> ReadActualTargetsAsync(CancellationToken cancellationToken) => Empty<VerificationTargetFact>(cancellationToken);
        public IAsyncEnumerable<VerificationTargetFact> ReadMissingTargetFactsAsync(CancellationToken cancellationToken) => Empty<VerificationTargetFact>(cancellationToken);
        public IAsyncEnumerable<VerificationTargetFact> ReadUnexpectedTargetFactsAsync(CancellationToken cancellationToken) => Empty<VerificationTargetFact>(cancellationToken);
        public IAsyncEnumerable<VerificationTargetFact> ReadDuplicateTargetFactsAsync(CancellationToken cancellationToken) => Empty<VerificationTargetFact>(cancellationToken);
        public IAsyncEnumerable<VerificationTargetFact> ReadTargetsWithoutLineageAsync(CancellationToken cancellationToken) => Empty<VerificationTargetFact>(cancellationToken);
        public IAsyncEnumerable<VerificationAttributeComparison> ReadAttributeComparisonsAsync(CancellationToken cancellationToken) => Empty<VerificationAttributeComparison>(cancellationToken);
        public IAsyncEnumerable<LineageRecord> ReadLineageAsync(string graphHash, IReadOnlyDictionary<string, MigrationNodeId> graphNodeIds,
            CancellationToken cancellationToken) => Empty<LineageRecord>(cancellationToken);

        public Task AddSourceArtifactAsync(string nodeKey, RecordEnvelope record, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> ContainsSourceArtifactAsync(string nodeKey, ArtifactReference artifact, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task AddExpectedTargetAsync(string nodeKey, RecordEnvelope expected, string sourceNodeKey, RecordEnvelope source,
            MigrationEdge edge, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> ContainsExpectedTargetAsync(string nodeKey, ArtifactReference target, string sourceNodeKey,
            ArtifactReference source, MigrationEdgeId edgeId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task AddJournalEntryAsync(VerificationJournalEntry entry, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task AddTargetObservationAsync(string nodeKey, RecordEnvelope record, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static async IAsyncEnumerable<T> Empty<T>([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield break;
        }
    }
}
