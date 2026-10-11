using System.Globalization;
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
using ProofShift.Connectors.StructuredFiles;
using ProofShift.Domain;
using ProofShift.Engine;
using ProofShift.Evidence;
using ProofShift.Graph;
using ProofShift.Packs.Abstractions;
using ProofShift.Packs.Fhir;
using ProofShift.Projection;
using ProofShift.Recovery;
using ProofShift.Snapshots;
using ProofShift.Verification;
using Testcontainers.PostgreSql;
using Xunit;

namespace ProofShift.EndToEnd.Tests;

[Collection("DockerIntegration")]
public sealed class FhirPhysicalAssuranceTests
{
    private const int PatientCount = 1_000;
    private const string SourceRecordsSecret = "PS010F_FHIR_SOURCE_RECORDS";
    private const string SourceFilesSecret = "PS010F_FHIR_SOURCE_FILES";
    private const string TargetDatabaseSecret = "PS010F_FHIR_TARGET_DATABASE";
    private const string TargetFilesSecret = "PS010F_FHIR_TARGET_FILES";

    private static readonly FhirDefect[] DefectManifest =
    [
        new("missing-observation", "missing target resource", "Fhir.Observation", "OBS000001", "target-presence", 3),
        new("quantity-value", "exact decimal quantity", "Fhir.Observation", "OBS000002", "observation-quantity"),
        new("valid-wrong-patient", "incorrect but resolvable reference", "Fhir.Observation", "OBS000003", "observation-patient-match"),
        new("coding-map", "code/system transformation", "Fhir.Observation", "OBS000004", "observation-code"),
        new("same-instant-different-offset", "offset fidelity", "Fhir.Observation", "OBS000005", "observation-instant"),
        new("wrong-resource-type", "typed reference", "Fhir.Encounter", "ENC000006", "encounter-patient"),
        new("missing-consent", "missing resource", "Fhir.Consent", "CON000007", "target-presence", 2),
        new("missing-binary", "missing document payload", "Fhir.Binary", "DOC000008.txt", "document-binary", 4),
        new("unexpected-condition", "unexpected target resource", "Fhir.Condition", "COND-UNEXPECTED", "unexpected-target")
    ];

    [Fact]
    public async Task FhirPhysicalExternalAndShadowScenariosProveExactDefectsAndQualification()
    {
        Assert.Equal(DefectManifest.Length, DefectManifest.Select(defect => defect.Id).Distinct(StringComparer.Ordinal).Count());
        var fixture = CreateFixture(PatientCount);
        Assert.Equal(6L * PatientCount, fixture.Entities.Sum(entity => (long)fixture.SourceRows[entity.TargetNode].Count));
        Assert.Equal(7L * PatientCount, fixture.Entities.Sum(entity => (long)fixture.SourceRows[entity.TargetNode].Count) + fixture.Documents.Count);
        Assert.Equal(0.25m, decimal.Parse(fixture.SourceRows["target-observation"][0]["quantityValue"], CultureInfo.InvariantCulture));

        var root = Path.Combine(Path.GetTempPath(), $"proofshift-fhir-f5-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        using var cleanup = new TemporaryDirectoryCleanup(root);
        var sourceRecordsRoot = Path.Combine(root, "source-records");
        var sourceFilesRoot = Path.Combine(root, "source-files");
        var targetFilesRoot = Path.Combine(root, "target-files");
        Directory.CreateDirectory(sourceRecordsRoot);
        Directory.CreateDirectory(sourceFilesRoot);
        Directory.CreateDirectory(targetFilesRoot);
        await WriteSourceRecordsAsync(fixture, sourceRecordsRoot, TestContext.Current.CancellationToken);
        await WriteDocumentFilesAsync(fixture.Documents, sourceFilesRoot, TestContext.Current.CancellationToken);
        await using var targetPostgres = new PostgreSqlBuilder("postgres:16-alpine").Build();
        try { await targetPostgres.StartAsync(TestContext.Current.CancellationToken); }
        catch (DockerUnavailableException)
        {
            await targetPostgres.DisposeAsync();
            throw Xunit.Sdk.SkipException.ForSkip("A Docker-compatible runtime is required for FHIR physical assurance.");
        }
        var configuration = CreateConfiguration(targetPostgres.GetConnectionString(), sourceRecordsRoot, sourceFilesRoot, targetFilesRoot);
        var graph = CreateGraph(fixture, configuration);
        var sourceConnectors = new ConnectorRegistry([new StructuredFileConnector("ndjson"), new FilesystemSourceConnector()]);
        var postgresTarget = new PostgresShadowTargetConnector();
        var filesystemTarget = new FilesystemShadowTargetConnector();
        var targets = new ShadowTargetConnectorRegistry([postgresTarget, filesystemTarget]);
        var contexts = new RuntimeConnectorContextFactory(new FhirEnvironment(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [SourceRecordsSecret] = sourceRecordsRoot,
            [SourceFilesSecret] = sourceFilesRoot,
            [TargetDatabaseSecret] = targetPostgres.GetConnectionString(),
            [TargetFilesSecret] = targetFilesRoot
        }));
        await CreateTargetTemplatesAsync(targetPostgres, fixture, TestContext.Current.CancellationToken);
        var checkpointStore = new FileSystemSnapshotStore(Path.Combine(root, "checkpoints"));
        var inspection = await new SourceInspectionService(sourceConnectors, contexts)
            .InspectAsync(configuration, graph, TestContext.Current.CancellationToken);
        Assert.True(inspection.IsValid, string.Join(Environment.NewLine,
            inspection.Sources.SelectMany(source => source.Inspection.Issues).Select(issue => $"{issue.Code}: {issue.Message}")));
        var checkpoint = await new SnapshotCaptureService(sourceConnectors, checkpointStore, contexts)
            .CaptureAsync(configuration, graph, TestContext.Current.CancellationToken);
        Assert.Equal(CheckpointStatus.Complete, checkpoint.Status);
        Assert.NotNull(checkpoint.Checkpoint);
        Assert.Equal(7L * PatientCount, checkpoint.Checkpoint.ArtifactCount);

        var registry = new PackRegistry([new FhirPack()]);
        var rules = registry.Resolve(configuration.Root.Packs).Resolve(CreateRules());
        var plan = VerificationExecutionPlan.Create(rules, graph);
        Assert.Equal(rules.Rules.Count, plan.Rules.Count);
        Assert.All(plan.Rules.Where(workset => workset.Rule.Id.Value.StartsWith("fhir-", StringComparison.Ordinal)),
            workset => Assert.Equal(VerificationPartitionExecution.Global, workset.PartitionExecution));
        Assert.Contains(plan.Rules.SelectMany(workset => workset.RequiredTargetFields), field => field == "quantity_value");
        Assert.Contains(rules.Rules, rule => rule.Id.Value == "observation-quantity");

        var service = new VerificationService(checkpointStore);
        var correctedRows = BuildTargetRows(fixture);
        var expectedTargetCount = correctedRows.Values.Sum(rows => (long)rows.Count) + fixture.Documents.Count;
        Assert.Equal(7L * PatientCount, expectedTargetCount);
        var corrected = await VerifyExternalAsync(service, configuration, graph, checkpoint, rules, correctedRows,
            fixture, new HashSet<string>(StringComparer.Ordinal), targetPostgres, postgresTarget, filesystemTarget, contexts, targetFilesRoot,
            Path.Combine(root, "external-corrected"));
        Assert.True(corrected.Run.Outcome == VerificationOutcome.Passed,
            $"Corrected external FHIR target failed: {string.Join(", ", corrected.Findings.Where(finding => finding.Result == EvidenceResult.Fail).Select(finding => finding.RuleId.Value + ":" + finding.Code).Take(50))}");
        Assert.DoesNotContain(corrected.Findings, finding => finding.Result == EvidenceResult.Fail);
        Assert.Equal(checkpoint.Checkpoint.ArtifactCount, corrected.Ledger.DispositionCount);
        Assert.Equal(expectedTargetCount, corrected.Ledger.LineageCount);
        Assert.Equal(expectedTargetCount, corrected.Run.TargetArtifactCount);
        Assert.Contains(corrected.Findings, finding => finding.Code == "ExternalTargetObserved" &&
            finding.Expected?.Value is StringValue observed && observed.Value == "externally-populated-target");

        var repeat = await VerifyExternalAsync(service, configuration, graph, checkpoint, rules, correctedRows,
            fixture, new HashSet<string>(StringComparer.Ordinal), targetPostgres, postgresTarget, filesystemTarget, contexts, targetFilesRoot,
            Path.Combine(root, "external-corrected-repeat"));
        Assert.NotEqual(corrected.Run.Id, repeat.Run.Id);
        Assert.Equal(corrected.Run.TargetFingerprint, repeat.Run.TargetFingerprint);
        Assert.Equal(corrected.Run.EvidenceFingerprint, repeat.Run.EvidenceFingerprint);
        Assert.Equal(corrected.Ledger.Fingerprint, repeat.Ledger.Fingerprint);

        var defectiveRows = CloneRows(correctedRows);
        ApplyDefects(defectiveRows);
        var missingDocument = "DOC000008.txt";
        var defective = await VerifyExternalAsync(service, configuration, graph, checkpoint, rules, defectiveRows,
            fixture, new HashSet<string>([missingDocument], StringComparer.Ordinal), targetPostgres, postgresTarget, filesystemTarget, contexts, targetFilesRoot,
            Path.Combine(root, "external-defective"));
        Assert.Equal(VerificationOutcome.Failed, defective.Run.Outcome);
        var failures = defective.Findings.Where(finding => finding.Result == EvidenceResult.Fail).ToArray();
        Assert.All(failures, finding =>
        {
            Assert.Equal("1", finding.RuleVersion);
            Assert.NotEmpty(finding.Inputs);
            Assert.False(string.IsNullOrWhiteSpace(finding.Explanation));
        });
        var expectedFailures = DefectManifest.Sum(defect => defect.ExpectedFindingCount);
        Assert.True(expectedFailures == failures.Length,
            $"Expected {expectedFailures} FHIR findings, observed {failures.Length}: {string.Join(", ", failures.GroupBy(finding => finding.RuleId.Value + ":" + finding.Code, StringComparer.Ordinal).Select(group => $"{group.Key}={group.Count()}"))}");
        var expectedRuleCounts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["document-binary"] = 1,
            ["encounter-patient"] = 1,
            ["observation-code"] = 1,
            ["observation-instant"] = 1,
            ["observation-patient-match"] = 1,
            ["observation-quantity"] = 1,
            ["target-lineage"] = 1,
            ["target-presence"] = 3,
            ["unexpected-target"] = 1,
            ["proofshift.verification.materialized-state"] = 4
        };
        var actualRuleCounts = failures.GroupBy(finding => finding.RuleId.Value, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        Assert.Equal(expectedRuleCounts.OrderBy(pair => pair.Key, StringComparer.Ordinal),
            actualRuleCounts.OrderBy(pair => pair.Key, StringComparer.Ordinal));
        Assert.All(DefectManifest, defect => Assert.Contains(failures, finding => finding.RuleId.Value == defect.ExpectedRuleId));
        Assert.Equal(expectedTargetCount - 3, defective.Ledger.LineageCount);
        Assert.Equal(3, defective.Findings.Count(finding => finding.Code == "UnaccountedArtifact"));
        Assert.Equal(2, defective.Findings.Count(finding => finding.Code == "MissingLineage"));

        await WriteDocumentFilesAsync(fixture.Documents, targetFilesRoot, TestContext.Current.CancellationToken);
        var evidenceStore = new FileSystemEvidenceStore(Path.Combine(root, ".proofshift", "verifications"));
        var recoveryStore = new FileSystemRecoveryArtifactStore(Path.Combine(root, ".proofshift", "recovery"));
        var dryRun = await new DryRunOrchestrator(sourceConnectors, targets, checkpointStore, service,
            new RecoveryService(checkpointStore,
                new RecoveryCompensatorRegistry([new ShadowBaselineRestoreCompensator()])), contexts)
            .RunAsync(configuration, graph, rules, new EffectiveRecoveryPolicy(), Path.Combine(root, "projected-corrected"),
                Path.Combine(root, "temporary", "projected-corrected"), "0.10F", evidenceStore, recoveryStore,
                TestContext.Current.CancellationToken);
        Assert.True(dryRun.Outcome == DryRunExecutionOutcome.Qualified,
            $"FHIR shadow dry run was {dryRun.Outcome} ({dryRun.FailureCode}); verification failures: {string.Join(", ", dryRun.Verification?.Findings.Where(finding => finding.Result == EvidenceResult.Fail).Select(finding => finding.RuleId.Value + ":" + finding.Code) ?? [])}; recovery issues: {string.Join(", ", dryRun.Recovery?.Assessment.Issues.Select(issue => issue.Code) ?? [])}");
        Assert.Equal(VerificationOutcome.Passed, dryRun.Verification!.Run.Outcome);
        Assert.Equal(DryRunQualificationStatus.Qualified, dryRun.Recovery!.Qualification.Status);
        Assert.Equal(RecoveryAssessmentOutcome.Passed, dryRun.Recovery.Assessment.Outcome);
        Assert.Equal(RecoveryRehearsalOutcome.Passed, dryRun.Recovery.Rehearsal.Outcome);
        Assert.NotNull(dryRun.VerificationEvidence);
        Assert.NotNull(dryRun.RecoveryArtifacts);
        Assert.DoesNotContain(dryRun.Verification.Findings, finding => finding.Result == EvidenceResult.Fail);

        var report = FhirAssuranceReport.Create(configuration.Root.Project!.Name!, dryRun.Verification!, dryRun.Recovery!);
        Assert.Equal(0, report.VerificationFailureCount);
        Assert.Equal(0, report.UnaccountedSourceCount);
        Assert.Equal(0, report.UnexplainedTargetCount);
        Assert.Equal("QUALIFIED", report.Qualification);
        Assert.Equal("FHIR-Style Healthcare Assurance", report.Title);
        Assert.Equal("Synthetic selected-field migration assurance", report.Scope);
        Assert.Contains("Fhir.Patient", report.Concepts);
        Assert.Contains("Fhir.Binary", report.Concepts);
        Assert.Contains("fhir-quantity-fidelity", report.RuleTypes);
        Assert.Contains("Not FHIR/profile/US Core certification", report.ClaimBoundaryText, StringComparison.Ordinal);
        Assert.DoesNotContain("Pension.Member", JsonSerializer.Serialize(report), StringComparison.Ordinal);
        Assert.DoesNotContain("HCM.Worker", JsonSerializer.Serialize(report), StringComparison.Ordinal);
    }

    private static FhirFixture CreateFixture(int count)
    {
        var observationCoding = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["urn:oid:2.16.840.1.113883.6.1"] = "http://loinc.org",
            ["GLU"] = "2345-7"
        };
        var fields = new[]
        {
            Spec("source-patient", "target-patient", "patient", "patient", "Fhir.Patient", [Map("id", "id", "text"), Map("active", "active", "boolean"), Map("status", "status", "text")]),
            Spec("source-encounter", "target-encounter", "encounter", "encounter", "Fhir.Encounter", [Map("id", "id", "text"), Map("patient_reference", "patientReference", "text"), Map("status", "status", "text"), Map("period_start", "periodStart", "text")]),
            Spec("source-observation", "target-observation", "observation", "observation", "Fhir.Observation", [Map("id", "id", "text"), Map("patient_reference", "patientReference", "text"), Map("encounter_reference", "encounterReference", "text"), Map("coding_system", "codingSystem", "text", observationCoding), Map("coding_code", "codingCode", "text", observationCoding), Map("quantity_value", "quantityValue", "numeric(18,6)"), Map("quantity_unit", "quantityUnit", "text"), Map("quantity_system", "quantitySystem", "text"), Map("quantity_code", "quantityCode", "text"), Map("effective_at", "effectiveAt", "text")]),
            Spec("source-condition", "target-condition", "condition", "condition", "Fhir.Condition", [Map("id", "id", "text"), Map("patient_reference", "patientReference", "text"), Map("encounter_reference", "encounterReference", "text"), Map("coding_system", "codingSystem", "text", observationCoding), Map("coding_code", "codingCode", "text", observationCoding), Map("onset_at", "onsetAt", "text")]),
            Spec("source-consent", "target-consent", "consent", "consent", "Fhir.Consent", [Map("id", "id", "text"), Map("patient_reference", "patientReference", "text"), Map("status", "status", "text"), Map("scope_code", "scopeCode", "text"), Map("effective_start", "effectiveStart", "text"), Map("effective_end", "effectiveEnd", "text")]),
            Spec("source-document-reference", "target-document-reference", "document-reference", "document_reference", "Fhir.DocumentReference", [Map("id", "id", "text"), Map("patient_reference", "patientReference", "text"), Map("status", "status", "text"), Map("binary_reference", "binaryReference", "text"), Map("sha256", "sha256", "text")])
        };
        var rows = fields.ToDictionary(entity => entity.TargetNode, _ => new List<Dictionary<string, string>>(), StringComparer.Ordinal);
        var documents = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        for (var index = 1; index <= count; index++)
        {
            var patient = $"PAT{index:D6}";
            var encounter = $"ENC{index:D6}";
            var observation = $"OBS{index:D6}";
            var condition = $"COND{index:D6}";
            var consent = $"CON{index:D6}";
            var document = $"DOC{index:D6}";
            Add(rows, "target-patient", ("id", patient), ("active", "true"), ("status", "active"));
            Add(rows, "target-encounter", ("id", encounter), ("patientReference", $"Patient/{patient}"), ("status", "finished"), ("periodStart", "2024-01-01T09:00:00-05:00"));
            Add(rows, "target-observation", ("id", observation), ("patientReference", $"Patient/{patient}"), ("encounterReference", $"Encounter/{encounter}"),
                ("codingSystem", "urn:oid:2.16.840.1.113883.6.1"), ("codingCode", "GLU"),
                ("quantityValue", (0.125m + (index % 100) * 0.125m).ToString("G29", CultureInfo.InvariantCulture)),
                ("quantityUnit", "milligram per deciliter"), ("quantitySystem", "http://unitsofmeasure.org"), ("quantityCode", "mg/dL"),
                ("effectiveAt", index == 5 ? "2024-11-03T01:30:00-04:00" : "2024-03-10T01:30:00-05:00"));
            Add(rows, "target-condition", ("id", condition), ("patientReference", $"Patient/{patient}"), ("encounterReference", $"Encounter/{encounter}"),
                ("codingSystem", "urn:oid:2.16.840.1.113883.6.1"), ("codingCode", "GLU"), ("onsetAt", "2024-02-15T12:00:00+00:00"));
            Add(rows, "target-consent", ("id", consent), ("patientReference", $"Patient/{patient}"), ("status", "active"),
                ("scopeCode", "treatment"), ("effectiveStart", "2024-01-01"), ("effectiveEnd", ""));
            var path = $"{document}.txt";
            var content = Encoding.UTF8.GetBytes($"Synthetic FHIR Binary payload {document}\n");
            documents.Add(path, content);
            Add(rows, "target-document-reference", ("id", document), ("patientReference", $"Patient/{patient}"), ("status", "current"),
                ("binaryReference", $"Binary/{path}"), ("sha256", Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant()));
        }
        return new FhirFixture(fields, rows, documents);
    }

    private static void Add(Dictionary<string, List<Dictionary<string, string>>> rows, string node,
        params (string Key, string Value)[] fields) => rows[node].Add(fields.ToDictionary(field => field.Key, field => field.Value, StringComparer.Ordinal));

    private static EntitySpec Spec(string sourceNode, string targetNode, string sourceFile, string targetTable,
        string semanticType, FieldMap[] fields) => new(sourceNode, targetNode, sourceFile, targetTable, semanticType, fields);

    private static FieldMap Map(string target, string source, string type,
        IReadOnlyDictionary<string, string>? codes = null) => new(target, source, type, codes);

    private static Dictionary<string, List<Dictionary<string, string>>> BuildTargetRows(FhirFixture fixture) =>
        fixture.Entities.ToDictionary(entity => entity.TargetNode, entity => fixture.SourceRows[entity.TargetNode].Select(row =>
            entity.Fields.ToDictionary(field => field.Target,
                field => field.Codes is not null && field.Codes.TryGetValue(row[field.Source], out var mapped) ? mapped : row[field.Source],
                StringComparer.Ordinal)).ToList(), StringComparer.Ordinal);

    private static Dictionary<string, List<Dictionary<string, string>>> CloneRows(
        IReadOnlyDictionary<string, List<Dictionary<string, string>>> rows) => rows.ToDictionary(pair => pair.Key,
        pair => pair.Value.Select(row => new Dictionary<string, string>(row, StringComparer.Ordinal)).ToList(), StringComparer.Ordinal);

    private static void ApplyDefects(Dictionary<string, List<Dictionary<string, string>>> rows)
    {
        rows["target-observation"].RemoveAll(row => row["id"] == "OBS000001");
        Find(rows, "target-observation", "id", "OBS000002")["quantity_value"] = "0.376";
        Find(rows, "target-observation", "id", "OBS000003")["patient_reference"] = "Patient/PAT000004";
        Find(rows, "target-observation", "id", "OBS000004")["coding_code"] = "9999-9";
        Find(rows, "target-observation", "id", "OBS000005")["effective_at"] = "2024-11-03T00:30:00-05:00";
        Find(rows, "target-encounter", "id", "ENC000006")["patient_reference"] = "Observation/OBS000006";
        rows["target-consent"].RemoveAll(row => row["id"] == "CON000007");
        rows["target-condition"].Add(new Dictionary<string, string>(rows["target-condition"][0], StringComparer.Ordinal)
        {
            ["id"] = "COND-UNEXPECTED"
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
            Rule("observation-uniqueness", "entity-uniqueness", Text("targetNode", "target-observation"), Text("semanticType", "Fhir.Observation")),
            Rule("observation-quantity", "fhir-quantity-fidelity", Text("targetNode", "target-observation"), Text("semanticType", "Fhir.Observation"),
                Text("identityField", "id"), Text("valueField", "quantity_value"), Text("unitField", "quantity_unit"),
                Text("unitSystemField", "quantity_system"), Text("unitCodeField", "quantity_code")),
            Rule("observation-instant", "fhir-instant-fidelity", Text("targetNode", "target-observation"), Text("semanticType", "Fhir.Observation"),
                Text("identityField", "id"), Text("instantField", "effective_at")),
            AttributeRule("observation-patient-match", "target-observation", "Fhir.Observation", "patient_reference"),
            AttributeRule("observation-coding", "target-observation", "Fhir.Observation", "coding_system"),
            AttributeRule("observation-code", "target-observation", "Fhir.Observation", "coding_code"),
            AttributeRule("condition-coding", "target-condition", "Fhir.Condition", "coding_system"),
            AttributeRule("condition-code", "target-condition", "Fhir.Condition", "coding_code"),
            ReferenceRule("encounter-patient", "target-encounter", "Fhir.Encounter", "patient_reference", "Patient", "target-patient", "Fhir.Patient", "id"),
            ReferenceRule("observation-patient", "target-observation", "Fhir.Observation", "patient_reference", "Patient", "target-patient", "Fhir.Patient", "id"),
            ReferenceRule("observation-encounter", "target-observation", "Fhir.Observation", "encounter_reference", "Encounter", "target-encounter", "Fhir.Encounter", "id"),
            ReferenceRule("condition-patient", "target-condition", "Fhir.Condition", "patient_reference", "Patient", "target-patient", "Fhir.Patient", "id"),
            ReferenceRule("condition-encounter", "target-condition", "Fhir.Condition", "encounter_reference", "Encounter", "target-encounter", "Fhir.Encounter", "id"),
            ReferenceRule("consent-patient", "target-consent", "Fhir.Consent", "patient_reference", "Patient", "target-patient", "Fhir.Patient", "id"),
            ReferenceRule("document-patient", "target-document-reference", "Fhir.DocumentReference", "patient_reference", "Patient", "target-patient", "Fhir.Patient", "id"),
            ReferenceRule("document-binary", "target-document-reference", "Fhir.DocumentReference", "binary_reference", "Binary", "target-binary", "Fhir.Binary", "relativePath")
        };
        return [.. rules];
    }

    private static VerificationRuleDefinition AttributeRule(string id, string node, string semantic, string field) =>
        Rule(id, "attribute-comparison", Text("targetNode", node), Text("semanticType", semantic), Text("attribute", field));

    private static VerificationRuleDefinition ReferenceRule(string id, string node, string semantic, string field,
        string resourceType, string referenceNode, string referenceSemantic, string key) =>
        Rule(id, "fhir-typed-reference-integrity", Text("sourceNode", node), Text("semanticType", semantic), Text("referenceField", field),
            Text("expectedResourceType", resourceType), Text("referenceNode", referenceNode),
            Text("referenceSemanticType", referenceSemantic), Text("referenceKeyField", key));

    private static VerificationRuleDefinition Rule(string id, string type, params KeyValuePair<string, ValueNode>[] options) =>
        new(new RuleId(id), type, "1", EvidenceSeverity.Critical, structuredOptions: options);

    private static KeyValuePair<string, ValueNode> Text(string name, string value) => new(name, new StringValue(value));

    private static LoadedProjectConfiguration CreateConfiguration(string targetConnection, string sourceRecordsRoot,
        string sourceFilesRoot, string targetFilesRoot)
    {
        var source = new SystemDefinition(new SystemId("fhir-source"), "Synthetic FHIR Export", SystemRole.Source,
        [
            new(new StorageEndpointId("fhir-source-records"), new ConnectorId("ndjson"), [new("root", $"secret:{SourceRecordsSecret}")]),
            new(new StorageEndpointId("fhir-source-files"), new ConnectorId("files"), [new("root", $"secret:{SourceFilesSecret}")])
        ]);
        var target = new SystemDefinition(new SystemId("fhir-target"), "Synthetic Healthcare Target", SystemRole.ShadowTarget,
        [
            new(new StorageEndpointId("fhir-target-db"), new ConnectorId("postgres"), [new("connection", $"secret:{TargetDatabaseSecret}")]),
            new(new StorageEndpointId("fhir-target-files"), new ConnectorId("files"), [new("root", $"secret:{TargetFilesSecret}")])
        ]);
        if (string.IsNullOrWhiteSpace(targetConnection) || string.IsNullOrWhiteSpace(sourceRecordsRoot) ||
            string.IsNullOrWhiteSpace(sourceFilesRoot) || string.IsNullOrWhiteSpace(targetFilesRoot))
            throw new ArgumentException("FHIR physical endpoints are required.");
        var root = new RootConfigurationDto(1, new ProjectConfigurationDto("fhir-f5", "Synthetic Healthcare Migration Assurance"),
            null, [], null, null, null, packs: [new PackConfigurationDto("proofshift.fhir", new FhirPack().Version)]);
        return new LoadedProjectConfiguration(root, [], [source, target], [], "synthetic-fhir-f5", new string('e', 64));
    }

    private static MigrationGraph CreateGraph(FhirFixture fixture, LoadedProjectConfiguration configuration)
    {
        var sourceSystem = configuration.Systems.Single(system => system.Id.Value == "fhir-source").Id;
        var targetSystem = configuration.Systems.Single(system => system.Id.Value == "fhir-target").Id;
        var nodes = new List<MigrationNode>();
        var edges = new List<MigrationEdge>();
        foreach (var entity in fixture.Entities)
        {
            var sourceFields = entity.Fields.ToDictionary(field => field.Source, StringComparer.Ordinal);
            var selectorProperties = new List<KeyValuePair<string, string>> { new("path", $"{entity.SourceFile}.ndjson") };
            foreach (var field in entity.Fields)
            {
                selectorProperties.Add(new($"fields.{field.Source}.path", field.Source));
                selectorProperties.Add(new($"fields.{field.Source}.type", field.Type == "numeric(18,6)" ? "decimal" : field.Type == "boolean" ? "boolean" : "string"));
            }
            var source = new MigrationNode(NodeId(entity.SourceNode), entity.SourceNode, MigrationNodeType.Source,
                entity.SemanticType, sourceSystem, new StorageEndpointId("fhir-source-records"),
                new ArtifactSelector("ndjson", selectorProperties, ["id"]));
            var target = new MigrationNode(NodeId(entity.TargetNode), entity.TargetNode, MigrationNodeType.Target,
                entity.SemanticType, targetSystem, new StorageEndpointId("fhir-target-db"),
                new ArtifactSelector("table", [new("name", $"public.{entity.TargetTable}")], ["id"]));
            nodes.Add(source);
            nodes.Add(target);
            var maps = entity.Fields.Select(field => new TransformationFieldDefinition(field.Target, field.Source,
                field.Codes is null ? [] : [new TransformationStep(TransformationStepType.CodeMap, "1", field.Codes)]));
            edges.Add(new MigrationEdge(EdgeId(entity.TargetNode), entity.TargetNode, [source.Id], [target.Id],
                new MigrationOperation(MigrationOperationType.Transform, fields: maps), "1",
                new RecoveryDefinition(RecoveryMode.Restore, requiresSnapshot: true)));
        }
        var sourceBinary = new MigrationNode(NodeId("source-binary"), "source-binary", MigrationNodeType.Source,
            "Fhir.Binary", sourceSystem, new StorageEndpointId("fhir-source-files"),
            new ArtifactSelector("file-pattern", [new("pattern", "*.txt")], ["relativePath"]));
        var targetBinary = new MigrationNode(NodeId("target-binary"), "target-binary", MigrationNodeType.Archive,
            "Fhir.Binary", targetSystem, new StorageEndpointId("fhir-target-files"),
            new ArtifactSelector("file-pattern", [new("pathField", "relativePath")], ["relativePath"]));
        nodes.Add(sourceBinary);
        nodes.Add(targetBinary);
        edges.Add(new MigrationEdge(EdgeId("binary-archive"), "binary-archive", [sourceBinary.Id], [targetBinary.Id],
            new MigrationOperation(MigrationOperationType.Archive), "1", new RecoveryDefinition(RecoveryMode.Restore, requiresSnapshot: true)));
        var orderedNodes = nodes.OrderBy(node => node.Name, StringComparer.Ordinal).ToArray();
        var orderedEdges = edges.OrderBy(edge => edge.Name, StringComparer.Ordinal).ToArray();
        var names = orderedNodes.ToDictionary(node => node.Id, node => node.Name);
        var canonical = GraphCanonicalizer.Canonicalize(1, orderedNodes, orderedEdges, names);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        return new MigrationGraph(new MigrationGraphId(NodeId("fhir-f5-graph").Value), orderedNodes, orderedEdges,
            hash, GraphCanonicalizer.FormatVersion);
    }

    private static async Task WriteSourceRecordsAsync(FhirFixture fixture, string root, CancellationToken cancellationToken)
    {
        foreach (var entity in fixture.Entities)
        {
            var path = Path.Combine(root, entity.SourceFile + ".ndjson");
            await using var stream = System.IO.File.Create(path);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            foreach (var row in fixture.SourceRows[entity.TargetNode])
            {
                cancellationToken.ThrowIfCancellationRequested();
                var record = new Dictionary<string, object?>(StringComparer.Ordinal) { ["resourceType"] = entity.SemanticType[5..], ["id"] = row["id"] };
                foreach (var field in entity.Fields)
                    if (field.Source != "id")
                        record[field.Source] = field.Type switch
                        {
                            "numeric(18,6)" => decimal.Parse(row[field.Source], CultureInfo.InvariantCulture),
                            "boolean" => bool.Parse(row[field.Source]),
                            _ => row[field.Source]
                        };
                await writer.WriteLineAsync(JsonSerializer.Serialize(record).AsMemory(), cancellationToken);
            }
        }
    }

    private static async Task WriteDocumentFilesAsync(IReadOnlyDictionary<string, byte[]> documents, string root,
        CancellationToken cancellationToken)
    {
        foreach (var (path, content) in documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullPath = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await File.WriteAllBytesAsync(fullPath, content, cancellationToken);
        }
    }

    private static async Task CreateTargetTemplatesAsync(PostgreSqlContainer postgres, FhirFixture fixture, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(postgres.GetConnectionString());
        await connection.OpenAsync(cancellationToken);
        foreach (var entity in fixture.Entities)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE TABLE public.{Quote(entity.TargetTable)} ({string.Join(", ", entity.Fields.Select(field => $"{Quote(field.Target)} {field.Type}"))})";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<ExternalVerificationResult> VerifyExternalAsync(VerificationService service,
        LoadedProjectConfiguration configuration, MigrationGraph graph, SnapshotCaptureResult checkpoint,
        VerificationRuleSet rules, IReadOnlyDictionary<string, List<Dictionary<string, string>>> rows, FhirFixture fixture,
        HashSet<string> missingFiles,
        PostgreSqlContainer postgres, PostgresShadowTargetConnector postgresTarget,
        FilesystemShadowTargetConnector filesystemTarget, RuntimeConnectorContextFactory contexts, string targetFilesRoot,
        string workingDirectory)
    {
        var runId = new RunId(Guid.NewGuid());
        await LoadExternalTargetAsync(postgres, runId, rows, fixture, graph, TestContext.Current.CancellationToken);
        await LoadExternalFilesAsync(configuration, graph, contexts, filesystemTarget, fixture.Documents, missingFiles, runId,
            TestContext.Current.CancellationToken);
        var targetNodes = graph.Nodes.Where(node => node.Type is MigrationNodeType.Target or MigrationNodeType.Archive).ToArray();
        var binaryNode = graph.Nodes.Single(node => node.Name == "target-binary");
        var binaryContext = new ShadowTargetContext(contexts.Create(configuration, binaryNode), runId, SystemRole.ShadowTarget);
        var expectedFiles = fixture.Documents.Where(document => !missingFiles.Contains(document.Key)).ToArray();
        var observedFiles = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var observed in filesystemTarget.ReadAsync(new ReadRequest(binaryContext, binaryNode.Selector, new ReadOptions()),
            TestContext.Current.CancellationToken).WithCancellation(TestContext.Current.CancellationToken))
        {
            var path = ((StringValue)observed.Values["relativePath"]).Value;
            var sourceDocument = fixture.Documents[path];
            Assert.Equal(Convert.ToHexString(SHA256.HashData(sourceDocument)).ToLowerInvariant(),
                ((BinaryReferenceValue)observed.Values["content"]).Sha256);
            Assert.True(observedFiles.Add(path), $"Duplicate external binary artifact {path}.");
        }
        Assert.Equal(expectedFiles.Length, observedFiles.Count);
        var runtimes = targetNodes.Select(node => node.Type == MigrationNodeType.Archive
            ? new VerificationTargetRuntime(node.Name, filesystemTarget,
                new ShadowTargetContext(contexts.Create(configuration, node), runId, SystemRole.ShadowTarget))
            : new VerificationTargetRuntime(node.Name, postgresTarget,
                new ShadowTargetContext(contexts.Create(configuration, node), runId, SystemRole.ShadowTarget))).ToArray();
        var observation = new ExternalMigrationObservation(runId, $"fhir-independent-{runId.Value:N}", configuration.ConfigurationHash,
            graph.GraphHash, checkpoint.Id, checkpoint.Checkpoint!.ManifestHash!, checkpoint.Checkpoint.SourceFingerprint!,
            targetNodes.Select(node => new ExternalTargetEndpoint(node.Name, node.SystemId, node.EndpointId,
                node.Type == MigrationNodeType.Archive ? filesystemTarget.Id : postgresTarget.Id,
                node.Type == MigrationNodeType.Archive ? filesystemTarget.Version : postgresTarget.Version)), DateTimeOffset.UnixEpoch);
        Assert.Equal(Path.GetFullPath(targetFilesRoot), Path.GetFullPath(contexts.Create(configuration, binaryNode)
            .Configuration.GetRequired("root").UseValue(value => value)));
        return await service.VerifyExternalTargetAsync(configuration, graph, observation, rules, workingDirectory, "0.10F",
            runtimes, TestContext.Current.CancellationToken);
    }

    private static async Task LoadExternalTargetAsync(PostgreSqlContainer postgres, RunId runId,
        IReadOnlyDictionary<string, List<Dictionary<string, string>>> rows, FhirFixture fixture, MigrationGraph graph,
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
        foreach (var node in graph.Nodes.Where(node => node.Type == MigrationNodeType.Target))
        {
            var spec = fixture.Entities.Single(entity => entity.TargetNode == node.Name);
            await using (var create = connection.CreateCommand())
            {
                create.CommandText = $"CREATE TABLE {Quote(schemaName)}.{Quote(spec.TargetTable)} ({string.Join(", ", spec.Fields.Select(field => $"{Quote(field.Target)} {field.Type}"))})";
                await create.ExecuteNonQueryAsync(cancellationToken);
            }
            var columns = spec.Fields.Select(field => Quote(field.Target)).ToArray();
            await using var importer = await connection.BeginBinaryImportAsync(
                $"COPY {Quote(schemaName)}.{Quote(spec.TargetTable)} ({string.Join(", ", columns)}) FROM STDIN (FORMAT BINARY)", cancellationToken);
            foreach (var row in rows[node.Name])
            {
                await importer.StartRowAsync(cancellationToken);
                foreach (var field in spec.Fields)
                {
                    if (field.Type == "numeric(18,6)")
                        await importer.WriteAsync(decimal.Parse(row[field.Target], CultureInfo.InvariantCulture), NpgsqlDbType.Numeric, cancellationToken);
                    else if (field.Type == "boolean")
                        await importer.WriteAsync(bool.Parse(row[field.Target]), NpgsqlDbType.Boolean, cancellationToken);
                    else
                        await importer.WriteAsync(row[field.Target], NpgsqlDbType.Text, cancellationToken);
                }
            }
            await importer.CompleteAsync(cancellationToken);
        }
    }

    private static async Task LoadExternalFilesAsync(LoadedProjectConfiguration configuration, MigrationGraph graph,
        RuntimeConnectorContextFactory contexts, FilesystemShadowTargetConnector files,
        IReadOnlyDictionary<string, byte[]> documents, HashSet<string> missing, RunId runId, CancellationToken cancellationToken)
    {
        var node = graph.Nodes.Single(item => item.Name == "target-binary");
        var context = new ShadowTargetContext(contexts.Create(configuration, node), runId, SystemRole.ShadowTarget);
        await files.PrepareAsync(context, node.Selector, cancellationToken);
        foreach (var (path, content) in documents.Where(pair => !missing.Contains(pair.Key)))
        {
            var hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
            var values = new Dictionary<string, ValueNode>(StringComparer.Ordinal)
            {
                ["relativePath"] = new StringValue(path),
                ["content"] = new BinaryReferenceValue($"fhir-fixture:{path}", content.LongLength, hash)
            };
            var artifact = new ArtifactReference(new ArtifactId($"fhir-file:{StableGuid(path):N}"), node.SystemId, node.EndpointId,
                "File.Artifact", path);
            var record = new RecordEnvelope(artifact, node.SemanticType, values,
                new ProvenanceMetadata(new ConnectorId("files"), node.EndpointId, path, DateTimeOffset.UnixEpoch, hash));
            await files.WriteAsync(new ShadowWriteRequest(context, node.Selector, record, node.Name,
                (_, _) => ValueTask.FromResult<Stream>(new MemoryStream(content, writable: false))), cancellationToken);
        }
        await files.CompleteAsync(context, cancellationToken);
    }

    private static MigrationNodeId NodeId(string value) => new(StableGuid("node:" + value));
    private static MigrationEdgeId EdgeId(string value) => new(StableGuid("edge:" + value));
    private static Guid StableGuid(string value) => new(SHA256.HashData(Encoding.UTF8.GetBytes(value))[..16]);
    private static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    private sealed record FieldMap(string Target, string Source, string Type, IReadOnlyDictionary<string, string>? Codes = null);
    private sealed record EntitySpec(string SourceNode, string TargetNode, string SourceFile, string TargetTable, string SemanticType, FieldMap[] Fields);
    private sealed record FhirFixture(EntitySpec[] Entities, Dictionary<string, List<Dictionary<string, string>>> SourceRows,
        Dictionary<string, byte[]> Documents);
    private sealed record FhirDefect(string Id, string Category, string SemanticConcept, string BusinessKey, string ExpectedRuleId,
        int ExpectedFindingCount = 1);
    private sealed class TemporaryDirectoryCleanup(string path) : IDisposable
    {
        public void Dispose() { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
    }
    private sealed class FhirEnvironment(IReadOnlyDictionary<string, string> values) : IEnvironmentVariableProvider
    {
        public string? GetValue(string name) => values.GetValueOrDefault(name);
    }
}
