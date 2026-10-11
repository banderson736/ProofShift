using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using Npgsql;
using NpgsqlTypes;
using ProofShift.Configuration;
using ProofShift.Connectors.Abstractions;
using ProofShift.Connectors.Postgres;
using ProofShift.Domain;
using ProofShift.Engine;
using ProofShift.Evidence;
using ProofShift.Graph;
using ProofShift.Packs.Abstractions;
using ProofShift.Packs.Hcm;
using ProofShift.Projection;
using ProofShift.Recovery;
using ProofShift.Snapshots;
using ProofShift.Verification;
using Testcontainers.PostgreSql;
using Xunit;

namespace ProofShift.EndToEnd.Tests;

[Collection("DockerIntegration")]
public sealed class HcmPhysicalAssuranceTests
{
    private const int WorkerCount = 5_000;
    private const string SourceSecret = "PS010F_HCM_SOURCE";
    private const string TargetSecret = "PS010F_HCM_TARGET";

    private static readonly HcmDefect[] DefectManifest =
    [
        new("missing-worker", "missing entity", "HCM.Worker", "WORK000001", "target-presence", EvidenceResult.Fail, 7),
        new("duplicate-worker", "duplicate identity", "HCM.Worker", "WORK000002", "worker-uniqueness", EvidenceResult.Fail, 1),
        new("missing-historical-employment", "missing history", "HCM.Employment", "EMP000003H", "target-presence", EvidenceResult.Fail, 3),
        new("employment-overlap", "effective-date overlap plus source-derived boundary mismatch", "HCM.Employment", "EMP000004C", "employment-intervals", EvidenceResult.Fail, 2),
        new("wrong-position-cost-center", "incorrect cost-center mapping", "HCM.Position", "POS006", "position-cost-center-match", EvidenceResult.Fail, 1),
        new("orphan-position-organization", "broken organization reference", "HCM.Position", "POS007", "position-organization", EvidenceResult.Fail, 1),
        new("worker-status-map", "code transformation", "HCM.Worker", "WORK000007", "worker-status-map", EvidenceResult.Fail, 1),
        new("compensation-amount", "exact financial value", "HCM.Compensation", "COMP000001C", "compensation-amount", EvidenceResult.Fail, 1),
        new("compensation-effective-date", "effective-date fidelity", "HCM.Compensation", "COMP000002C", "compensation-effective-from", EvidenceResult.Fail, 1),
        new("payroll-net", "payroll arithmetic", "HCM.PayrollResult", "PAY000003", "payroll-equation", EvidenceResult.Fail, 1),
        new("benefit-owner", "broken worker reference", "HCM.BenefitEnrollment", "BEN000004", "benefit-worker", EvidenceResult.Fail, 1),
        new("benefit-code-map", "code transformation", "HCM.BenefitEnrollment", "BEN000005", "benefit-plan-map", EvidenceResult.Fail, 1),
        new("leave-balance", "exact balance value", "HCM.LeaveBalance", "BAL000006", "leave-balance", EvidenceResult.Fail, 1),
        new("unexpected-worker", "unexpected target", "HCM.Worker", "WORK-UNEXPECTED", "unexpected-target", EvidenceResult.Fail, 3)
    ];

    [Fact]
    public async Task HcmExternalAndShadowScenariosProveExactDefectsAndQualification()
    {
        Assert.Equal(DefectManifest.Length, DefectManifest.Select(defect => defect.Id).Distinct(StringComparer.Ordinal).Count());
        var fixture = CreateFixture(WorkerCount);
        Assert.Equal(40_575, fixture.Entities.Sum(entity => fixture.SourceRows[entity.TargetNode].Count));
        Assert.Equal(1_234_567.8901m, decimal.Parse(fixture.SourceRows["target-compensation"].Single(row =>
            row["COMP_NO"] == "COMP000001C")["AMOUNT"], NumberStyles.Number, CultureInfo.InvariantCulture));
        Assert.Equal(37.6250m, decimal.Parse(fixture.SourceRows["target-compensation"].Single(row =>
            row["COMP_NO"] == "COMP000002C")["AMOUNT"], NumberStyles.Number, CultureInfo.InvariantCulture));
        var root = Path.Combine(Path.GetTempPath(), $"proofshift-hcm-f4-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        using var cleanup = new TemporaryDirectoryCleanup(root);
        await using var sourcePostgres = new PostgreSqlBuilder("postgres:16-alpine").Build();
        try { await sourcePostgres.StartAsync(TestContext.Current.CancellationToken); }
        catch (DockerUnavailableException)
        {
            await sourcePostgres.DisposeAsync();
            throw Xunit.Sdk.SkipException.ForSkip("A Docker-compatible runtime is required for HCM physical assurance.");
        }
        await using var targetPostgres = new PostgreSqlBuilder("postgres:16-alpine").Build();
        try { await targetPostgres.StartAsync(TestContext.Current.CancellationToken); }
        catch (DockerUnavailableException)
        {
            await targetPostgres.DisposeAsync();
            throw Xunit.Sdk.SkipException.ForSkip("A Docker-compatible runtime is required for HCM physical assurance.");
        }
        await SeedSourceAsync(sourcePostgres, fixture, TestContext.Current.CancellationToken);
        await CreateTargetTemplatesAsync(targetPostgres, fixture, TestContext.Current.CancellationToken);

        var configuration = CreateConfiguration(sourcePostgres.GetConnectionString(), targetPostgres.GetConnectionString());
        var graph = CreateGraph(fixture, configuration);
        var sourceConnectors = new ConnectorRegistry([new PostgresSourceConnector()]);
        var targetConnector = new PostgresShadowTargetConnector();
        var targets = new ShadowTargetConnectorRegistry([targetConnector]);
        var contexts = new RuntimeConnectorContextFactory(new HcmEnvironment(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [SourceSecret] = sourcePostgres.GetConnectionString(),
            [TargetSecret] = targetPostgres.GetConnectionString()
        }));
        var checkpointStore = new FileSystemSnapshotStore(Path.Combine(root, "checkpoints"));
        var inspection = await new SourceInspectionService(sourceConnectors, contexts)
            .InspectAsync(configuration, graph, TestContext.Current.CancellationToken);
        Assert.True(inspection.IsValid, string.Join(Environment.NewLine,
            inspection.Sources.SelectMany(source => source.Inspection.Issues).Select(issue => $"{issue.Code}: {issue.Message}")));
        var checkpoint = await new SnapshotCaptureService(sourceConnectors, checkpointStore, contexts)
            .CaptureAsync(configuration, graph, TestContext.Current.CancellationToken);
        Assert.Equal(CheckpointStatus.Complete, checkpoint.Status);
        Assert.NotNull(checkpoint.Checkpoint);

        var registry = new PackRegistry([new HcmPack()]);
        var ruleSet = registry.Resolve(configuration.Root.Packs).Resolve(CreateRules());
        var plan = VerificationExecutionPlan.Create(ruleSet, graph);
        Assert.Equal(ruleSet.Rules.Count, plan.Rules.Count);
        Assert.All(plan.Rules.Where(workset => workset.Rule.Id.Value.StartsWith("hcm-", StringComparison.Ordinal)),
            workset => Assert.Equal(VerificationPartitionExecution.Global, workset.PartitionExecution));
        Assert.Contains(plan.Rules.SelectMany(workset => workset.RequiredTargetFields), field => field == "effective_from");
        Assert.Contains(plan.Rules.SelectMany(workset => workset.RequiredTargetFields), field => field == "gross");

        var service = new VerificationService(checkpointStore);
        var correctedRows = BuildTargetRows(fixture);
        var expectedTargetCount = correctedRows.Values.Sum(rows => (long)rows.Count);
        Assert.Equal(40_575, expectedTargetCount);
        var corrected = await VerifyExternalAsync(service, configuration, graph, checkpoint, fixture, ruleSet, correctedRows,
            targetPostgres, targetConnector, contexts, Path.Combine(root, "external-corrected"));
        Assert.Equal(VerificationOutcome.Passed, corrected.Run.Outcome);
        Assert.DoesNotContain(corrected.Findings, finding => finding.Result == EvidenceResult.Fail);
        Assert.Equal(checkpoint.Checkpoint.ArtifactCount, corrected.Ledger.DispositionCount);
        Assert.Equal(expectedTargetCount, corrected.Ledger.LineageCount);
        Assert.Equal(expectedTargetCount, corrected.Run.TargetArtifactCount);
        Assert.Contains(corrected.Findings, finding => finding.RuleId.Value == "source-accounting" &&
            finding.Result == EvidenceResult.Pass && finding.Expected?.Value is IntegerValue sourceCount &&
            sourceCount.Value == checkpoint.Checkpoint.ArtifactCount);
        Assert.Contains(corrected.Findings, finding => finding.RuleId.Value == "compensation-pay-basis" &&
            finding.Code == "AttributeComparison" && finding.Result == EvidenceResult.Pass);
        Assert.Equal("HOURLY", correctedRows["target-compensation"].Single(row => row["compensation_id"] == "COMP000002C")["pay_basis"]);

        var correctedRepeat = await VerifyExternalAsync(service, configuration, graph, checkpoint, fixture, ruleSet, correctedRows,
            targetPostgres, targetConnector, contexts, Path.Combine(root, "external-corrected-repeat"));
        Assert.NotEqual(corrected.Run.Id, correctedRepeat.Run.Id);
        Assert.Equal(corrected.Run.TargetFingerprint, correctedRepeat.Run.TargetFingerprint);
        Assert.Equal(corrected.Run.EvidenceFingerprint, correctedRepeat.Run.EvidenceFingerprint);
        Assert.Equal(corrected.Ledger.Fingerprint, correctedRepeat.Ledger.Fingerprint);
        Assert.Equal(corrected.Ledger.DispositionCount, correctedRepeat.Ledger.DispositionCount);
        Assert.Equal(corrected.Ledger.LineageCount, correctedRepeat.Ledger.LineageCount);

        var defectiveRows = CloneRows(correctedRows);
        ApplyDefects(defectiveRows);
        var defective = await VerifyExternalAsync(service, configuration, graph, checkpoint, fixture, ruleSet, defectiveRows,
            targetPostgres, targetConnector, contexts, Path.Combine(root, "external-defective"), expectedMissingWorkerReferences: 2);
        Assert.Equal(VerificationOutcome.Failed, defective.Run.Outcome);
        var failures = defective.Findings.Where(finding => finding.Result == EvidenceResult.Fail).ToArray();
        var expectedFindingCount = DefectManifest.Sum(defect => defect.ExpectedFindingCount);
        Assert.True(expectedFindingCount == failures.Length,
            $"Expected {expectedFindingCount} HCM findings, observed {failures.Length}: {string.Join(", ", failures.GroupBy(finding => finding.RuleId.Value + ":" + finding.Code, StringComparer.Ordinal).Select(group => $"{group.Key}={group.Count()}"))}");
        var expectedRuleCounts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["target-presence"] = 2,
            ["worker-uniqueness"] = 1,
            ["employment-intervals"] = 1,
            ["employment-start-fidelity"] = 1,
            ["position-cost-center-match"] = 1,
            ["position-organization"] = 1,
            ["worker-status-map"] = 1,
            ["compensation-amount"] = 1,
            ["compensation-effective-from"] = 1,
            ["payroll-equation"] = 1,
            ["benefit-worker"] = 2,
            ["benefit-plan-map"] = 1,
            ["leave-balance"] = 1,
            ["unexpected-target"] = 1,
            ["employment-worker"] = 2,
            ["compensation-employment"] = 1,
            ["payroll-worker"] = 1,
            ["leave-worker"] = 1,
            ["target-lineage"] = 1,
            ["proofshift.verification.materialized-state"] = 3
        };
        var actualRuleCounts = failures.GroupBy(finding => finding.RuleId.Value, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        Assert.Equal(expectedRuleCounts.OrderBy(pair => pair.Key, StringComparer.Ordinal),
            actualRuleCounts.OrderBy(pair => pair.Key, StringComparer.Ordinal));
        Assert.All(failures, finding =>
        {
            Assert.Equal("1", finding.RuleVersion);
            Assert.NotEmpty(finding.Inputs);
            Assert.False(string.IsNullOrWhiteSpace(finding.Explanation));
        });
        Assert.All(DefectManifest, defect => Assert.Contains(failures,
            finding => finding.RuleId.Value == defect.ExpectedRuleId));
        var preciseCompensationFailure = Assert.Single(failures, finding => finding.RuleId.Value == "compensation-amount");
        Assert.Equal(new DecimalValue(1_234_567.8901m), preciseCompensationFailure.Expected!.Value);
        Assert.Equal(new DecimalValue(1_234_567.8902m), preciseCompensationFailure.Actual!.Value);
        Assert.Equal(expectedTargetCount - 2, defective.Ledger.LineageCount);

        var evidenceStore = new FileSystemEvidenceStore(Path.Combine(root, ".proofshift", "verifications"));
        var recoveryStore = new FileSystemRecoveryArtifactStore(Path.Combine(root, ".proofshift", "recovery"));
        var dryRun = await new DryRunOrchestrator(sourceConnectors, targets, checkpointStore, service,
            new RecoveryService(checkpointStore,
                new RecoveryCompensatorRegistry([new ShadowBaselineRestoreCompensator()])), contexts)
            .RunAsync(configuration, graph, ruleSet, new EffectiveRecoveryPolicy(), Path.Combine(root, "projected-corrected"),
                Path.Combine(root, "temporary", "projected-corrected"), "0.10F", evidenceStore, recoveryStore,
                TestContext.Current.CancellationToken);
        Assert.True(dryRun.Outcome == DryRunExecutionOutcome.Qualified,
            $"HCM shadow dry run was {dryRun.Outcome} ({dryRun.FailureCode}); verification failures: {string.Join(", ", dryRun.Verification?.Findings.Where(finding => finding.Result == EvidenceResult.Fail).Select(finding => finding.RuleId.Value + ":" + finding.Code) ?? [])}; recovery issues: {string.Join(", ", dryRun.Recovery?.Assessment.Issues.Select(issue => issue.Code) ?? [])}");
        Assert.Equal(VerificationOutcome.Passed, dryRun.Verification!.Run.Outcome);
        Assert.Equal(DryRunQualificationStatus.Qualified, dryRun.Recovery!.Qualification.Status);
        Assert.Equal(RecoveryAssessmentOutcome.Passed, dryRun.Recovery.Assessment.Outcome);
        Assert.Equal(RecoveryRehearsalOutcome.Passed, dryRun.Recovery.Rehearsal.Outcome);
        Assert.NotNull(dryRun.VerificationEvidence);
        Assert.NotNull(dryRun.RecoveryArtifacts);
        Assert.DoesNotContain(dryRun.Verification.Findings, finding => finding.Result == EvidenceResult.Fail);

        var report = HcmAssuranceReport.Create(configuration.Root.Project!.Name!, dryRun.Verification!, dryRun.Recovery!);
        Assert.Equal(0, report.VerificationFailureCount);
        Assert.Equal(0, report.UnaccountedSourceCount);
        Assert.Equal(0, report.UnexplainedTargetCount);
        Assert.Equal("QUALIFIED", report.Qualification);
        Assert.Contains("HCM.Worker", report.Concepts);
        Assert.Contains("HCM.Compensation", report.Concepts);
        Assert.Contains("hcm-payroll-reconciliation", report.RuleTypes);
        Assert.Contains("does not validate payroll tax", report.ClaimBoundaryText, StringComparison.Ordinal);
        Assert.DoesNotContain("Pension.Member", JsonSerializer.Serialize(report), StringComparison.Ordinal);
        Assert.DoesNotContain("Justice.Case", JsonSerializer.Serialize(report), StringComparison.Ordinal);
        Assert.DoesNotContain("Utility.Account", JsonSerializer.Serialize(report), StringComparison.Ordinal);
    }

    private static Fixture CreateFixture(int workerCount)
    {
        var costCodeMap = Enumerable.Range(1, 50).ToDictionary(index => $"C{index:D3}", index => $"CC{index:D3}", StringComparer.Ordinal);
        var positionCodeMap = Enumerable.Range(1, 500).ToDictionary(index => $"P{index:D3}", index => $"POS{index:D3}", StringComparer.Ordinal);
        var statusMap = new Dictionary<string, string>(StringComparer.Ordinal) { ["A"] = "ACTIVE", ["T"] = "TERMINATED" };
        var benefitMap = new Dictionary<string, string>(StringComparer.Ordinal) { ["MED"] = "MEDICAL", ["DEN"] = "DENTAL" };
        var entities = new[]
        {
            Spec("source-worker", "target-worker", "legacy.worker", "worker_target", "HCM.Worker", "WORKER_NO", "worker_id",
                [Map("worker_id", "WORKER_NO", "text"), Map("display_name", "DISPLAY_NAME", "text"), Map("status", "WORKER_STATUS_CD", "text", statusMap)]),
            Spec("source-organization", "target-organization", "legacy.organization_unit", "organization_target", "HCM.Organization", "ORG_CODE", "organization_id",
                [Map("organization_id", "ORG_CODE", "text"), Map("name", "ORG_NAME", "text")]),
            Spec("source-cost-center", "target-cost-center", "legacy.cost_center", "cost_center_target", "HCM.CostCenter", "CENTER_CODE", "cost_center_id",
                [Map("cost_center_id", "CENTER_CODE", "text", costCodeMap), Map("organization_id", "ORG_CODE", "text"), Map("cost_center_code", "CENTER_CODE", "text", costCodeMap)]),
            Spec("source-position", "target-position", "legacy.position", "position_target", "HCM.Position", "POSITION_CD", "position_id",
                [Map("position_id", "POSITION_CD", "text", positionCodeMap), Map("organization_id", "ORG_CODE", "text"), Map("cost_center_id", "CENTER_CODE", "text", costCodeMap), Map("title", "POSITION_TITLE", "text")]),
            Spec("source-employment", "target-employment", "legacy.worker_job", "employment_target", "HCM.Employment", "JOB_ROW_ID", "employment_id",
                [Map("employment_id", "JOB_ROW_ID", "text"), Map("worker_id", "WORKER_NO", "text"), Map("position_id", "POSITION_CD", "text", positionCodeMap), Map("effective_from", "START_DATE", "date"), Map("effective_to", "END_DATE", "date", nullable: true), Map("status", "EMP_STATUS_CD", "text", statusMap)]),
            Spec("source-compensation", "target-compensation", "legacy.compensation", "compensation_target", "HCM.Compensation", "COMP_NO", "compensation_id",
                [Map("compensation_id", "COMP_NO", "text"), Map("employment_id", "JOB_ROW_ID", "text"), Map("worker_id", "WORKER_NO", "text"), Map("amount", "AMOUNT", "numeric(18,4)"), Map("currency", "CURRENCY_CD", "text"), Map("pay_basis", "RATE_BASIS_CD", "text", new Dictionary<string, string>(StringComparer.Ordinal) { ["SAL"] = "ANNUAL", ["HR"] = "HOURLY" }), Map("effective_from", "START_DATE", "date"), Map("effective_to", "END_DATE", "date", nullable: true)]),
            Spec("source-payroll", "target-payroll", "legacy.payroll_result", "payroll_target", "HCM.PayrollResult", "PAY_NO", "payroll_id",
                [Map("payroll_id", "PAY_NO", "text"), Map("worker_id", "WORKER_NO", "text"), Map("period", "PAY_PERIOD", "text"), Map("currency", "CURRENCY_CD", "text"), Map("gross", "GROSS_AMT", "numeric(18,2)"), Map("deductions", "DEDUCTION_AMT", "numeric(18,2)"), Map("net", "NET_AMT", "numeric(18,2)")]),
            Spec("source-benefit", "target-benefit", "legacy.benefit_enrollment", "benefit_target", "HCM.BenefitEnrollment", "ENROLL_NO", "enrollment_id",
                [Map("enrollment_id", "ENROLL_NO", "text"), Map("worker_id", "WORKER_NO", "text"), Map("plan_code", "BENEFIT_CD", "text", benefitMap), Map("effective_from", "START_DATE", "date"), Map("effective_to", "END_DATE", "date", nullable: true)]),
            Spec("source-leave", "target-leave", "legacy.leave_balance", "leave_target", "HCM.LeaveBalance", "BALANCE_NO", "balance_id",
                [Map("balance_id", "BALANCE_NO", "text"), Map("worker_id", "WORKER_NO", "text"), Map("leave_type", "LEAVE_CD", "text"), Map("balance", "BALANCE_QTY", "numeric(12,4)")])
        };
        var rows = entities.ToDictionary(entity => entity.TargetNode, _ => new List<Dictionary<string, string>>(workerCount * 2), StringComparer.Ordinal);
        for (var index = 1; index <= 25; index++)
            Add(rows, "target-organization", ("ORG_CODE", $"ORG{index:D3}"), ("ORG_NAME", $"Synthetic Organization {index:D3}"));
        for (var index = 1; index <= 50; index++)
            Add(rows, "target-cost-center", ("CENTER_CODE", $"C{index:D3}"), ("ORG_CODE", $"ORG{(index - 1) % 25 + 1:D3}"));
        for (var index = 1; index <= 500; index++)
            Add(rows, "target-position", ("POSITION_CD", $"P{index:D3}"), ("ORG_CODE", $"ORG{(index - 1) % 25 + 1:D3}"),
                ("CENTER_CODE", $"C{(index - 1) % 50 + 1:D3}"), ("POSITION_TITLE", $"Synthetic Position {index:D3}"));

        for (var index = 1; index <= workerCount; index++)
        {
            var worker = $"WORK{index:D6}";
            var position = $"P{(index - 1) % 500 + 1:D3}";
            var oldEmployment = $"EMP{index:D6}H";
            var currentEmployment = $"EMP{index:D6}C";
            var oldStart = "2020-01-01";
            var oldEnd = "2022-12-31";
            var currentStart = "2023-01-01";
            Add(rows, "target-worker", ("WORKER_NO", worker), ("DISPLAY_NAME", $"Synthetic Worker {index:D6}"), ("WORKER_STATUS_CD", "A"));
            Add(rows, "target-employment", ("JOB_ROW_ID", oldEmployment), ("WORKER_NO", worker), ("POSITION_CD", position),
                ("START_DATE", oldStart), ("END_DATE", oldEnd), ("EMP_STATUS_CD", "T"));
            Add(rows, "target-employment", ("JOB_ROW_ID", currentEmployment), ("WORKER_NO", worker), ("POSITION_CD", position),
                ("START_DATE", currentStart), ("END_DATE", ""), ("EMP_STATUS_CD", "A"));
            var oldAmount = 68_000.2500m + index * 0.1250m;
            var currentAmount = index == 1 ? 1_234_567.8901m : index == 2 ? 37.6250m : 82_000.1875m + index * 0.1250m;
            Add(rows, "target-compensation", ("COMP_NO", $"COMP{index:D6}H"), ("JOB_ROW_ID", oldEmployment), ("WORKER_NO", worker),
                ("AMOUNT", oldAmount.ToString("G29", CultureInfo.InvariantCulture)), ("CURRENCY_CD", "USD"),
                ("RATE_BASIS_CD", "SAL"), ("START_DATE", oldStart), ("END_DATE", oldEnd));
            Add(rows, "target-compensation", ("COMP_NO", $"COMP{index:D6}C"), ("JOB_ROW_ID", currentEmployment), ("WORKER_NO", worker),
                ("AMOUNT", currentAmount.ToString("G29", CultureInfo.InvariantCulture)), ("CURRENCY_CD", "USD"),
                ("RATE_BASIS_CD", index == 2 ? "HR" : "SAL"), ("START_DATE", currentStart), ("END_DATE", ""));
            var gross = index == 1 ? 100_000.01m : 5_000m + index * 13.57m;
            var deductions = index == 1 ? 12_345.67m : decimal.Round(gross * 0.20m, 2, MidpointRounding.AwayFromZero);
            var net = gross - deductions;
            Add(rows, "target-payroll", ("PAY_NO", $"PAY{index:D6}"), ("WORKER_NO", worker), ("PAY_PERIOD", "2025-Q1"),
                ("CURRENCY_CD", "USD"), ("GROSS_AMT", gross.ToString("G29", CultureInfo.InvariantCulture)),
                ("DEDUCTION_AMT", deductions.ToString("G29", CultureInfo.InvariantCulture)), ("NET_AMT", net.ToString("G29", CultureInfo.InvariantCulture)));
            Add(rows, "target-benefit", ("ENROLL_NO", $"BEN{index:D6}"), ("WORKER_NO", worker),
                ("BENEFIT_CD", index % 2 == 0 ? "MED" : "DEN"), ("START_DATE", "2024-01-01"), ("END_DATE", ""));
            Add(rows, "target-leave", ("BALANCE_NO", $"BAL{index:D6}"), ("WORKER_NO", worker), ("LEAVE_CD", "VAC"),
                ("BALANCE_QTY", (15.2500m + index % 100 * 0.1250m).ToString("G29", CultureInfo.InvariantCulture)));
        }
        return new Fixture(entities, rows);
    }

    private static void Add(Dictionary<string, List<Dictionary<string, string>>> rows, string node,
        params (string Key, string Value)[] fields) => rows[node].Add(fields.ToDictionary(field => field.Key,
        field => field.Value, StringComparer.Ordinal));

    private static EntitySpec Spec(string sourceNode, string targetNode, string sourceTable, string targetTable,
        string semanticType, string identitySource, string identityTarget, FieldMap[] fields) =>
        new(sourceNode, targetNode, sourceTable, targetTable, semanticType, identitySource, identityTarget, fields);

    private static FieldMap Map(string target, string source, string type,
        IReadOnlyDictionary<string, string>? codes = null, bool nullable = false) => new(target, source, type, codes, nullable);

    private static Dictionary<string, List<Dictionary<string, string>>> BuildTargetRows(Fixture fixture) =>
        fixture.Entities.ToDictionary(entity => entity.TargetNode, entity => fixture.SourceRows[entity.TargetNode].Select(row =>
            entity.Fields.ToDictionary(field => field.Target,
                field => field.Codes is not null && field.Codes.TryGetValue(row[field.Source], out var mapped) ? mapped : row[field.Source],
                StringComparer.Ordinal)).ToList(), StringComparer.Ordinal);

    private static Dictionary<string, List<Dictionary<string, string>>> CloneRows(
        IReadOnlyDictionary<string, List<Dictionary<string, string>>> rows) => rows.ToDictionary(pair => pair.Key,
        pair => pair.Value.Select(row => new Dictionary<string, string>(row, StringComparer.Ordinal)).ToList(), StringComparer.Ordinal);

    private static void ApplyDefects(Dictionary<string, List<Dictionary<string, string>>> rows)
    {
        rows["target-worker"].RemoveAll(row => row["worker_id"] == "WORK000001");
        rows["target-worker"].Add(new Dictionary<string, string>(rows["target-worker"].Single(row => row["worker_id"] == "WORK000002"), StringComparer.Ordinal));
        rows["target-employment"].RemoveAll(row => row["employment_id"] == "EMP000003H");
        Find(rows, "target-employment", "employment_id", "EMP000004C")["effective_from"] = "2022-12-31";
        Find(rows, "target-position", "position_id", "POS006")["cost_center_id"] = "CC002";
        Find(rows, "target-position", "position_id", "POS007")["organization_id"] = "ORG-GHOST";
        Find(rows, "target-worker", "worker_id", "WORK000007")["status"] = "INVALID";
        Find(rows, "target-compensation", "compensation_id", "COMP000001C")["amount"] = "1234567.8902";
        Find(rows, "target-compensation", "compensation_id", "COMP000002C")["effective_from"] = "2023-01-02";
        Find(rows, "target-payroll", "payroll_id", "PAY000003")["net"] =
            (decimal.Parse(Find(rows, "target-payroll", "payroll_id", "PAY000003")["net"], CultureInfo.InvariantCulture) + 0.01m).ToString("G29", CultureInfo.InvariantCulture);
        Find(rows, "target-benefit", "enrollment_id", "BEN000004")["worker_id"] = "WORK-GHOST";
        Find(rows, "target-benefit", "enrollment_id", "BEN000005")["plan_code"] = "INVALID";
        Find(rows, "target-leave", "balance_id", "BAL000006")["balance"] =
            (decimal.Parse(Find(rows, "target-leave", "balance_id", "BAL000006")["balance"], CultureInfo.InvariantCulture) + 0.1250m).ToString("G29", CultureInfo.InvariantCulture);
        rows["target-worker"].Add(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["worker_id"] = "WORK-UNEXPECTED", ["display_name"] = "Synthetic Unexpected Worker", ["status"] = "ACTIVE"
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
            Rule("worker-uniqueness", "entity-uniqueness", Text("targetNode", "target-worker"), Text("semanticType", "HCM.Worker")),
            AttributeRule("worker-status-map", "target-worker", "HCM.Worker", "status"),
            AttributeRule("position-cost-center-match", "target-position", "HCM.Position", "cost_center_id"),
            AttributeRule("benefit-plan-map", "target-benefit", "HCM.BenefitEnrollment", "plan_code"),
            AttributeRule("benefit-effective-from", "target-benefit", "HCM.BenefitEnrollment", "effective_from"),
            AttributeRule("benefit-effective-to", "target-benefit", "HCM.BenefitEnrollment", "effective_to"),
            AttributeRule("compensation-effective-from", "target-compensation", "HCM.Compensation", "effective_from"),
            AttributeRule("employment-start-fidelity", "target-employment", "HCM.Employment", "effective_from"),
            AttributeRule("employment-end-fidelity", "target-employment", "HCM.Employment", "effective_to"),
            Rule("employment-intervals", "effective-dated-interval", Text("targetNode", "target-employment"),
                Text("semanticType", "HCM.Employment"), Text("identityField", "employment_id"), Text("ownerField", "worker_id"),
                Text("startField", "effective_from"), Text("endField", "effective_to"), Bool("requireContinuous", true)),
            Rule("current-employment", "hcm-current-employment", Text("targetNode", "target-employment"),
                Text("semanticType", "HCM.Employment"), Text("workerField", "worker_id"), Text("startField", "effective_from"),
                Text("endField", "effective_to"), Text("asOfDate", "2025-01-01")),
            ExactValueRule("compensation-amount", "target-compensation", "HCM.Compensation", "compensation_id", "amount"),
            AttributeRule("compensation-pay-basis", "target-compensation", "HCM.Compensation", "pay_basis"),
            ExactValueRule("leave-balance", "target-leave", "HCM.LeaveBalance", "balance_id", "balance"),
            Rule("payroll-equation", "hcm-payroll-reconciliation", Text("targetNode", "target-payroll"),
                Text("semanticType", "HCM.PayrollResult"), Text("identityField", "payroll_id"), Text("workerField", "worker_id"),
                Text("periodField", "period"), Text("currencyField", "currency"), Text("grossField", "gross"),
                Text("deductionsField", "deductions"), Text("netField", "net")),
            ReferenceRule("employment-worker", "target-employment", "HCM.Employment", "worker_id", "target-worker", "HCM.Worker", "worker_id"),
            ReferenceRule("employment-position", "target-employment", "HCM.Employment", "position_id", "target-position", "HCM.Position", "position_id"),
            ReferenceRule("position-organization", "target-position", "HCM.Position", "organization_id", "target-organization", "HCM.Organization", "organization_id"),
            ReferenceRule("position-cost-center", "target-position", "HCM.Position", "cost_center_id", "target-cost-center", "HCM.CostCenter", "cost_center_id"),
            ReferenceRule("cost-center-organization", "target-cost-center", "HCM.CostCenter", "organization_id", "target-organization", "HCM.Organization", "organization_id"),
            ReferenceRule("compensation-employment", "target-compensation", "HCM.Compensation", "employment_id", "target-employment", "HCM.Employment", "employment_id"),
            ReferenceRule("payroll-worker", "target-payroll", "HCM.PayrollResult", "worker_id", "target-worker", "HCM.Worker", "worker_id"),
            ReferenceRule("benefit-worker", "target-benefit", "HCM.BenefitEnrollment", "worker_id", "target-worker", "HCM.Worker", "worker_id"),
            ReferenceRule("leave-worker", "target-leave", "HCM.LeaveBalance", "worker_id", "target-worker", "HCM.Worker", "worker_id")
        };
        return [.. rules];
    }

    private static VerificationRuleDefinition ExactValueRule(string id, string node, string semantic, string identity, string amount) =>
        Rule(id, "hcm-exact-value-fidelity", Text("targetNode", node), Text("semanticType", semantic),
            Text("identityField", identity), Text("amountField", amount));

    private static VerificationRuleDefinition AttributeRule(string id, string node, string semantic, string field) =>
        Rule(id, "attribute-comparison", Text("targetNode", node), Text("semanticType", semantic), Text("attribute", field));

    private static VerificationRuleDefinition ReferenceRule(string id, string node, string semantic, string field,
        string referenceNode, string referenceSemantic, string key) =>
        Rule(id, "hcm-reference-integrity", Text("sourceNode", node), Text("semanticType", semantic), Text("referenceField", field),
            Text("referenceNode", referenceNode), Text("referenceSemanticType", referenceSemantic), Text("referenceKeyField", key));

    private static VerificationRuleDefinition Rule(string id, string type, params KeyValuePair<string, ValueNode>[] options) =>
        new(new RuleId(id), type, "1", EvidenceSeverity.Critical, structuredOptions: options);
    private static KeyValuePair<string, ValueNode> Text(string name, string value) => new(name, new StringValue(value));
    private static KeyValuePair<string, ValueNode> Bool(string name, bool value) => new(name, new BooleanValue(value));

    private static LoadedProjectConfiguration CreateConfiguration(string sourceConnection, string targetConnection)
    {
        var source = new SystemDefinition(new SystemId("hcm-source"), "Synthetic Legacy HCM", SystemRole.Source,
            [new(new StorageEndpointId("hcm-source-db"), new ConnectorId("postgres"), [new("connection", $"secret:{SourceSecret}")])]);
        var target = new SystemDefinition(new SystemId("hcm-target"), "Synthetic HCM Target", SystemRole.ShadowTarget,
            [new(new StorageEndpointId("hcm-target-db"), new ConnectorId("postgres"), [new("connection", $"secret:{TargetSecret}")])]);
        if (string.IsNullOrWhiteSpace(sourceConnection) || string.IsNullOrWhiteSpace(targetConnection))
            throw new ArgumentException("Physical HCM PostgreSQL endpoints are required.");
        var root = new RootConfigurationDto(1, new ProjectConfigurationDto("hcm-f4", "Synthetic ERP HCM Assurance"),
            null, [], null, null, null, packs: [new PackConfigurationDto("proofshift.hcm", new HcmPack().Version)]);
        return new LoadedProjectConfiguration(root, [], [source, target], [], "synthetic-hcm-f4", new string('c', 64));
    }

    private static MigrationGraph CreateGraph(Fixture fixture, LoadedProjectConfiguration configuration)
    {
        var sourceSystem = configuration.Systems.Single(system => system.Id.Value == "hcm-source").Id;
        var targetSystem = configuration.Systems.Single(system => system.Id.Value == "hcm-target").Id;
        var nodes = new List<MigrationNode>();
        var edges = new List<MigrationEdge>();
        foreach (var entity in fixture.Entities)
        {
            nodes.Add(new MigrationNode(NodeId(entity.SourceNode), entity.SourceNode, MigrationNodeType.Source,
                entity.SemanticType, sourceSystem, new StorageEndpointId("hcm-source-db"),
                new ArtifactSelector("table", [new("name", entity.SourceTable)], [entity.IdentitySource])));
            nodes.Add(new MigrationNode(NodeId(entity.TargetNode), entity.TargetNode, MigrationNodeType.Target,
                entity.SemanticType, targetSystem, new StorageEndpointId("hcm-target-db"),
                new ArtifactSelector("table", [new("name", $"public.{entity.TargetTable}")], [entity.IdentityTarget])));
            var fields = entity.Fields.Select(field => new TransformationFieldDefinition(field.Target, field.Source,
                field.Codes is null ? [] : [new TransformationStep(TransformationStepType.CodeMap, "1",
                    field.Codes.Select(pair => new KeyValuePair<string, string>(pair.Key, pair.Value)))]));
            edges.Add(new MigrationEdge(EdgeId(entity.TargetNode), entity.TargetNode,
                [NodeId(entity.SourceNode)], [NodeId(entity.TargetNode)], new MigrationOperation(MigrationOperationType.Transform, fields: fields),
                "1", new RecoveryDefinition(RecoveryMode.Restore, requiresSnapshot: true)));
        }
        var orderedNodes = nodes.OrderBy(node => node.Name, StringComparer.Ordinal).ToArray();
        var orderedEdges = edges.OrderBy(edge => edge.Name, StringComparer.Ordinal).ToArray();
        var names = orderedNodes.ToDictionary(node => node.Id, node => node.Name);
        var canonical = GraphCanonicalizer.Canonicalize(1, orderedNodes, orderedEdges, names);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        return new MigrationGraph(new MigrationGraphId(NodeId("hcm-f4-graph").Value), orderedNodes, orderedEdges,
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
            var table = entity.SourceTable.Split('.')[1];
            var identity = Quote(entity.IdentitySource);
            var sourceFields = entity.Fields.GroupBy(field => field.Source, StringComparer.Ordinal)
                .Select(group => group.First()).OrderBy(field => field.Source, StringComparer.Ordinal).ToArray();
            await using (var create = connection.CreateCommand())
            {
                create.CommandText = $"CREATE TABLE legacy.{Quote(table)} ({string.Join(", ", sourceFields.Select(field => $"{Quote(field.Source)} {field.Type}{(field.Nullable ? string.Empty : " NOT NULL")}"))}, PRIMARY KEY ({identity}))";
                await create.ExecuteNonQueryAsync(cancellationToken);
            }
            var copy = $"COPY legacy.{Quote(table)} ({string.Join(", ", sourceFields.Select(field => Quote(field.Source)))}) FROM STDIN (FORMAT BINARY)";
            await using var importer = await connection.BeginBinaryImportAsync(copy, cancellationToken);
            foreach (var row in fixture.SourceRows[entity.TargetNode])
            {
                await importer.StartRowAsync(cancellationToken);
                foreach (var field in sourceFields)
                    await WriteBinaryValueAsync(importer, field.Type, row[field.Source], field.Nullable, cancellationToken);
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
            create.CommandText = $"CREATE TABLE public.{Quote(entity.TargetTable)} ({string.Join(", ", entity.Fields.Select(field => $"{Quote(field.Target)} {field.Type}{(field.Nullable ? string.Empty : " NOT NULL")}"))})";
            await create.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<ExternalVerificationResult> VerifyExternalAsync(VerificationService service,
        LoadedProjectConfiguration configuration, MigrationGraph graph, SnapshotCaptureResult checkpoint, Fixture fixture,
        VerificationRuleSet rules, IReadOnlyDictionary<string, List<Dictionary<string, string>>> rows,
        PostgreSqlContainer postgres, PostgresShadowTargetConnector target, RuntimeConnectorContextFactory contexts,
        string workingDirectory, int expectedMissingWorkerReferences = 0)
    {
        var runId = new RunId(Guid.NewGuid());
        await LoadExternalTargetAsync(postgres, runId, rows, fixture, graph, cancellationToken: TestContext.Current.CancellationToken);
        var employmentNode = graph.Nodes.Single(node => node.Name == "target-employment");
        var workerNode = graph.Nodes.Single(node => node.Name == "target-worker");
        var externalSchema = $"proofshift_shadow_{runId.Value:N}";
        var employmentTable = employmentNode.Selector.Properties["name"].Split('.')[1];
        var workerTable = workerNode.Selector.Properties["name"].Split('.')[1];
        await using (var connection = new NpgsqlConnection(postgres.GetConnectionString()))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT employment.worker_id FROM {Quote(externalSchema)}.{Quote(employmentTable)} employment LEFT JOIN {Quote(externalSchema)}.{Quote(workerTable)} worker ON worker.worker_id=employment.worker_id WHERE worker.worker_id IS NULL ORDER BY employment.worker_id LIMIT 25";
            var missingWorkerKeys = new List<string>();
            await using (var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken))
                while (await reader.ReadAsync(TestContext.Current.CancellationToken)) missingWorkerKeys.Add(reader.GetString(0));
            Assert.True(expectedMissingWorkerReferences == missingWorkerKeys.Count,
                $"Expected {expectedMissingWorkerReferences} missing worker references; found {missingWorkerKeys.Count}: {string.Join(",", missingWorkerKeys)}");
        }
        var targetNodes = graph.Nodes.Where(node => node.Type == MigrationNodeType.Target).ToArray();
        var runtimes = targetNodes.Select(node => new VerificationTargetRuntime(node.Name, target,
            new ShadowTargetContext(contexts.Create(configuration, node), runId, SystemRole.ShadowTarget))).ToArray();
        var observation = new ExternalMigrationObservation(runId, $"hcm-independent-{runId.Value:N}", configuration.ConfigurationHash,
            graph.GraphHash, checkpoint.Id, checkpoint.Checkpoint!.ManifestHash!, checkpoint.Checkpoint.SourceFingerprint!,
            targetNodes.Select(node => new ExternalTargetEndpoint(node.Name, node.SystemId, node.EndpointId, target.Id, target.Version)),
            DateTimeOffset.UnixEpoch);
        Assert.Equal(checkpoint.Checkpoint.Id, observation.CheckpointId);
        return await service.VerifyExternalTargetAsync(configuration, graph, observation, rules, workingDirectory, "0.10F",
            runtimes, TestContext.Current.CancellationToken);
    }

    private static async Task LoadExternalTargetAsync(PostgreSqlContainer postgres, RunId runId,
        IReadOnlyDictionary<string, List<Dictionary<string, string>>> rows, Fixture fixture, MigrationGraph graph,
        CancellationToken cancellationToken)
    {
        var schemaName = $"proofshift_shadow_{runId.Value:N}";
        await using var connection = new NpgsqlConnection(postgres.GetConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using (var schema = connection.CreateCommand())
        {
            schema.CommandText = $"CREATE SCHEMA {Quote(schemaName)}";
            await schema.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var entity in graph.Nodes.Where(node => node.Type == MigrationNodeType.Target))
        {
            var spec = rows[entity.Name];
            var tableName = entity.Selector.Properties["name"].Split('.')[1];
            var schemaSpec = fixture.Entities.Single(item => item.TargetNode == entity.Name);
            await using (var create = connection.CreateCommand())
            {
                create.CommandText = $"CREATE TABLE {Quote(schemaName)}.{Quote(tableName)} ({string.Join(", ", schemaSpec.Fields.Select(field => $"{Quote(field.Target)} {field.Type}{(field.Nullable ? string.Empty : " NOT NULL")}"))})";
                await create.ExecuteNonQueryAsync(cancellationToken);
            }
            var copy = $"COPY {Quote(schemaName)}.{Quote(tableName)} ({string.Join(", ", schemaSpec.Fields.Select(field => Quote(field.Target)))}) FROM STDIN (FORMAT BINARY)";
            await using var importer = await connection.BeginBinaryImportAsync(copy, cancellationToken);
            foreach (var row in spec)
            {
                await importer.StartRowAsync(cancellationToken);
                foreach (var field in schemaSpec.Fields)
                    await WriteBinaryValueAsync(importer, field.Type, row[field.Target], field.Nullable, cancellationToken);
            }
            await importer.CompleteAsync(cancellationToken);
        }
    }

    private static async Task WriteBinaryValueAsync(NpgsqlBinaryImporter importer, string type, string value,
        bool nullable, CancellationToken cancellationToken)
    {
        if (value.Length == 0 && nullable)
        {
            await importer.WriteNullAsync(cancellationToken);
            return;
        }
        switch (type)
        {
            case "date":
                await importer.WriteAsync(DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture), NpgsqlDbType.Date, cancellationToken);
                break;
            case "numeric(18,2)":
            case "numeric(18,4)":
            case "numeric(12,4)":
                await importer.WriteAsync(decimal.Parse(value, NumberStyles.Number, CultureInfo.InvariantCulture), NpgsqlDbType.Numeric, cancellationToken);
                break;
            default:
                await importer.WriteAsync(value, NpgsqlDbType.Text, cancellationToken);
                break;
        }
    }

    private static MigrationNodeId NodeId(string value) => new(StableGuid("node:" + value));
    private static MigrationEdgeId EdgeId(string value) => new(StableGuid("edge:" + value));
    private static Guid StableGuid(string value) => new(SHA256.HashData(Encoding.UTF8.GetBytes(value))[..16]);
    private static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private sealed record FieldMap(string Target, string Source, string Type,
        IReadOnlyDictionary<string, string>? Codes = null, bool Nullable = false);
    private sealed record EntitySpec(string SourceNode, string TargetNode, string SourceTable, string TargetTable,
        string SemanticType, string IdentitySource, string IdentityTarget, FieldMap[] Fields);
    private sealed record Fixture(EntitySpec[] Entities, Dictionary<string, List<Dictionary<string, string>>> SourceRows);
    private sealed record HcmDefect(string Id, string Category, string SemanticConcept, string BusinessKey,
        string ExpectedRuleId, EvidenceResult ExpectedOutcome, int ExpectedFindingCount);
    private sealed class TemporaryDirectoryCleanup(string path) : IDisposable
    {
        public void Dispose() { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
    }
    private sealed class HcmEnvironment(IReadOnlyDictionary<string, string> values) : IEnvironmentVariableProvider
    {
        public string? GetValue(string name) => values.GetValueOrDefault(name);
    }
}
