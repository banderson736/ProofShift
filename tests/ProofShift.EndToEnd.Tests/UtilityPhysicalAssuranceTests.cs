using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Data.SqlClient;
using Npgsql;
using NpgsqlTypes;
using ProofShift.Configuration;
using ProofShift.Connectors.Abstractions;
using ProofShift.Connectors.Csv;
using ProofShift.Connectors.Files;
using ProofShift.Connectors.Postgres;
using ProofShift.Connectors.SqlServer;
using ProofShift.Domain;
using ProofShift.Engine;
using ProofShift.Evidence;
using ProofShift.Graph;
using ProofShift.Packs.Abstractions;
using ProofShift.Packs.Utility;
using ProofShift.Projection;
using ProofShift.Recovery;
using ProofShift.Snapshots;
using ProofShift.Verification;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;
using Xunit;

namespace ProofShift.EndToEnd.Tests;

[Collection("DockerIntegration")]
public sealed class UtilityPhysicalAssuranceTests
{
    private const int AccountCount = 1_000;
    private const string SqlSecret = "PS010F_UTILITY_SQL";
    private const string CsvRootSecret = "PS010F_UTILITY_CSV";
    private const string SourceFilesSecret = "PS010F_UTILITY_SOURCE_FILES";
    private const string TargetConnectionSecret = "PS010F_UTILITY_TARGET";
    private const string TargetFilesSecret = "PS010F_UTILITY_TARGET_FILES";

    private static readonly UtilityDefect[] DefectManifest =
    [
        new("utility-missing-account", "missing entity", "Utility.Account", "target-presence"),
        new("utility-duplicate-customer", "duplicate entity", "Utility.Customer", "customer-uniqueness"),
        new("utility-missing-account-reference", "referential consequence", "Utility.ServicePoint", "service-point-account-reference"),
        new("utility-orphan-service-point", "broken relationship", "Utility.ServicePoint", "service-point-account-reference"),
        new("utility-wrong-meter-assignment", "incorrect relationship", "Utility.Meter", "meter-servicepoint-attribute"),
        new("utility-meter-read-order", "temporal defect", "Utility.MeterRead", "meter-read-sequence"),
        new("utility-meter-read-value", "incorrect attribute", "Utility.MeterRead", "meter-read-sequence"),
        new("utility-rate-code", "code transformation", "Utility.RateAssignment", "rate-code-mapping"),
        new("utility-invoice-total", "financial aggregate", "Utility.Invoice", "invoice-total"),
        new("utility-payment-owner", "incorrect relationship", "Utility.Payment", "payment-owner"),
        new("utility-adjustment-total", "financial aggregate", "Utility.Adjustment", "adjustment-total"),
        new("utility-document-owner", "incorrect relationship", "Utility.Document", "document-owner"),
        new("utility-missing-document", "missing artifact", "Utility.DocumentPayload", "target-presence"),
        new("utility-unexpected-customer", "unexpected target", "Utility.Customer", "unexpected-target")
    ];

    [Fact]
    public async Task UtilityExternalAndShadowScenariosProveExactDefectsAndQualification()
    {
        Assert.Equal(DefectManifest.Length, DefectManifest.Select(defect => defect.Id).Distinct(StringComparer.Ordinal).Count());
        var fixture = CreateFixture(AccountCount);
        var root = Path.Combine(Path.GetTempPath(), $"proofshift-utility-f2-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        using var cleanup = new TemporaryDirectoryCleanup(root);
        var sourceCsv = Path.Combine(root, "source-csv");
        var sourceFiles = Path.Combine(root, "source-files");
        var targetFiles = Path.Combine(root, "target-files");
        Directory.CreateDirectory(targetFiles);
        await WriteCsvSourcesAsync(fixture, sourceCsv, TestContext.Current.CancellationToken);
        await WriteSourceFilesAsync(fixture, sourceFiles, TestContext.Current.CancellationToken);

        var sqlContainer = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04")
            .WithPassword("Synthetic-Utility-Only-Password!2026").Build();
        try
        {
            await sqlContainer.StartAsync(TestContext.Current.CancellationToken);
        }
        catch (DockerUnavailableException)
        {
            await sqlContainer.DisposeAsync();
            throw Xunit.Sdk.SkipException.ForSkip("A Docker-compatible runtime is required for Utility physical assurance.");
        }
        await using var sqlLifetime = sqlContainer;

        var postgresContainer = new PostgreSqlBuilder("postgres:16-alpine").Build();
        try
        {
            await postgresContainer.StartAsync(TestContext.Current.CancellationToken);
        }
        catch (DockerUnavailableException)
        {
            await postgresContainer.DisposeAsync();
            throw Xunit.Sdk.SkipException.ForSkip("A Docker-compatible runtime is required for Utility physical assurance.");
        }
        await using var postgresLifetime = postgresContainer;

        var sqlConnection = new SqlConnectionStringBuilder(sqlContainer.GetConnectionString())
        {
            Encrypt = false,
            TrustServerCertificate = true
        }.ConnectionString;
        await SeedSqlSourcesAsync(sqlConnection, fixture, TestContext.Current.CancellationToken);
        var configuration = CreateConfiguration(postgresContainer.GetConnectionString(), sourceCsv,
            sourceFiles, targetFiles);
        var graph = CreateGraph(fixture, configuration);
        var sourceConnectors = new ConnectorRegistry(
        [new SqlServerSourceConnector(), new CsvSourceConnector(), new FilesystemSourceConnector()]);
        var postgresTarget = new PostgresShadowTargetConnector();
        var filesystemTarget = new FilesystemShadowTargetConnector();
        var targetConnectors = new ShadowTargetConnectorRegistry([postgresTarget, filesystemTarget]);
        var environment = new UtilityEnvironmentProvider(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [SqlSecret] = sqlConnection,
            [CsvRootSecret] = sourceCsv,
            [SourceFilesSecret] = sourceFiles,
            [TargetConnectionSecret] = postgresContainer.GetConnectionString(),
            [TargetFilesSecret] = targetFiles
        });
        var contextFactory = new RuntimeConnectorContextFactory(environment);
        var snapshotStore = new FileSystemSnapshotStore(Path.Combine(root, "checkpoints"));
        var inspection = await new SourceInspectionService(sourceConnectors, contextFactory)
            .InspectAsync(configuration, graph, TestContext.Current.CancellationToken);
        Assert.True(inspection.IsValid, string.Join(Environment.NewLine, inspection.Sources
            .SelectMany(source => source.Inspection.Issues).Select(issue => $"{issue.Code}: {issue.Message}")));
        var capture = await new SnapshotCaptureService(sourceConnectors, snapshotStore, contextFactory)
            .CaptureAsync(configuration, graph, TestContext.Current.CancellationToken);
        Assert.True(capture.Status == CheckpointStatus.Complete,
            $"Utility source checkpoint failed: {capture.FailureCode}");
        Assert.NotNull(capture.Checkpoint);

        var rules = new PackRegistry([new UtilityPack()]).Resolve(configuration.Root.Packs)
            .Resolve(CreateRules());
        var correctedRows = BuildTargetRows(fixture);
        var expectedTargetCount = correctedRows.Values.Sum(rows => (long)rows.Count) + fixture.Documents.Count;
        var verification = new VerificationService(snapshotStore);
        var corrected = await VerifyExternalAsync(verification, configuration, graph, capture, fixture, rules, correctedRows,
            fixture.Documents, new HashSet<string>(StringComparer.Ordinal), contextFactory, postgresTarget, filesystemTarget, postgresContainer,
            Path.Combine(root, "external-corrected"));
        Assert.Equal(VerificationOutcome.Passed, corrected.Run.Outcome);
        Assert.DoesNotContain(corrected.Findings, finding => finding.Result == EvidenceResult.Fail);
        Assert.Equal(capture.Checkpoint!.ArtifactCount, corrected.Ledger.DispositionCount);
        Assert.Equal(expectedTargetCount, corrected.Ledger.LineageCount);

        var defectiveRows = CloneRows(correctedRows);
        ApplyDefects(defectiveRows);
        var missingDocuments = new HashSet<string>([fixture.Documents[1].DocumentId], StringComparer.Ordinal);
        var defective = await VerifyExternalAsync(verification, configuration, graph, capture, fixture, rules, defectiveRows,
            fixture.Documents, missingDocuments, contextFactory, postgresTarget, filesystemTarget, postgresContainer,
            Path.Combine(root, "external-defective"));
        Assert.Equal(VerificationOutcome.Failed, defective.Run.Outcome);
        var failures = defective.Findings.Where(finding => finding.Result == EvidenceResult.Fail).ToArray();
        var missingDefects = DefectManifest.Where(defect => !failures.Any(finding => finding.RuleId.Value == defect.ExpectedRuleId))
            .Select(defect => $"{defect.Id}->{defect.ExpectedRuleId}").ToArray();
        var observedFailures = failures.Select(finding => $"{finding.RuleId.Value}:{finding.Code}").Distinct(StringComparer.Ordinal);
        Assert.True(missingDefects.Length == 0,
            $"Unmatched Utility defect manifest entries: {string.Join(", ", missingDefects)}. Observed failures: {string.Join(", ", observedFailures)}");
        var expectedRuleFailureCounts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["target-presence"] = 2,
            ["customer-uniqueness"] = 1,
            ["service-point-account-reference"] = 2,
            ["meter-servicepoint-attribute"] = 1,
            ["meter-read-sequence"] = 4,
            ["rate-code-mapping"] = 1,
            ["invoice-total"] = 1,
            ["payment-owner"] = 1,
            ["adjustment-total"] = 1,
            ["document-owner"] = 1,
            ["unexpected-target"] = 1
        };
        var observedRuleFailureCounts = failures.Where(finding => expectedRuleFailureCounts.ContainsKey(finding.RuleId.Value))
            .GroupBy(finding => finding.RuleId.Value, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        Assert.Equal(expectedRuleFailureCounts.OrderBy(pair => pair.Key, StringComparer.Ordinal),
            observedRuleFailureCounts.OrderBy(pair => pair.Key, StringComparer.Ordinal));
        var meterReadCodeCounts = failures.Where(finding => finding.RuleId.Value == "meter-read-sequence")
            .GroupBy(finding => finding.Code, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        Assert.Equal(new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["UtilityMeterReadMismatch"] = 3,
            ["UtilityMeterReadChronology"] = 1
        }.OrderBy(pair => pair.Key, StringComparer.Ordinal),
            meterReadCodeCounts.OrderBy(pair => pair.Key, StringComparer.Ordinal));
        Assert.Equal(2, failures.Count(finding => finding.RuleId.Value == "payment-total"));
        Assert.Equal(expectedTargetCount - 2, defective.Ledger.LineageCount);
        Assert.Equal(capture.Checkpoint.ArtifactCount, defective.Ledger.DispositionCount);

        await CreatePostgresTemplatesAsync(postgresContainer, fixture, TestContext.Current.CancellationToken);
        var evidenceStore = new FileSystemEvidenceStore(Path.Combine(root, ".proofshift", "verifications"));
        var recoveryStore = new FileSystemRecoveryArtifactStore(Path.Combine(root, ".proofshift", "recovery"));
        var dryRun = await new DryRunOrchestrator(sourceConnectors, targetConnectors, snapshotStore,
            verification, new RecoveryService(snapshotStore,
                new RecoveryCompensatorRegistry([new ShadowBaselineRestoreCompensator()])), contextFactory)
            .RunAsync(configuration, graph, rules, new EffectiveRecoveryPolicy(), Path.Combine(root, "projected-corrected"),
                Path.Combine(root, "temporary", "projected-corrected"), "0.10F", evidenceStore, recoveryStore,
                TestContext.Current.CancellationToken);
        Assert.Equal(DryRunExecutionOutcome.Qualified, dryRun.Outcome);
        Assert.Equal(VerificationOutcome.Passed, dryRun.Verification!.Run.Outcome);
        Assert.Equal(DryRunQualificationStatus.Qualified, dryRun.Recovery!.Qualification.Status);
        Assert.Equal(0, dryRun.Verification.Findings.Count(finding => finding.Result == EvidenceResult.Fail));
    }

    private static UtilityFixture CreateFixture(int accountCount)
    {
        var customerRows = Enumerable.Range(1, accountCount).Select(index => Row(
            ("customer_id", $"C{index:D6}"), ("name", $"Synthetic Customer {index:D6}"))).ToArray();
        var accountRows = Enumerable.Range(1, accountCount).Select(index => Row(
            ("account_id", $"A{index:D6}"), ("customer_id", $"C{index:D6}"),
            ("status_code", index % 2 == 0 ? "A" : "I"), ("effective_from", "2024-01-01"))).ToArray();
        var servicePointRows = Enumerable.Range(1, accountCount).Select(index => Row(
            ("service_point_id", $"SP{index:D6}"), ("account_id", $"A{index:D6}"), ("status", "ACTIVE"))).ToArray();
        var meterRows = Enumerable.Range(1, accountCount).Select(index => Row(
            ("meter_id", $"M{index:D6}"), ("service_point_id", $"SP{index:D6}"), ("serial_number", $"SERIAL-{index:D6}"))).ToArray();
        var meterReadRows = Enumerable.Range(1, accountCount).SelectMany(index => new[]
        {
            Row(("read_id", $"RD{index:D6}-01"), ("meter_id", $"M{index:D6}"), ("read_sequence", "1"),
                ("read_at", "2026-01-01T08:00:00"), ("usage", "100.125"), ("unit", "kWh")),
            Row(("read_id", $"RD{index:D6}-02"), ("meter_id", $"M{index:D6}"), ("read_sequence", "2"),
                ("read_at", "2026-02-01T08:00:00"), ("usage", "112.000"), ("unit", "kWh"))
        }).ToArray();
        var invoiceRows = Enumerable.Range(1, accountCount).Select(index => Row(
            ("invoice_id", $"INV{index:D6}"), ("account_id", $"A{index:D6}"),
            ("billing_period", "2026-01"), ("invoice_total", "120.00"))).ToArray();
        var paymentRows = Enumerable.Range(1, accountCount).Select(index => Row(
            ("payment_id", $"PAY{index:D6}"), ("account_id", $"A{index:D6}"),
            ("invoice_id", $"INV{index:D6}"), ("payment_amount", "100.00"))).ToArray();
        var adjustmentRows = Enumerable.Range(1, accountCount).Select(index => Row(
            ("adjustment_id", $"ADJ{index:D6}"), ("account_id", $"A{index:D6}"),
            ("adjustment_amount", "1.25"))).ToArray();
        var rateRows = Enumerable.Range(1, accountCount).Select(index => Row(
            ("account_id", $"A{index:D6}"), ("rate_code", index % 2 == 0 ? "R1" : "R2"),
            ("effective_from", "2024-01-01"))).ToArray();
        var documentCount = Math.Max(accountCount / 10, 1);
        var documents = Enumerable.Range(1, documentCount).Select(index =>
        {
            var documentId = $"DOC{index:D6}";
            var accountId = $"A{index * 10:D6}";
            var relativePath = $"documents/{documentId}.txt";
            var bytes = Encoding.UTF8.GetBytes($"Synthetic Utility document {documentId} for {accountId}.");
            var hash = Sha(bytes);
            return new UtilityDocument(documentId, accountId, relativePath, bytes, hash);
        }).ToArray();
        var documentRows = documents.Select(document => Row(("document_id", document.DocumentId),
            ("account_id", document.AccountId), ("relative_path", document.RelativePath),
            ("content_hash", document.Sha256))).ToArray();

        return new UtilityFixture(
        [
            SqlEntity("UtilityCustomer", "source-customer", "target-customer", "Utility.Customer", ["customer_id"],
                ["customer_id", "name"], customerRows, [Map("customer_id"), Map("name")]),
            SqlEntity("UtilityAccount", "source-account", "target-account", "Utility.Account", ["account_id"],
                ["account_id", "customer_id", "status_code", "effective_from"], accountRows,
                [Map("account_id"), Map("customer_id"), Map("status", "status_code", ("A", "ACTIVE"), ("I", "INACTIVE")), Map("effective_from")]),
            SqlEntity("UtilityServicePoint", "source-service-point", "target-service-point", "Utility.ServicePoint", ["service_point_id"],
                ["service_point_id", "account_id", "status"], servicePointRows,
                [Map("service_point_id"), Map("account_id"), Map("status")]),
            SqlEntity("UtilityMeter", "source-meter", "target-meter", "Utility.Meter", ["meter_id"],
                ["meter_id", "service_point_id", "serial_number"], meterRows,
                [Map("meter_id"), Map("service_point_id"), Map("serial_number")]),
            SqlEntity("UtilityMeterRead", "source-meter-read", "target-meter-read", "Utility.MeterRead", ["read_id"],
                ["read_id", "meter_id", "read_sequence", "read_at", "usage", "unit"], meterReadRows,
                [Map("read_id"), Map("meter_id"), Map("read_sequence"), Map("read_at"), Map("usage"), Map("unit")]),
            SqlEntity("UtilityInvoice", "source-invoice", "target-invoice", "Utility.Invoice", ["invoice_id"],
                ["invoice_id", "account_id", "billing_period", "invoice_total"], invoiceRows,
                [Map("invoice_id"), Map("account_id"), Map("billing_period"), Map("invoice_total")]),
            SqlEntity("UtilityPayment", "source-payment", "target-payment", "Utility.Payment", ["payment_id"],
                ["payment_id", "account_id", "invoice_id", "payment_amount"], paymentRows,
                [Map("payment_id"), Map("account_id"), Map("invoice_id"), Map("payment_amount")]),
            SqlEntity("UtilityAdjustment", "source-adjustment", "target-adjustment", "Utility.Adjustment", ["adjustment_id"],
                ["adjustment_id", "account_id", "adjustment_amount"], adjustmentRows,
                [Map("adjustment_id"), Map("account_id"), Map("adjustment_amount")]),
            CsvEntity("source-rate-assignment", "target-rate-assignment", "Utility.RateAssignment", "rates.csv", ["account_id"],
                ["account_id", "rate_code", "effective_from"], rateRows,
                [Map("account_id"), Map("rate_code", "rate_code", ("R1", "RESIDENTIAL"), ("R2", "COMMERCIAL")), Map("effective_from")]),
            CsvEntity("source-document-metadata", "target-document", "Utility.Document", "documents.csv", ["document_id"],
                ["document_id", "account_id", "relative_path", "content_hash"], documentRows,
                [Map("document_id"), Map("account_id"), Map("relative_path"), Map("content_hash")])
        ], documents);
    }

    private static LoadedProjectConfiguration CreateConfiguration(string postgresConnection,
        string csvRoot, string sourceFilesRoot, string targetFilesRoot)
    {
        var source = new SystemDefinition(new SystemId("utility-legacy"), "Synthetic Utility Source", SystemRole.Source,
        [
            new(new StorageEndpointId("utility-sql"), new ConnectorId("sqlserver"),
                [new("connection", $"secret:{SqlSecret}"), new("checkpoint.consistency", "observed")]),
            new(new StorageEndpointId("utility-csv"), new ConnectorId("csv"),
                [new("root", $"secret:{CsvRootSecret}"), new("delimiter", ";")]),
            new(new StorageEndpointId("utility-source-files"), new ConnectorId("files"),
                [new("root", $"secret:{SourceFilesSecret}")])
        ]);
        var target = new SystemDefinition(new SystemId("utility-target"), "Synthetic Utility Target", SystemRole.ShadowTarget,
        [
            new(new StorageEndpointId("utility-postgres"), new ConnectorId("postgres"),
                [new("connection", $"secret:{TargetConnectionSecret}")]),
            new(new StorageEndpointId("utility-target-files"), new ConnectorId("files"),
                [new("root", $"secret:{TargetFilesSecret}")])
        ]);
        var root = new RootConfigurationDto(1, new ProjectConfigurationDto("utility-f2", "Synthetic Utility Assurance"),
            null, [], null, null, null, packs: [new PackConfigurationDto("proofshift.utility", new UtilityPack().Version)]);
        return new LoadedProjectConfiguration(root, [], [source, target], [], "synthetic-utility-f2", new string('a', 64));
    }

    private static MigrationGraph CreateGraph(UtilityFixture fixture, LoadedProjectConfiguration configuration)
    {
        var sourceSystem = configuration.Systems.Single(system => system.Id.Value == "utility-legacy").Id;
        var targetSystem = configuration.Systems.Single(system => system.Id.Value == "utility-target").Id;
        var nodes = new List<MigrationNode>();
        var edges = new List<MigrationEdge>();
        foreach (var entity in fixture.Entities)
        {
            var sourceSelector = entity.SourceConnector == "sqlserver"
                ? new ArtifactSelector("table", [new("name", entity.SourceLocation)], entity.IdentityFields)
                : new ArtifactSelector("csv", [new("path", entity.SourceLocation), new("delimiter", ";")], entity.IdentityFields);
            var targetSelector = new ArtifactSelector("table", [new("name", $"public.{entity.TargetTable}")], entity.TargetIdentityFields);
            var source = new MigrationNode(NodeId(entity.SourceNode), entity.SourceNode, MigrationNodeType.Source,
                entity.SemanticType, sourceSystem,
                new StorageEndpointId(entity.SourceConnector == "sqlserver" ? "utility-sql" : "utility-csv"), sourceSelector);
            var target = new MigrationNode(NodeId(entity.TargetNode), entity.TargetNode, MigrationNodeType.Target,
                entity.SemanticType, targetSystem, new StorageEndpointId("utility-postgres"), targetSelector);
            nodes.Add(source);
            nodes.Add(target);
            var fields = entity.FieldMappings.Select(mapping => new TransformationFieldDefinition(mapping.Target, mapping.Source,
                mapping.Codes is null ? [] : [new TransformationStep(TransformationStepType.CodeMap, "1",
                    mapping.Codes.Select(pair => new KeyValuePair<string, string>(pair.Key, pair.Value)))]));
            edges.Add(new MigrationEdge(EdgeId($"{entity.TargetNode}-conversion"), $"{entity.TargetNode}-conversion",
                [source.Id], [target.Id], new MigrationOperation(MigrationOperationType.Transform, fields: fields), "1",
                new RecoveryDefinition(RecoveryMode.Restore, requiresSnapshot: true)));
        }

        var fileSource = new MigrationNode(NodeId("source-document-payload"), "source-document-payload",
            MigrationNodeType.Source, "Utility.DocumentPayload", sourceSystem,
            new StorageEndpointId("utility-source-files"),
            new ArtifactSelector("file-pattern", [new("pattern", "documents/*.txt")], ["relativePath"]));
        var fileTarget = new MigrationNode(NodeId("target-document-payload"), "target-document-payload",
            MigrationNodeType.Archive, "Utility.DocumentPayload", targetSystem,
            new StorageEndpointId("utility-target-files"),
            new ArtifactSelector("file-pattern", [new("pathField", "relativePath")], ["relativePath"]));
        nodes.Add(fileSource);
        nodes.Add(fileTarget);
        edges.Add(new MigrationEdge(EdgeId("document-payload-archive"), "document-payload-archive",
            [fileSource.Id], [fileTarget.Id], new MigrationOperation(MigrationOperationType.Archive), "1",
            new RecoveryDefinition(RecoveryMode.Restore, requiresSnapshot: true)));

        var orderedNodes = nodes.OrderBy(node => node.Name, StringComparer.Ordinal).ToArray();
        var orderedEdges = edges.OrderBy(edge => edge.Name, StringComparer.Ordinal).ToArray();
        var names = orderedNodes.ToDictionary(node => node.Id, node => node.Name);
        var canonical = GraphCanonicalizer.Canonicalize(1, orderedNodes, orderedEdges, names);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        return new MigrationGraph(new MigrationGraphId(NodeId("utility-f2-graph").Value), orderedNodes, orderedEdges,
            hash, GraphCanonicalizer.FormatVersion);
    }

    private static VerificationRuleDefinition[] CreateRules()
    {
        VerificationRuleDefinition[] rules =
        [
            Rule("source-accounting", "source-artifact-accounting"),
            Rule("target-lineage", "target-lineage"),
            Rule("target-presence", "target-presence"),
            Rule("unexpected-target", "unexpected-target"),
            Rule("customer-uniqueness", "entity-uniqueness", TextOption("targetNode", "target-customer"),
                TextOption("semanticType", "Utility.Customer")),
            AttributeRule("utility-account-status", "target-account", "Utility.Account", "status", "account_id"),
            AttributeRule("rate-code-mapping", "target-rate-assignment", "Utility.RateAssignment", "rate_code", "account_id"),
            AttributeRule("meter-servicepoint-attribute", "target-meter", "Utility.Meter", "service_point_id", "meter_id"),
            AttributeRule("payment-owner", "target-payment", "Utility.Payment", "account_id", "payment_id"),
            AttributeRule("document-owner", "target-document", "Utility.Document", "account_id", "document_id"),
            ReferenceRule("account-customer-reference", "target-account", "Utility.Account", "customer_id",
                "target-customer", "Utility.Customer", "customer_id"),
            ReferenceRule("service-point-account-reference", "target-service-point", "Utility.ServicePoint", "account_id",
                "target-account", "Utility.Account", "account_id"),
            ReferenceRule("meter-servicepoint-reference", "target-meter", "Utility.Meter", "service_point_id",
                "target-service-point", "Utility.ServicePoint", "service_point_id"),
            ReferenceRule("invoice-account-reference", "target-invoice", "Utility.Invoice", "account_id",
                "target-account", "Utility.Account", "account_id"),
            ReferenceRule("payment-account-reference", "target-payment", "Utility.Payment", "account_id",
                "target-account", "Utility.Account", "account_id"),
            ReferenceRule("adjustment-account-reference", "target-adjustment", "Utility.Adjustment", "account_id",
                "target-account", "Utility.Account", "account_id"),
            ReferenceRule("rate-account-reference", "target-rate-assignment", "Utility.RateAssignment", "account_id",
                "target-account", "Utility.Account", "account_id"),
            ReferenceRule("document-account-reference", "target-document", "Utility.Document", "account_id",
                "target-account", "Utility.Account", "account_id"),
            Rule("meter-read-sequence", "utility-meter-read-sequence",
                TextOption("targetNode", "target-meter-read"), TextOption("semanticType", "Utility.MeterRead"),
                TextOption("readKeyField", "read_id"), TextOption("meterField", "meter_id"),
                TextOption("sequenceField", "read_sequence"), TextOption("timestampField", "read_at"),
                TextOption("usageField", "usage"), TextOption("unitField", "unit")),
            FinancialRule("invoice-total", "utility-invoice-reconciliation", "target-invoice", "Utility.Invoice",
                "invoice_total", ["account_id", "billing_period"]),
            FinancialRule("payment-total", "utility-payment-reconciliation", "target-payment", "Utility.Payment",
                "payment_amount", ["account_id", "invoice_id"]),
            FinancialRule("adjustment-total", "utility-adjustment-reconciliation", "target-adjustment", "Utility.Adjustment",
                "adjustment_amount", ["account_id"])
        ];
        return rules;
    }

    private static VerificationRuleDefinition AttributeRule(string id, string node, string semanticType, string field,
        string identityField) =>
        Rule(id, "utility-attribute-equality", TextOption("attribute", field), TextOption("targetNode", node),
            TextOption("semanticType", semanticType), TextOption("identityField", identityField));

    private static VerificationRuleDefinition ReferenceRule(string id, string sourceNode, string sourceSemantic,
        string referenceField, string referenceNode, string referenceSemantic, string referenceKeyField) =>
        Rule(id, "utility-reference-integrity", TextOption("sourceNode", sourceNode), TextOption("semanticType", sourceSemantic),
            TextOption("referenceField", referenceField), TextOption("referenceNode", referenceNode),
            TextOption("referenceSemanticType", referenceSemantic), TextOption("referenceKeyField", referenceKeyField));

    private static VerificationRuleDefinition FinancialRule(string id, string type, string node, string semanticType,
        string amountField, string[] groupFields) => Rule(id, type, TextOption("targetNode", node),
        TextOption("semanticType", semanticType), TextOption("amountField", amountField),
        new KeyValuePair<string, ValueNode>("groupBy", new CollectionValue(groupFields.Select(field => (ValueNode)new StringValue(field)))),
        new KeyValuePair<string, ValueNode>("tolerance", new DecimalValue(0m)));

    private static VerificationRuleDefinition Rule(string id, string type, params KeyValuePair<string, ValueNode>[] options) =>
        new(new RuleId(id), type, "1", EvidenceSeverity.Critical, structuredOptions: options);

    private static KeyValuePair<string, ValueNode> TextOption(string name, string value) =>
        new(name, new StringValue(value));

    private static async Task LoadExternalPostgresTargetAsync(PostgreSqlContainer container, RunId runId, UtilityFixture fixture,
        IReadOnlyDictionary<string, List<Dictionary<string, string>>> rows, CancellationToken cancellationToken)
    {
        var schema = $"proofshift_shadow_{runId.Value:N}";
        await using var connection = new NpgsqlConnection(container.GetConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using (var createSchema = connection.CreateCommand())
        {
            createSchema.CommandText = $"CREATE SCHEMA {Quote(schema)}";
            await createSchema.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var targetEntity in fixture.Entities)
        {
            var specification = rows[targetEntity.TargetNode];
            var columns = specification.SelectMany(row => row.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            await using (var createTable = connection.CreateCommand())
            {
                createTable.CommandText = $"CREATE TABLE {Quote(schema)}.{Quote(targetEntity.TargetTable)} ({string.Join(", ", columns.Select(column =>
                    $"{Quote(column)} text"))})";
                await createTable.ExecuteNonQueryAsync(cancellationToken);
            }

            var copy = $"COPY {Quote(schema)}.{Quote(targetEntity.TargetTable)} ({string.Join(", ", columns.Select(Quote))}) FROM STDIN (FORMAT BINARY)";
            await using var importer = await connection.BeginBinaryImportAsync(copy, cancellationToken);
            foreach (var row in specification)
            {
                await importer.StartRowAsync(cancellationToken);
                foreach (var column in columns)
                {
                    await importer.WriteAsync(row[column], NpgsqlDbType.Text, cancellationToken);
                }
            }
            await importer.CompleteAsync(cancellationToken);
        }
    }

    private static async Task CreatePostgresTemplatesAsync(PostgreSqlContainer container, UtilityFixture fixture,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(container.GetConnectionString());
        await connection.OpenAsync(cancellationToken);
        foreach (var entity in fixture.Entities)
        {
            await using var create = connection.CreateCommand();
            create.CommandText = $"CREATE TABLE public.{Quote(entity.TargetTable)} ({string.Join(", ", entity.FieldMappings.Select(field =>
                $"{Quote(field.Target)} {PostgresType(entity, field)}"))})";
            await create.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static string PostgresType(UtilityEntity entity, FieldMap field)
    {
        if (entity.SourceConnector != "sqlserver" || field.Codes is not null) return "text";
        return SqlType(field.Source) switch
        {
            "date" => "date",
            "datetime2(7)" => "timestamp without time zone",
            "int" => "integer",
            "decimal(18,3)" or "decimal(18,2)" => "numeric",
            _ => "text"
        };
    }

    private static async Task WritePostgresValueAsync(NpgsqlBinaryImporter importer, UtilityEntity entity,
        FieldMap field, string value, CancellationToken cancellationToken)
    {
        var type = PostgresType(entity, field);
        switch (type)
        {
            case "date":
                await importer.WriteAsync(DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                    NpgsqlDbType.Date, cancellationToken);
                break;
            case "datetime2(7)":
                var local = DateTime.SpecifyKind(DateTime.ParseExact(value, "yyyy-MM-dd'T'HH:mm:ss",
                    CultureInfo.InvariantCulture, DateTimeStyles.None), DateTimeKind.Unspecified);
                await importer.WriteAsync(local, NpgsqlDbType.Timestamp, cancellationToken);
                break;
            case "integer":
                await importer.WriteAsync(int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture),
                    NpgsqlDbType.Integer, cancellationToken);
                break;
            case "numeric":
                await importer.WriteAsync(decimal.Parse(value, NumberStyles.Number, CultureInfo.InvariantCulture),
                    NpgsqlDbType.Numeric, cancellationToken);
                break;
            default:
                await importer.WriteAsync(value, NpgsqlDbType.Text, cancellationToken);
                break;
        }
    }

    private static async Task<ExternalVerificationResult> VerifyExternalAsync(VerificationService service,
        LoadedProjectConfiguration configuration, MigrationGraph graph, SnapshotCaptureResult capture, UtilityFixture fixture,
        VerificationRuleSet rules, IReadOnlyDictionary<string, List<Dictionary<string, string>>> rows,
        IReadOnlyCollection<UtilityDocument> documents, IReadOnlySet<string> missingDocuments,
        RuntimeConnectorContextFactory contextFactory, PostgresShadowTargetConnector postgres,
        FilesystemShadowTargetConnector files, PostgreSqlContainer container, string temporaryDirectory)
    {
        var observationRunId = new RunId(Guid.NewGuid());
        await LoadExternalPostgresTargetAsync(container, observationRunId, fixture, rows, TestContext.Current.CancellationToken);
        await LoadExternalFileTargetAsync(configuration, graph, contextFactory, files, documents, missingDocuments,
            observationRunId, TestContext.Current.CancellationToken);
        var targetNodes = graph.Nodes.Where(node => node.Type is MigrationNodeType.Target or MigrationNodeType.Archive)
            .OrderBy(node => node.Name, StringComparer.Ordinal).ToArray();
        var runtimes = targetNodes.Select(node =>
        {
            var connector = node.EndpointId.Value == "utility-target-files"
                ? (IShadowTargetConnector)files : postgres;
            return new VerificationTargetRuntime(node.Name, connector,
                new ShadowTargetContext(contextFactory.Create(configuration, node), observationRunId, SystemRole.ShadowTarget));
        }).ToArray();
        var observation = new ExternalMigrationObservation(observationRunId,
            $"utility-independent-target-{observationRunId.Value:N}", configuration.ConfigurationHash, graph.GraphHash,
            capture.Id, capture.Checkpoint!.ManifestHash!, capture.Checkpoint.SourceFingerprint!,
            targetNodes.Select(node =>
            {
                var connector = node.EndpointId.Value == "utility-target-files"
                    ? (IShadowTargetConnector)files : postgres;
                return new ExternalTargetEndpoint(node.Name, node.SystemId, node.EndpointId, connector.Id, connector.Version);
            }), DateTimeOffset.UnixEpoch);
        return await service.VerifyExternalTargetAsync(configuration, graph, observation, rules, temporaryDirectory,
            "0.10F", runtimes, TestContext.Current.CancellationToken);
    }

    private static async Task LoadExternalFileTargetAsync(LoadedProjectConfiguration configuration,
        MigrationGraph graph, RuntimeConnectorContextFactory contextFactory, FilesystemShadowTargetConnector connector,
        IReadOnlyCollection<UtilityDocument> documents, IReadOnlySet<string> missingDocuments, RunId runId,
        CancellationToken cancellationToken)
    {
        var node = graph.Nodes.Single(item => item.Name == "target-document-payload");
        var context = new ShadowTargetContext(contextFactory.Create(configuration, node), runId, SystemRole.ShadowTarget);
        await connector.PrepareAsync(context, node.Selector, cancellationToken);
        foreach (var document in documents.Where(item => !missingDocuments.Contains(item.DocumentId)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var binary = new BinaryReferenceValue($"utility-fixture:{document.RelativePath}", document.Content.LongLength, document.Sha256);
            var values = new Dictionary<string, ValueNode>(StringComparer.Ordinal)
            {
                ["relativePath"] = new StringValue(document.RelativePath),
                ["content"] = binary
            };
            var artifact = new ArtifactReference(new ArtifactId($"utility-file:{document.DocumentId}"), node.SystemId,
                node.EndpointId, "File.Artifact", document.RelativePath);
            var record = new RecordEnvelope(artifact, node.SemanticType, values,
                new ProvenanceMetadata(new ConnectorId("files"), node.EndpointId, document.RelativePath,
                    DateTimeOffset.UnixEpoch, document.Sha256));
            await connector.WriteAsync(new ShadowWriteRequest(context, node.Selector, record, node.Name,
                (_, _) => ValueTask.FromResult<Stream>(new MemoryStream(document.Content, writable: false))), cancellationToken);
        }
        await connector.CompleteAsync(context, cancellationToken);
    }

    private static async Task WriteCsvSourcesAsync(UtilityFixture fixture, string root, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(root);
        foreach (var entity in fixture.Entities.Where(entity => entity.SourceConnector == "csv"))
        {
            var path = Path.Combine(root, entity.SourceLocation);
            await using var writer = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan), new UTF8Encoding(false));
            await writer.WriteLineAsync(string.Join(';', entity.SourceFields.Select(EscapeCsv))).ConfigureAwait(false);
            foreach (var row in entity.Rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await writer.WriteLineAsync(string.Join(';', entity.SourceFields.Select(field => EscapeCsv(row[field]))))
                    .ConfigureAwait(false);
            }
        }
    }

    private static async Task WriteSourceFilesAsync(UtilityFixture fixture, string root, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(root);
        foreach (var document in fixture.Documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.Combine(root, document.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, document.Content, cancellationToken).ConfigureAwait(false);
            File.SetLastWriteTimeUtc(path, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            Assert.Equal(document.Sha256, Sha(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false)));
        }
    }

    private static async Task SeedSqlSourcesAsync(string connectionString, UtilityFixture fixture,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        foreach (var entity in fixture.Entities.Where(entity => entity.SourceConnector == "sqlserver"))
        {
            var table = entity.SourceLocation["dbo.".Length..];
            await using (var create = connection.CreateCommand())
            {
                create.CommandText = $"CREATE TABLE dbo.[{table}] ({string.Join(", ", entity.SourceFields.Select(field =>
                    $"[{field}] {SqlType(field)} {(entity.IdentityFields.Contains(field, StringComparer.Ordinal) ? "NOT NULL" : "NULL")}"))})";
                await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            var tableData = new DataTable { Locale = CultureInfo.InvariantCulture };
            foreach (var field in entity.SourceFields) tableData.Columns.Add(field, SqlClrType(field));
            foreach (var row in entity.Rows)
            {
                var values = entity.SourceFields.Select(field => SqlValue(field, row[field])).ToArray();
                tableData.Rows.Add(values);
            }

            using var bulkCopy = new SqlBulkCopy(connection) { DestinationTableName = $"[dbo].[{table}]", BatchSize = 8192 };
            foreach (var field in entity.SourceFields) bulkCopy.ColumnMappings.Add(field, field);
            await bulkCopy.WriteToServerAsync(tableData, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string SqlType(string field) => field switch
    {
        "effective_from" => "date",
        "read_at" => "datetime2(7)",
        "read_sequence" => "int",
        "usage" => "decimal(18,3)",
        "invoice_total" or "payment_amount" or "adjustment_amount" => "decimal(18,2)",
        _ => "nvarchar(256)"
    };

    private static Type SqlClrType(string field) => field switch
    {
        "effective_from" or "read_at" => typeof(DateTime),
        "read_sequence" => typeof(int),
        "usage" or "invoice_total" or "payment_amount" or "adjustment_amount" => typeof(decimal),
        _ => typeof(string)
    };

    private static object SqlValue(string field, string value) => field switch
    {
        "effective_from" => DateTime.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None),
        "read_at" => DateTime.SpecifyKind(DateTime.ParseExact(value, "yyyy-MM-dd'T'HH:mm:ss",
            CultureInfo.InvariantCulture, DateTimeStyles.None), DateTimeKind.Unspecified),
        "read_sequence" => int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture),
        "usage" or "invoice_total" or "payment_amount" or "adjustment_amount" =>
            decimal.Parse(value, NumberStyles.Number, CultureInfo.InvariantCulture),
        _ => value
    };

    private static string EscapeCsv(string value) => value.IndexOfAny([';', '"', '\r', '\n']) < 0
        ? value : '"' + value.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';

    private static Dictionary<string, List<Dictionary<string, string>>> BuildTargetRows(UtilityFixture fixture) =>
        fixture.Entities.ToDictionary(entity => entity.TargetNode, entity => entity.Rows.Select(row =>
            entity.FieldMappings.ToDictionary(mapping => mapping.Target,
                mapping => mapping.Codes is not null && mapping.Codes.TryGetValue(row[mapping.Source], out var mapped)
                    ? mapped : row[mapping.Source], StringComparer.Ordinal)).ToList(), StringComparer.Ordinal);

    private static Dictionary<string, List<Dictionary<string, string>>> CloneRows(
        IReadOnlyDictionary<string, List<Dictionary<string, string>>> rows) =>
        rows.ToDictionary(pair => pair.Key, pair => pair.Value.Select(row =>
            new Dictionary<string, string>(row, StringComparer.Ordinal)).ToList(), StringComparer.Ordinal);

    private static void ApplyDefects(Dictionary<string, List<Dictionary<string, string>>> rows)
    {
        rows["target-account"].RemoveAll(row => row["account_id"] == "A000004");
        rows["target-customer"].Add(new Dictionary<string, string>(
            rows["target-customer"].Single(row => row["customer_id"] == "C000001"), StringComparer.Ordinal));
        Find(rows, "target-service-point", "service_point_id", "SP000005")["account_id"] = "A-GHOST";
        Find(rows, "target-meter", "meter_id", "M000006")["service_point_id"] = "SP000001";
        var firstRead = Find(rows, "target-meter-read", "read_id", "RD000003-01");
        var secondRead = Find(rows, "target-meter-read", "read_id", "RD000003-02");
        (firstRead["read_at"], secondRead["read_at"]) = (secondRead["read_at"], firstRead["read_at"]);
        Find(rows, "target-meter-read", "read_id", "RD000004-02")["usage"] = "99.125";
        Find(rows, "target-rate-assignment", "account_id", "A000005")["rate_code"] = "UNMAPPED";
        Find(rows, "target-invoice", "invoice_id", "INV000006")["invoice_total"] = "0.01";
        Find(rows, "target-payment", "payment_id", "PAY000007")["account_id"] = "A000008";
        Find(rows, "target-adjustment", "adjustment_id", "ADJ000008")["adjustment_amount"] = "99.99";
        Find(rows, "target-document", "document_id", "DOC000001")["account_id"] = "A000002";
        rows["target-customer"].Add(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["customer_id"] = "C-UNEXPECTED",
            ["name"] = "Synthetic Unexpected Customer"
        });
    }

    private static Dictionary<string, string> Find(Dictionary<string, List<Dictionary<string, string>>> rows,
        string node, string field, string value) => rows[node].Single(row => row[field] == value);

    private static UtilityEntity SqlEntity(string table, string sourceNode, string targetNode, string semanticType,
        string[] identityFields, string[] sourceFields, IReadOnlyList<Dictionary<string, string>> rows,
        FieldMap[] fieldMappings) => new("sqlserver", $"dbo.{table}", sourceNode, targetNode, semanticType,
            $"utility_{ToSnake(table)}", identityFields, sourceFields, fieldMappings, rows);

    private static UtilityEntity CsvEntity(string sourceNode, string targetNode, string semanticType, string path,
        string[] identityFields, string[] sourceFields, IReadOnlyList<Dictionary<string, string>> rows,
        FieldMap[] fieldMappings) => new("csv", path, sourceNode, targetNode, semanticType,
            $"utility_{ToSnake(targetNode[7..])}", identityFields, sourceFields, fieldMappings, rows);

    private static FieldMap Map(string target, string? source = null, params (string Source, string Target)[] codes) =>
        new(target, source ?? target, codes.Length == 0 ? null : codes.ToDictionary(pair => pair.Source, pair => pair.Target, StringComparer.Ordinal));

    private static Dictionary<string, string> Row(params (string Key, string Value)[] fields) =>
        fields.ToDictionary(field => field.Key, field => field.Value, StringComparer.Ordinal);

    private static string ToSnake(string value)
    {
        value = value.Replace('-', '_');
        var result = new StringBuilder();
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsUpper(character) && index > 0) result.Append('_');
            result.Append(char.ToLowerInvariant(character));
        }
        return result.ToString();
    }

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static MigrationNodeId NodeId(string name) => new(StableGuid("node:" + name));
    private static MigrationEdgeId EdgeId(string name) => new(StableGuid("edge:" + name));

    private static Guid StableGuid(string value) => new(SHA256.HashData(Encoding.UTF8.GetBytes(value))[..16]);

    private static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private sealed record FieldMap(string Target, string Source, IReadOnlyDictionary<string, string>? Codes);
    private sealed record UtilityEntity(string SourceConnector, string SourceLocation, string SourceNode, string TargetNode,
        string SemanticType, string TargetTable, string[] IdentityFields, string[] SourceFields,
        FieldMap[] FieldMappings, IReadOnlyList<Dictionary<string, string>> Rows)
    {
        public string[] TargetFields => FieldMappings.Select(mapping => mapping.Target).ToArray();
        public string[] TargetIdentityFields => IdentityFields.Select(identity =>
            FieldMappings.Single(mapping => mapping.Source == identity).Target).ToArray();
    }
    private sealed record UtilityDocument(string DocumentId, string AccountId, string RelativePath, byte[] Content, string Sha256);
    private sealed record UtilityFixture(IReadOnlyList<UtilityEntity> Entities, IReadOnlyList<UtilityDocument> Documents);
    private sealed record UtilityDefect(string Id, string Category, string SemanticConcept, string ExpectedRuleId);
    private sealed class TemporaryDirectoryCleanup(string path) : IDisposable
    {
        public void Dispose()
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
    }

    private sealed class UtilityEnvironmentProvider(IReadOnlyDictionary<string, string> values) : IEnvironmentVariableProvider
    {
        public string? GetValue(string name) => values.GetValueOrDefault(name);
    }
}