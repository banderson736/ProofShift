using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using Npgsql;
using NpgsqlTypes;
using ProofShift.Configuration;
using ProofShift.Connectors.Abstractions;
using ProofShift.Connectors.Files;
using ProofShift.Connectors.Postgres;
using ProofShift.Domain;
using ProofShift.Engine;
using ProofShift.Evidence;
using ProofShift.Graph;
using ProofShift.Packs.Abstractions;
using ProofShift.Packs.Justice;
using ProofShift.Projection;
using ProofShift.Recovery;
using ProofShift.Snapshots;
using ProofShift.Verification;
using Testcontainers.PostgreSql;
using Xunit;

namespace ProofShift.EndToEnd.Tests;

[Collection("DockerIntegration")]
public sealed class JusticePhysicalAssuranceTests
{
    private const int CaseCount = 2_000;
    private const string SourceSecret = "PS010F_JUSTICE_SOURCE";
    private const string TargetSecret = "PS010F_JUSTICE_TARGET";
    private const string SourceFilesSecret = "PS010F_JUSTICE_SOURCE_FILES";
    private const string TargetFilesSecret = "PS010F_JUSTICE_TARGET_FILES";

    private static readonly JusticeDefect[] DefectManifest =
    [
        new("missing-case", "missing entity", "Justice.Case", "CASE000001", "target-presence", EvidenceResult.Fail, 9),
        new("duplicate-case", "duplicate identity", "Justice.Case", "CASE000002", "case-uniqueness", EvidenceResult.Fail, 1),
        new("duplicate-person", "duplicate identity", "Justice.Person", "P000003", "person-uniqueness", EvidenceResult.Fail, 1),
        new("party-orphan-case", "broken relationship", "Justice.CaseParty", "PARTY000003", "party-case-reference", EvidenceResult.Fail, 2),
        new("party-wrong-case", "incorrect relationship", "Justice.CaseParty", "PARTY000004", "party-case-match", EvidenceResult.Fail, 1),
        new("party-orphan-person", "broken relationship", "Justice.CaseParty", "PARTY000005", "party-person-reference", EvidenceResult.Fail, 1),
        new("orphan-charge", "broken relationship", "Justice.Charge", "CHG000006", "charge-case-reference", EvidenceResult.Fail, 1),
        new("case-status-map", "code transformation", "Justice.Case", "CASE000007", "case-status", EvidenceResult.Fail, 1),
        new("disposition-code-map", "code transformation", "Justice.Disposition", "DISP000008", "disposition-code", EvidenceResult.Fail, 1),
        new("filing-chronology", "temporal defect", "Justice.Filing", "FILE000009", "filing-event-order", EvidenceResult.Fail, 1),
        new("hearing-chronology", "temporal defect", "Justice.Hearing", "HEAR000010", "hearing-event-order", EvidenceResult.Fail, 1),
        new("sentence-disposition", "broken relationship", "Justice.Sentence", "SENT000011", "sentence-disposition-reference", EvidenceResult.Fail, 1),
        new("missing-document", "missing artifact", "Justice.Document", "DOC000020", "target-presence", EvidenceResult.Fail, 1),
        new("document-wrong-case", "incorrect relationship", "Justice.Document", "DOC000040", "document-case-match", EvidenceResult.Fail, 1),
        new("unexpected-case", "unexpected artifact", "Justice.Case", "CASE-UNEXPECTED", "unexpected-target", EvidenceResult.Fail, 1)
    ];

    [Fact]
    public async Task JusticePhysicalExternalAndShadowScenariosProveExactDefectsAndQualification()
    {
        Assert.Equal(DefectManifest.Length, DefectManifest.Select(defect => defect.Id).Distinct(StringComparer.Ordinal).Count());
        var fixture = CreateFixture(CaseCount);
        var root = Path.Combine(Path.GetTempPath(), $"proofshift-justice-f3-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        using var cleanup = new TemporaryDirectoryCleanup(root);
        var sourceFilesRoot = Path.Combine(root, "source-files");
        var targetFilesRoot = Path.Combine(root, "target-files");
        Directory.CreateDirectory(targetFilesRoot);
        await WriteSourceFilesAsync(fixture.Documents, sourceFilesRoot, TestContext.Current.CancellationToken);

        await using var sourcePostgres = new PostgreSqlBuilder("postgres:16-alpine").Build();
        try { await sourcePostgres.StartAsync(TestContext.Current.CancellationToken); }
        catch (DockerUnavailableException)
        {
            await sourcePostgres.DisposeAsync();
            throw Xunit.Sdk.SkipException.ForSkip("A Docker-compatible runtime is required for Justice physical assurance.");
        }
        await using var targetPostgres = new PostgreSqlBuilder("postgres:16-alpine").Build();
        try { await targetPostgres.StartAsync(TestContext.Current.CancellationToken); }
        catch (DockerUnavailableException)
        {
            await targetPostgres.DisposeAsync();
            throw Xunit.Sdk.SkipException.ForSkip("A Docker-compatible runtime is required for Justice physical assurance.");
        }
        await SeedSourceAsync(sourcePostgres, fixture, TestContext.Current.CancellationToken);
        await CreateTargetTemplatesAsync(targetPostgres, fixture, TestContext.Current.CancellationToken);

        var configuration = CreateConfiguration(sourcePostgres.GetConnectionString(), targetPostgres.GetConnectionString(), sourceFilesRoot, targetFilesRoot);
        var graph = CreateGraph(fixture, configuration);
        var sourceConnectors = new ConnectorRegistry([new PostgresSourceConnector(), new FilesystemSourceConnector()]);
        var postgresTarget = new PostgresShadowTargetConnector();
        var filesystemTarget = new FilesystemShadowTargetConnector();
        var targets = new ShadowTargetConnectorRegistry([postgresTarget, filesystemTarget]);
        var environment = new JusticeEnvironment(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [SourceSecret] = sourcePostgres.GetConnectionString(),
            [TargetSecret] = targetPostgres.GetConnectionString(),
            [SourceFilesSecret] = sourceFilesRoot,
            [TargetFilesSecret] = targetFilesRoot
        });
        var contextFactory = new RuntimeConnectorContextFactory(environment);
        var checkpointStore = new FileSystemSnapshotStore(Path.Combine(root, "checkpoints"));
        var inspection = await new SourceInspectionService(sourceConnectors, contextFactory)
            .InspectAsync(configuration, graph, TestContext.Current.CancellationToken);
        Assert.True(inspection.IsValid, string.Join(Environment.NewLine,
            inspection.Sources.SelectMany(source => source.Inspection.Issues).Select(issue => $"{issue.Code}: {issue.Message}")));
        var checkpoint = await new SnapshotCaptureService(sourceConnectors, checkpointStore, contextFactory)
            .CaptureAsync(configuration, graph, TestContext.Current.CancellationToken);
        Assert.Equal(CheckpointStatus.Complete, checkpoint.Status);
        Assert.NotNull(checkpoint.Checkpoint);

        var registry = new PackRegistry([new JusticePack()]);
        var ruleSet = registry.Resolve(configuration.Root.Packs).Resolve(CreateRules());
        var rulePlan = VerificationExecutionPlan.Create(ruleSet, graph);
        Assert.Equal(ruleSet.Rules.Count, rulePlan.Rules.Count);
        Assert.All(rulePlan.Rules.Where(workset => workset.Rule.Id.Value is "party-case-reference" or "party-person-reference" or "charge-case-reference" or "sentence-disposition-reference" or "document-case-reference"),
            workset => Assert.Equal(VerificationPartitionExecution.Global, workset.PartitionExecution));
        Assert.Contains(rulePlan.Rules.SelectMany(workset => workset.RequiredTargetFields), field => field == "case_id");

        var service = new VerificationService(checkpointStore);
        var cleanRows = BuildTargetRows(fixture);
        var expectedTargetCount = cleanRows.Values.Sum(rows => (long)rows.Count) + fixture.Documents.Length;
        var cleanExternal = await VerifyExternalAsync(service, configuration, graph, checkpoint, ruleSet, cleanRows,
            fixture.Documents, new HashSet<string>(StringComparer.Ordinal), targetPostgres, postgresTarget, filesystemTarget,
            contextFactory, Path.Combine(root, "external-corrected"));
        Assert.Equal(VerificationOutcome.Passed, cleanExternal.Run.Outcome);
        Assert.DoesNotContain(cleanExternal.Findings, finding => finding.Result == EvidenceResult.Fail);
        Assert.DoesNotContain(cleanExternal.Findings, finding => finding.Result == EvidenceResult.Fail &&
            finding.Code is "UnaccountedArtifact" or "MissingLineage" or "UnexpectedTarget");
        Assert.Equal(checkpoint.Checkpoint.ArtifactCount, cleanExternal.Ledger.DispositionCount);
        Assert.Equal(expectedTargetCount, cleanExternal.Ledger.LineageCount);
        Assert.Equal(expectedTargetCount, cleanExternal.Run.TargetArtifactCount);
        Assert.Contains(cleanExternal.Findings, finding => finding.RuleId.Value == "source-accounting" &&
            finding.Result == EvidenceResult.Pass && finding.Expected?.Value is IntegerValue total &&
            total.Value == checkpoint.Checkpoint.ArtifactCount);
        Assert.Contains(cleanExternal.Findings, finding => finding.Code == "ExternalTargetObserved" &&
            finding.Expected?.Value is StringValue observed && observed.Value == "externally-populated-target");
        var cleanRepeat = await VerifyExternalAsync(service, configuration, graph, checkpoint, ruleSet, cleanRows,
            fixture.Documents, new HashSet<string>(StringComparer.Ordinal), targetPostgres, postgresTarget, filesystemTarget,
            contextFactory, Path.Combine(root, "external-corrected-repeat"));
        Assert.NotEqual(cleanExternal.Run.Id, cleanRepeat.Run.Id);
        Assert.Equal(cleanExternal.Run.TargetFingerprint, cleanRepeat.Run.TargetFingerprint);
        Assert.Equal(cleanExternal.Run.EvidenceFingerprint, cleanRepeat.Run.EvidenceFingerprint);
        Assert.Equal(cleanExternal.Ledger.Fingerprint, cleanRepeat.Ledger.Fingerprint);
        Assert.Equal(cleanExternal.Ledger.DispositionCount, cleanRepeat.Ledger.DispositionCount);
        Assert.Equal(cleanExternal.Ledger.LineageCount, cleanRepeat.Ledger.LineageCount);

        var defectiveRows = CloneRows(cleanRows);
        ApplyDefects(defectiveRows);
        var defective = await VerifyExternalAsync(service, configuration, graph, checkpoint, ruleSet, defectiveRows,
            fixture.Documents, new HashSet<string>(["DOC000020"], StringComparer.Ordinal), targetPostgres, postgresTarget,
            filesystemTarget, contextFactory, Path.Combine(root, "external-defective"));
        Assert.Equal(VerificationOutcome.Failed, defective.Run.Outcome);
        var failures = defective.Findings.Where(finding => finding.Result == EvidenceResult.Fail).ToArray();
        Assert.Equal(DefectManifest.Sum(defect => defect.ExpectedFindingCount), failures.Length);
        var expectedFailureCounts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["target-presence"] = 2, ["case-uniqueness"] = 1, ["person-uniqueness"] = 1,
            ["party-case-reference"] = 2, ["party-person-reference"] = 1,
            ["charge-case-reference"] = 2, ["case-status"] = 1, ["disposition-code"] = 1,
            ["filing-event-order"] = 1, ["hearing-event-order"] = 1, ["party-case-match"] = 2,
            ["filing-case-reference"] = 1, ["hearing-case-reference"] = 1,
            ["sentence-disposition-reference"] = 1, ["document-case-match"] = 1, ["unexpected-target"] = 1,
            ["target-lineage"] = 1, ["proofshift.verification.materialized-state"] = 3
        };
        var observedFailureCounts = failures.GroupBy(finding => finding.RuleId.Value, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        Assert.Equal(expectedFailureCounts.OrderBy(pair => pair.Key, StringComparer.Ordinal),
            observedFailureCounts.OrderBy(pair => pair.Key, StringComparer.Ordinal));
        Assert.All(failures, finding =>
        {
            Assert.Equal("1", finding.RuleVersion);
            Assert.NotEmpty(finding.Inputs);
            Assert.False(string.IsNullOrWhiteSpace(finding.Explanation));
        });
        Assert.All(DefectManifest, defect => Assert.Contains(failures,
            finding => finding.RuleId.Value == defect.ExpectedRuleId));
        Assert.Equal(expectedTargetCount - 2, defective.Ledger.LineageCount);
        Assert.Equal(2, defective.Findings.Count(finding => finding.Code == "UnaccountedArtifact"));
        Assert.Equal(2, defective.Findings.Count(finding => finding.Code == "MissingLineage"));

        var evidenceStore = new FileSystemEvidenceStore(Path.Combine(root, ".proofshift", "verifications"));
        var recoveryStore = new FileSystemRecoveryArtifactStore(Path.Combine(root, ".proofshift", "recovery"));
        var dryRun = await new DryRunOrchestrator(sourceConnectors, targets, checkpointStore, service,
            new RecoveryService(checkpointStore,
                new RecoveryCompensatorRegistry([new ShadowBaselineRestoreCompensator()])), contextFactory)
            .RunAsync(configuration, graph, ruleSet, new EffectiveRecoveryPolicy(), Path.Combine(root, "projected-corrected"),
                Path.Combine(root, "temporary", "projected-corrected"), "0.10F", evidenceStore, recoveryStore,
                TestContext.Current.CancellationToken);
        Assert.True(dryRun.Outcome == DryRunExecutionOutcome.Qualified,
            $"Justice shadow dry run was {dryRun.Outcome} ({dryRun.FailureCode}); verification failures: {string.Join(", ", dryRun.Verification?.Findings.Where(finding => finding.Result == EvidenceResult.Fail).Select(finding => finding.RuleId.Value + ":" + finding.Code) ?? [])}; recovery issues: {string.Join(", ", dryRun.Recovery?.Assessment.Issues.Select(issue => issue.Code) ?? [])}; rehearsal: {dryRun.Recovery?.Rehearsal?.Outcome}");
        Assert.Equal(VerificationOutcome.Passed, dryRun.Verification!.Run.Outcome);
        Assert.Equal(DryRunQualificationStatus.Qualified, dryRun.Recovery!.Qualification.Status);
        Assert.Equal(RecoveryRehearsalOutcome.Passed, dryRun.Recovery.Rehearsal.Outcome);
        Assert.NotNull(dryRun.VerificationEvidence);
        Assert.NotNull(dryRun.RecoveryArtifacts);
        Assert.DoesNotContain(dryRun.Verification.Findings, finding => finding.Result == EvidenceResult.Fail &&
            finding.Code is "UnaccountedArtifact" or "MissingLineage" or "UnexpectedTarget");
        Assert.DoesNotContain(dryRun.Verification.Findings, finding => finding.Result == EvidenceResult.Fail);
        Assert.Equal(RecoveryAssessmentOutcome.Passed, dryRun.Recovery.Assessment.Outcome);

        var report = JusticeAssuranceReport.Create(configuration.Root.Project!.Name!, dryRun.Verification!, dryRun.Recovery!);
        var reportJson = JsonSerializer.Serialize(report);
        var reportPath = Path.Combine(root, "justice-assurance-report.json");
        await File.WriteAllTextAsync(reportPath, reportJson, TestContext.Current.CancellationToken);
        reportJson = await File.ReadAllTextAsync(reportPath, TestContext.Current.CancellationToken);
        Assert.Contains("Justice.Case", reportJson, StringComparison.Ordinal);
        Assert.Contains("justice-reference-integrity", reportJson, StringComparison.Ordinal);
        Assert.Equal(0, report.VerificationFailureCount);
        Assert.Equal(0, report.UnaccountedSourceCount);
        Assert.Equal(0, report.UnexplainedTargetCount);
        Assert.Equal("QUALIFIED", report.Qualification);
        Assert.DoesNotContain("Pension.Member", reportJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Utility.Account", reportJson, StringComparison.Ordinal);
        Assert.DoesNotContain("meter", reportJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("beneficiary", reportJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("does not validate law", reportJson, StringComparison.Ordinal);
    }

    private static Fixture CreateFixture(int caseCount)
    {
        var specs = new[]
        {
            Spec("source-person", "target-person", "legacy.person", "person", "Justice.Person", "PERSON_NO", [Map("person_id", "PERSON_NO"), Map("display_name", "PERSON_NAME")], "person_id"),
            Spec("source-case", "target-case", "legacy.case_record", "case_record", "Justice.Case", "CASE_ID", [Map("case_id", "CASE_ID"), Map("status", "CASE_STATUS_CD", ("A", "ACTIVE"), ("P", "PENDING")), Map("opened_at", "OPENED_AT")], "case_id"),
            Spec("source-case-party", "target-case-party", "legacy.case_party", "case_party", "Justice.CaseParty", "PARTY_NO", [Map("party_id", "PARTY_NO"), Map("case_id", "CASE_ID"), Map("person_id", "PERSON_NO"), Map("role", "PARTY_ROLE_CD", ("DEF", "DEFENDANT"))], "party_id"),
            Spec("source-charge", "target-charge", "legacy.charge", "charge", "Justice.Charge", "CHARGE_NO", [Map("charge_id", "CHARGE_NO"), Map("case_id", "CASE_ID"), Map("charge_code", "CHARGE_CD")], "charge_id"),
            Spec("source-filing", "target-filing", "legacy.filing", "filing", "Justice.Filing", "FILING_NO", [Map("filing_id", "FILING_NO"), Map("case_id", "CASE_ID"), Map("case_opened_at", "CASE_OPENED_AT"), Map("filed_at", "FILED_AT")], "filing_id"),
            Spec("source-hearing", "target-hearing", "legacy.hearing", "hearing", "Justice.Hearing", "HEARING_NO", [Map("hearing_id", "HEARING_NO"), Map("case_id", "CASE_ID"), Map("filed_at", "FILED_AT"), Map("hearing_at", "HEARING_AT")], "hearing_id"),
            Spec("source-disposition", "target-disposition", "legacy.disposition", "disposition", "Justice.Disposition", "DISP_NO", [Map("disposition_id", "DISP_NO"), Map("charge_id", "CHARGE_NO"), Map("outcome_code", "DISP_CD", ("DISMIS", "DISMISSED"), ("CONV", "CONVICTED")), Map("disposition_at", "DISP_AT")], "disposition_id"),
            Spec("source-sentence", "target-sentence", "legacy.sentence", "sentence", "Justice.Sentence", "SENTENCE_NO", [Map("sentence_id", "SENTENCE_NO"), Map("disposition_id", "DISP_NO"), Map("effective_at", "EFFECTIVE_AT")], "sentence_id"),
            Spec("source-document", "target-document", "legacy.document", "document", "Justice.Document", "DOCUMENT_NO", [Map("document_id", "DOCUMENT_NO"), Map("case_id", "CASE_ID"), Map("relative_path", "RELATIVE_PATH"), Map("sha256", "SHA256")], "document_id")
        };
        var rows = new Dictionary<string, List<Dictionary<string, string>>>(StringComparer.Ordinal);
        foreach (var spec in specs) rows.Add(spec.TargetNode, []);
        for (var index = 1; index <= caseCount; index++)
        {
            var key = index.ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
            var caseId = $"CASE{key}";
            var personId = $"P{key}";
            var opened = $"2024-01-{(index % 20 + 1):D2}T09:00:00";
            var filed = $"2024-02-{(index % 20 + 1):D2}T09:00:00";
            var hearing = $"2024-03-{(index % 20 + 1):D2}T10:00:00";
            var disposition = $"DISP{key}";
            var charge = $"CHG{key}";
            Add(rows, "target-person", ("PERSON_NO", personId), ("PERSON_NAME", $"Synthetic Person {key}"));
            Add(rows, "target-case", ("CASE_ID", caseId), ("CASE_STATUS_CD", index % 2 == 0 ? "A" : "P"), ("OPENED_AT", opened));
            Add(rows, "target-case-party", ("PARTY_NO", $"PARTY{key}"), ("CASE_ID", caseId), ("PERSON_NO", personId), ("PARTY_ROLE_CD", "DEF"));
            Add(rows, "target-charge", ("CHARGE_NO", charge), ("CASE_ID", caseId), ("CHARGE_CD", "SYNTH-01"));
            Add(rows, "target-filing", ("FILING_NO", $"FILE{key}"), ("CASE_ID", caseId), ("CASE_OPENED_AT", opened), ("FILED_AT", filed));
            Add(rows, "target-hearing", ("HEARING_NO", $"HEAR{key}"), ("CASE_ID", caseId), ("FILED_AT", filed), ("HEARING_AT", hearing));
            Add(rows, "target-disposition", ("DISP_NO", disposition), ("CHARGE_NO", charge), ("DISP_CD", index % 2 == 0 ? "DISMIS" : "CONV"), ("DISP_AT", $"2024-04-{(index % 20 + 1):D2}T12:00:00"));
            Add(rows, "target-sentence", ("SENTENCE_NO", $"SENT{key}"), ("DISP_NO", disposition), ("EFFECTIVE_AT", $"2024-05-{(index % 20 + 1):D2}T00:00:00"));
            if (index % 20 == 0)
            {
                var doc = $"DOC{key}";
                var path = $"cases/{caseId}/{doc}.txt";
                var bytes = Encoding.UTF8.GetBytes($"Synthetic Justice case document {doc} for {caseId}.");
                rows["target-document"].Add(new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["DOCUMENT_NO"] = doc, ["CASE_ID"] = caseId, ["RELATIVE_PATH"] = path, ["SHA256"] = Sha(bytes)
                });
            }
        }
        var documentFiles = rows["target-document"].Select(row =>
        {
            var bytes = Encoding.UTF8.GetBytes($"Synthetic Justice case document {row["DOCUMENT_NO"]} for {row["CASE_ID"]}.");
            return new JusticeDocument(row["DOCUMENT_NO"], row["CASE_ID"], row["RELATIVE_PATH"], bytes, row["SHA256"]);
        }).ToArray();
        return new Fixture(specs, rows, documentFiles);
    }

    private static void Add(Dictionary<string, List<Dictionary<string, string>>> rows, string node,
        params (string Key, string Value)[] values) => rows[node].Add(values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));

    private static EntitySpec Spec(string sourceNode, string targetNode, string sourceTable, string targetTable,
        string semanticType, string sourceIdentity, FieldMap[] fields, string identityField) =>
        new(sourceNode, targetNode, sourceTable, targetTable, semanticType, sourceIdentity, identityField, fields);

    private static FieldMap Map(string target, string source, params (string Source, string Target)[] codes) =>
        new(target, source, codes.Length == 0 ? null : codes.ToDictionary(item => item.Source, item => item.Target, StringComparer.Ordinal));

    private static Dictionary<string, List<Dictionary<string, string>>> BuildTargetRows(Fixture fixture) =>
        fixture.Entities.ToDictionary(entity => entity.TargetNode, entity => fixture.SourceRows[entity.TargetNode].Select(row =>
            entity.Fields.ToDictionary(field => field.Target,
                field => field.Codes is not null && field.Codes.TryGetValue(row[field.Source], out var mapped) ? mapped : row[field.Source],
                StringComparer.Ordinal)).ToList(), StringComparer.Ordinal);

    private static Dictionary<string, List<Dictionary<string, string>>> CloneRows(IReadOnlyDictionary<string, List<Dictionary<string, string>>> rows) =>
        rows.ToDictionary(pair => pair.Key, pair => pair.Value.Select(row => new Dictionary<string, string>(row, StringComparer.Ordinal)).ToList(), StringComparer.Ordinal);

    private static void ApplyDefects(Dictionary<string, List<Dictionary<string, string>>> rows)
    {
        rows["target-case"].RemoveAll(row => row["case_id"] == "CASE000001");
        rows["target-case"].Add(new Dictionary<string, string>(rows["target-case"].Single(row => row["case_id"] == "CASE000002"), StringComparer.Ordinal));
        rows["target-person"].Add(new Dictionary<string, string>(rows["target-person"].Single(row => row["person_id"] == "P000003"), StringComparer.Ordinal));
        Find(rows, "target-case-party", "party_id", "PARTY000003")["case_id"] = "CASE-GHOST";
        Find(rows, "target-case-party", "party_id", "PARTY000004")["case_id"] = "CASE000005";
        Find(rows, "target-case-party", "party_id", "PARTY000005")["person_id"] = "P-GHOST";
        Find(rows, "target-charge", "charge_id", "CHG000006")["case_id"] = "CASE-GHOST";
        Find(rows, "target-case", "case_id", "CASE000007")["status"] = "INVALID";
        Find(rows, "target-disposition", "disposition_id", "DISP000008")["outcome_code"] = "INVALID";
        Find(rows, "target-filing", "filing_id", "FILE000009")["case_opened_at"] = "2024-12-01T09:00:00";
        Find(rows, "target-hearing", "hearing_id", "HEAR000010")["hearing_at"] = "2024-01-01T10:00:00";
        Find(rows, "target-sentence", "sentence_id", "SENT000011")["disposition_id"] = "DISP-GHOST";
        Find(rows, "target-document", "document_id", "DOC000040")["case_id"] = "CASE000041";
        rows["target-case"].Add(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["case_id"] = "CASE-UNEXPECTED", ["status"] = "ACTIVE", ["opened_at"] = "2024-01-01T09:00:00"
        });
    }

    private static Dictionary<string, string> Find(Dictionary<string, List<Dictionary<string, string>>> rows,
        string node, string field, string value) => rows[node].Single(row => row[field] == value);

    private static VerificationRuleDefinition[] CreateRules()
    {
        var rules = new List<VerificationRuleDefinition>
        {
            Rule("source-accounting", "source-artifact-accounting"),
            Rule("target-lineage", "target-lineage"),
            Rule("target-presence", "target-presence"),
            Rule("unexpected-target", "unexpected-target"),
            Rule("case-uniqueness", "entity-uniqueness", Text("targetNode", "target-case"), Text("semanticType", "Justice.Case")),
            Rule("person-uniqueness", "entity-uniqueness", Text("targetNode", "target-person"), Text("semanticType", "Justice.Person")),
            AttributeRule("case-status", "target-case", "Justice.Case", "case_id", "status"),
            AttributeRule("filing-date-preservation", "target-filing", "Justice.Filing", "filing_id", "filed_at"),
            AttributeRule("party-case-match", "target-case-party", "Justice.CaseParty", "party_id", "case_id"),
            AttributeRule("disposition-code", "target-disposition", "Justice.Disposition", "disposition_id", "outcome_code"),
            AttributeRule("document-case-match", "target-document", "Justice.Document", "document_id", "case_id"),
            EventRule("filing-event-order", "target-filing", "Justice.Filing", "filing_id", "case_opened_at", "filed_at"),
            EventRule("hearing-event-order", "target-hearing", "Justice.Hearing", "hearing_id", "filed_at", "hearing_at"),
            ReferenceRule("party-case-reference", "target-case-party", "Justice.CaseParty", "case_id", "target-case", "Justice.Case", "case_id"),
            ReferenceRule("party-person-reference", "target-case-party", "Justice.CaseParty", "person_id", "target-person", "Justice.Person", "person_id"),
            ReferenceRule("charge-case-reference", "target-charge", "Justice.Charge", "case_id", "target-case", "Justice.Case", "case_id"),
            ReferenceRule("filing-case-reference", "target-filing", "Justice.Filing", "case_id", "target-case", "Justice.Case", "case_id"),
            ReferenceRule("hearing-case-reference", "target-hearing", "Justice.Hearing", "case_id", "target-case", "Justice.Case", "case_id"),
            ReferenceRule("disposition-charge-reference", "target-disposition", "Justice.Disposition", "charge_id", "target-charge", "Justice.Charge", "charge_id"),
            ReferenceRule("sentence-disposition-reference", "target-sentence", "Justice.Sentence", "disposition_id", "target-disposition", "Justice.Disposition", "disposition_id"),
            ReferenceRule("document-case-reference", "target-document", "Justice.Document", "case_id", "target-case", "Justice.Case", "case_id")
        };
        return [.. rules];
    }

    private static VerificationRuleDefinition AttributeRule(string id, string node, string semanticType, string identity, string attribute) =>
        Rule(id, "justice-attribute-equality", Text("targetNode", node), Text("semanticType", semanticType),
            Text("identityField", identity), Text("attribute", attribute));

    private static VerificationRuleDefinition EventRule(string id, string node, string semanticType, string identity,
        string earlier, string later) => Rule(id, "justice-event-order", Text("targetNode", node), Text("semanticType", semanticType),
            Text("identityField", identity), Text("earlierField", earlier), Text("laterField", later));

    private static VerificationRuleDefinition ReferenceRule(string id, string sourceNode, string sourceSemantic,
        string referenceField, string referenceNode, string referenceSemantic, string referenceKey) =>
        Rule(id, "justice-reference-integrity", Text("sourceNode", sourceNode), Text("semanticType", sourceSemantic),
            Text("referenceField", referenceField), Text("referenceNode", referenceNode),
            Text("referenceSemanticType", referenceSemantic), Text("referenceKeyField", referenceKey));

    private static VerificationRuleDefinition Rule(string id, string type, params KeyValuePair<string, ValueNode>[] options) =>
        new(new RuleId(id), type, "1", EvidenceSeverity.Critical, structuredOptions: options);

    private static KeyValuePair<string, ValueNode> Text(string name, string value) => new(name, new StringValue(value));

    private static LoadedProjectConfiguration CreateConfiguration(string sourceConnection, string targetConnection,
        string sourceFiles, string targetFiles)
    {
        var source = new SystemDefinition(new SystemId("justice-source"), "Synthetic Legacy Justice", SystemRole.Source,
        [
            new(new StorageEndpointId("justice-postgres-source"), new ConnectorId("postgres"), [new("connection", $"secret:{SourceSecret}")]),
            new(new StorageEndpointId("justice-source-files"), new ConnectorId("files"), [new("root", $"secret:{SourceFilesSecret}")])
        ]);
        var target = new SystemDefinition(new SystemId("justice-target"), "Synthetic Justice Target", SystemRole.ShadowTarget,
        [
            new(new StorageEndpointId("justice-postgres-target"), new ConnectorId("postgres"), [new("connection", $"secret:{TargetSecret}")]),
            new(new StorageEndpointId("justice-target-files"), new ConnectorId("files"), [new("root", $"secret:{TargetFilesSecret}")])
        ]);
        var root = new RootConfigurationDto(1, new ProjectConfigurationDto("justice-f3", "Synthetic Case Management Assurance"),
            null, [], null, null, null, packs: [new PackConfigurationDto("proofshift.justice", new JusticePack().Version)]);
        if (string.IsNullOrWhiteSpace(sourceConnection) || string.IsNullOrWhiteSpace(targetConnection))
            throw new ArgumentException("Physical Justice PostgreSQL endpoints are required.");
        return new LoadedProjectConfiguration(root, [], [source, target], [], "synthetic-justice-f3", new string('b', 64));
    }

    private static MigrationGraph CreateGraph(Fixture fixture, LoadedProjectConfiguration configuration)
    {
        var sourceSystem = configuration.Systems.Single(system => system.Id.Value == "justice-source").Id;
        var targetSystem = configuration.Systems.Single(system => system.Id.Value == "justice-target").Id;
        var nodes = new List<MigrationNode>();
        var edges = new List<MigrationEdge>();
        foreach (var entity in fixture.Entities)
        {
            var source = new MigrationNode(NodeId(entity.SourceNode), entity.SourceNode, MigrationNodeType.Source,
                entity.SemanticType, sourceSystem, new StorageEndpointId("justice-postgres-source"),
                new ArtifactSelector("table", [new("name", entity.SourceTable)], [entity.IdentitySource]));
            var target = new MigrationNode(NodeId(entity.TargetNode), entity.TargetNode, MigrationNodeType.Target,
                entity.SemanticType, targetSystem, new StorageEndpointId("justice-postgres-target"),
                new ArtifactSelector("table", [new("name", $"public.{entity.TargetTable}")], [entity.IdentityTarget]));
            nodes.Add(source);
            nodes.Add(target);
            var fields = entity.Fields.Select(field => new TransformationFieldDefinition(field.Target, field.Source,
                field.Codes is null ? [] : [new TransformationStep(TransformationStepType.CodeMap, "1",
                    field.Codes.Select(pair => new KeyValuePair<string, string>(pair.Key, pair.Value)))]));
            edges.Add(new MigrationEdge(EdgeId(entity.TargetNode), entity.TargetNode, [source.Id], [target.Id],
                new MigrationOperation(MigrationOperationType.Transform, fields: fields), "1",
                new RecoveryDefinition(RecoveryMode.Restore, requiresSnapshot: true)));
        }
        var sourceFiles = new MigrationNode(NodeId("source-document-payload"), "source-document-payload",
            MigrationNodeType.Source, "Justice.Document", sourceSystem, new StorageEndpointId("justice-source-files"),
            new ArtifactSelector("file-pattern", [new("pattern", "cases/*/*.txt")], ["relativePath"]));
        var targetFiles = new MigrationNode(NodeId("target-document-payload"), "target-document-payload",
            MigrationNodeType.Archive, "Justice.Document", targetSystem, new StorageEndpointId("justice-target-files"),
            new ArtifactSelector("file-pattern", [new("pathField", "relativePath")], ["relativePath"]));
        nodes.Add(sourceFiles);
        nodes.Add(targetFiles);
        edges.Add(new MigrationEdge(EdgeId("document-payload"), "document-payload", [sourceFiles.Id], [targetFiles.Id],
            new MigrationOperation(MigrationOperationType.Archive), "1", new RecoveryDefinition(RecoveryMode.Restore, requiresSnapshot: true)));
        var orderedNodes = nodes.OrderBy(node => node.Name, StringComparer.Ordinal).ToArray();
        var orderedEdges = edges.OrderBy(edge => edge.Name, StringComparer.Ordinal).ToArray();
        var names = orderedNodes.ToDictionary(node => node.Id, node => node.Name);
        var canonical = GraphCanonicalizer.Canonicalize(1, orderedNodes, orderedEdges, names);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        return new MigrationGraph(new MigrationGraphId(NodeId("justice-f3-graph").Value), orderedNodes, orderedEdges,
            hash, GraphCanonicalizer.FormatVersion);
    }

    private static async Task SeedSourceAsync(PostgreSqlContainer postgres, Fixture fixture, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(postgres.GetConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using (var schema = connection.CreateCommand())
        {
            schema.CommandText = "CREATE SCHEMA legacy";
            await schema.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var entity in fixture.Entities)
        {
            await using (var create = connection.CreateCommand())
            {
                create.CommandText = $"CREATE TABLE {Quote("legacy")}.{Quote(entity.SourceTable[(entity.SourceTable.IndexOf('.') + 1)..])} ({string.Join(", ", entity.Fields.Select(field => $"{Quote(field.Source)} text NOT NULL"))})";
                await create.ExecuteNonQueryAsync(cancellationToken);
            }
            var copy = $"COPY {Quote("legacy")}.{Quote(entity.SourceTable[(entity.SourceTable.IndexOf('.') + 1)..])} ({string.Join(", ", entity.Fields.Select(field => Quote(field.Source)))}) FROM STDIN (FORMAT BINARY)";
            await using var importer = await connection.BeginBinaryImportAsync(copy, cancellationToken);
            foreach (var row in fixture.SourceRows[entity.TargetNode])
            {
                await importer.StartRowAsync(cancellationToken);
                foreach (var field in entity.Fields) await importer.WriteAsync(row[field.Source], NpgsqlDbType.Text, cancellationToken);
            }
            await importer.CompleteAsync(cancellationToken);
        }
    }

    private static async Task CreateTargetTemplatesAsync(PostgreSqlContainer postgres, Fixture fixture, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(postgres.GetConnectionString());
        await connection.OpenAsync(cancellationToken);
        foreach (var entity in fixture.Entities)
        {
            await using var create = connection.CreateCommand();
            create.CommandText = $"CREATE TABLE public.{Quote(entity.TargetTable)} ({string.Join(", ", entity.Fields.Select(field => $"{Quote(field.Target)} text"))})";
            await create.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<ExternalVerificationResult> VerifyExternalAsync(VerificationService service,
        LoadedProjectConfiguration configuration, MigrationGraph graph, SnapshotCaptureResult checkpoint,
        VerificationRuleSet rules, IReadOnlyDictionary<string, List<Dictionary<string, string>>> rows,
        IReadOnlyCollection<JusticeDocument> documents, HashSet<string> missingDocuments, PostgreSqlContainer postgres,
        PostgresShadowTargetConnector target, FilesystemShadowTargetConnector files,
        RuntimeConnectorContextFactory contexts, string workDirectory)
    {
        var runId = new RunId(Guid.NewGuid());
        await LoadExternalTargetAsync(postgres, runId, rows, graph, cancellationToken: TestContext.Current.CancellationToken);
        await LoadExternalFilesAsync(configuration, graph, contexts, files, documents, missingDocuments, runId,
            TestContext.Current.CancellationToken);
        var documentNode = graph.Nodes.Single(node => node.Name == "target-document-payload");
        var documentContext = new ShadowTargetContext(contexts.Create(configuration, documentNode), runId, SystemRole.ShadowTarget);
        var expectedDocuments = documents.Where(document => !missingDocuments.Contains(document.Id))
            .ToDictionary(document => document.Path, StringComparer.Ordinal);
        var observedDocumentCount = 0;
        await foreach (var observed in files.ReadAsync(new ReadRequest(documentContext, documentNode.Selector,
            new ReadOptions()), TestContext.Current.CancellationToken).WithCancellation(TestContext.Current.CancellationToken))
        {
            var path = ((StringValue)observed.Values["relativePath"]).Value;
            Assert.True(expectedDocuments.TryGetValue(path, out var expectedDocument), $"Unexpected external document path {path}.");
            Assert.Equal(expectedDocument!.Sha256, observed.Provenance.SourceHash);
            Assert.Equal(expectedDocument.Sha256, ((BinaryReferenceValue)observed.Values["content"]).Sha256);
            observedDocumentCount++;
        }
        Assert.Equal(expectedDocuments.Count, observedDocumentCount);
        var targetNodes = graph.Nodes.Where(node => node.Type is MigrationNodeType.Target or MigrationNodeType.Archive).ToArray();
        var runtimes = targetNodes.Select(node =>
        {
            var connector = node.Name == "target-document-payload" ? (IShadowTargetConnector)files : target;
            return new VerificationTargetRuntime(node.Name, connector,
                new ShadowTargetContext(contexts.Create(configuration, node), runId, SystemRole.ShadowTarget));
        }).ToArray();
        var observation = new ExternalMigrationObservation(runId, $"justice-independent-{runId.Value:N}",
            configuration.ConfigurationHash, graph.GraphHash, checkpoint.Id, checkpoint.Checkpoint!.ManifestHash!,
            checkpoint.Checkpoint.SourceFingerprint!, targetNodes.Select(node =>
            {
                var connector = node.Name == "target-document-payload" ? (IShadowTargetConnector)files : target;
                return new ExternalTargetEndpoint(node.Name, node.SystemId, node.EndpointId, connector.Id, connector.Version);
            }), DateTimeOffset.UnixEpoch);
        Assert.Equal(checkpoint.Checkpoint.Id, observation.CheckpointId);
        return await service.VerifyExternalTargetAsync(configuration, graph, observation, rules, workDirectory, "0.10F",
            runtimes, TestContext.Current.CancellationToken);
    }

    private static async Task LoadExternalTargetAsync(PostgreSqlContainer postgres, RunId runId,
        IReadOnlyDictionary<string, List<Dictionary<string, string>>> rows, MigrationGraph graph, CancellationToken cancellationToken)
    {
        var schemaName = $"proofshift_shadow_{runId.Value:N}";
        await using var connection = new NpgsqlConnection(postgres.GetConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using (var schema = connection.CreateCommand())
        {
            schema.CommandText = $"CREATE SCHEMA {Quote(schemaName)}";
            await schema.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var pair in rows)
        {
            var node = graph.Nodes.Single(item => item.Name == pair.Key);
            var table = node.Selector.Properties["name"].Split('.')[1];
            var columns = pair.Value.SelectMany(row => row.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            await using (var create = connection.CreateCommand())
            {
                create.CommandText = $"CREATE TABLE {Quote(schemaName)}.{Quote(table)} ({string.Join(", ", columns.Select(column => $"{Quote(column)} text"))})";
                await create.ExecuteNonQueryAsync(cancellationToken);
            }
            var copy = $"COPY {Quote(schemaName)}.{Quote(table)} ({string.Join(", ", columns.Select(Quote))}) FROM STDIN (FORMAT BINARY)";
            await using var importer = await connection.BeginBinaryImportAsync(copy, cancellationToken);
            foreach (var row in pair.Value)
            {
                await importer.StartRowAsync(cancellationToken);
                foreach (var column in columns) await importer.WriteAsync(row[column], NpgsqlDbType.Text, cancellationToken);
            }
            await importer.CompleteAsync(cancellationToken);
        }
    }

    private static async Task LoadExternalFilesAsync(LoadedProjectConfiguration configuration, MigrationGraph graph,
        RuntimeConnectorContextFactory contexts, FilesystemShadowTargetConnector files, IReadOnlyCollection<JusticeDocument> documents,
        HashSet<string> missing, RunId runId, CancellationToken cancellationToken)
    {
        var node = graph.Nodes.Single(item => item.Name == "target-document-payload");
        var context = new ShadowTargetContext(contexts.Create(configuration, node), runId, SystemRole.ShadowTarget);
        await files.PrepareAsync(context, node.Selector, cancellationToken);
        foreach (var document in documents.Where(item => !missing.Contains(item.Id)))
        {
            var binary = new BinaryReferenceValue($"justice-fixture:{document.Path}", document.Content.LongLength, document.Sha256);
            var values = new Dictionary<string, ValueNode>(StringComparer.Ordinal)
            {
                ["relativePath"] = new StringValue(document.Path), ["content"] = binary
            };
            var artifact = new ArtifactReference(new ArtifactId($"justice-file:{document.Id}"), node.SystemId, node.EndpointId,
                "File.Artifact", document.Path);
            var record = new RecordEnvelope(artifact, node.SemanticType, values,
                new ProvenanceMetadata(new ConnectorId("files"), node.EndpointId, document.Path, DateTimeOffset.UnixEpoch, document.Sha256));
            await files.WriteAsync(new ShadowWriteRequest(context, node.Selector, record, node.Name,
                (_, _) => ValueTask.FromResult<Stream>(new MemoryStream(document.Content, writable: false))), cancellationToken);
        }
        await files.CompleteAsync(context, cancellationToken);
    }

    private static async Task WriteSourceFilesAsync(IReadOnlyCollection<JusticeDocument> documents, string root, CancellationToken cancellationToken)
    {
        foreach (var document in documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.Combine(root, document.Path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, document.Content, cancellationToken);
            Assert.Equal(document.Sha256, Sha(await File.ReadAllBytesAsync(path, cancellationToken)));
        }
    }

    private static MigrationNodeId NodeId(string value) => new(StableGuid("node:" + value));
    private static MigrationEdgeId EdgeId(string value) => new(StableGuid("edge:" + value));
    private static Guid StableGuid(string value) => new(SHA256.HashData(Encoding.UTF8.GetBytes(value))[..16]);
    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private sealed record FieldMap(string Target, string Source, IReadOnlyDictionary<string, string>? Codes);
    private sealed record EntitySpec(string SourceNode, string TargetNode, string SourceTable, string TargetTable,
        string SemanticType, string IdentitySource, string IdentityTarget, FieldMap[] Fields);
    private sealed record JusticeDocument(string Id, string CaseId, string Path, byte[] Content, string Sha256);
    private sealed record Fixture(EntitySpec[] Entities, Dictionary<string, List<Dictionary<string, string>>> SourceRows,
        JusticeDocument[] Documents);
    private sealed record JusticeDefect(string Id, string Category, string SemanticConcept, string BusinessKey,
        string ExpectedRuleId, EvidenceResult ExpectedOutcome, int ExpectedFindingCount);
    private sealed class TemporaryDirectoryCleanup(string path) : IDisposable
    {
        public void Dispose() { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
    }
    private sealed class JusticeEnvironment(IReadOnlyDictionary<string, string> values) : IEnvironmentVariableProvider
    {
        public string? GetValue(string name) => values.GetValueOrDefault(name);
    }
}
