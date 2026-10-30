using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ProofShift.Cli;
using ProofShift.Configuration;
using ProofShift.Connectors.Abstractions;
using ProofShift.Domain;
using ProofShift.Evidence;
using ProofShift.Engine;
using ProofShift.Graph;
using ProofShift.Projection;
using ProofShift.Snapshots;
using ProofShift.Verification;
using YamlDotNet.RepresentationModel;
using Xunit;

namespace ProofShift.EndToEnd.Tests;

public sealed class AuthoringCliTests
{
    [Fact]
    public async Task ExternalVerificationRequiresCheckpointAndObservationSelectors()
    {
        var missingCheckpoint = await RunAsync("verify-external", "missing.yaml", "--observation", "trial-001");
        Assert.Equal(2, missingCheckpoint.ExitCode);
        Assert.Contains("verify-external", missingCheckpoint.Error, StringComparison.Ordinal);

        var missingObservation = await RunAsync("verify-external", "missing.yaml", "--checkpoint", "checkpoint-id");
        Assert.Equal(2, missingObservation.ExitCode);
        Assert.Contains("--observation", missingObservation.Error, StringComparison.Ordinal);

        var invalidRun = await RunAsync("report", "missing.yaml", "--verification-run", "not-a-guid");
        Assert.Equal(2, invalidRun.ExitCode);
        Assert.Contains("GUID", invalidRun.Error, StringComparison.Ordinal);
    }

    private static readonly string[] ExpectedPackIds =
        ["proofshift.fhir", "proofshift.hcm", "proofshift.justice", "proofshift.pension", "proofshift.utility"];

    [Theory]
    [InlineData("generic")]
    [InlineData("pension")]
    public async Task InitCreatesCredentialFreeStrictlyValidProjectAndRefusesOverwrite(string template)
    {
        var root = Path.Combine(Path.GetTempPath(), $"proofshift-authoring-{Guid.NewGuid():N}");
        try
        {
            var init = await RunAsync("init", root, "--template", template);
            Assert.True(init.ExitCode == 0, init.Error);
            var path = Path.Combine(root, "proofshift.yaml");
            var original = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
            var validation = await RunAsync("validate", path, "--strict", "--json");
            Assert.True(validation.ExitCode == 0, validation.Output + validation.Error);
            using var document = JsonDocument.Parse(validation.Output);
            Assert.True(document.RootElement.GetProperty("valid").GetBoolean());
            Assert.Empty(document.RootElement.GetProperty("issues").EnumerateArray());
            var target = await File.ReadAllTextAsync(Path.Combine(root, "systems", "target.yaml"), TestContext.Current.CancellationToken);
            Assert.Contains("PROOFSHIFT_TARGET_CONNECTION", target, StringComparison.Ordinal);
            Assert.DoesNotContain("Password=", target, StringComparison.OrdinalIgnoreCase);
            var discoveryPath = Path.Combine(root, "discovery", "source");
            var discovered = await RunAsync("discover", path, "--system", "source", "--endpoint", "records", "--output", discoveryPath, "--json");
            Assert.True(discovered.ExitCode == 0, discovered.Error);
            using var physical = JsonDocument.Parse(discovered.Output);
            Assert.Equal("csv", physical.RootElement.GetProperty("connectorId").GetString());
            Assert.True(File.Exists(Path.Combine(discoveryPath, "manifest.json")));
            var second = await RunAsync("init", root);
            Assert.Equal(1, second.ExitCode);
            Assert.Contains("PSAUTHOR011", second.Error, StringComparison.Ordinal);
            Assert.Equal(original, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("generic")]
    [InlineData("pension")]
    public async Task WorkspaceInitCreatesPortableLayoutAndValidationFailsClosedOnUnsafePaths(string template)
    {
        var root = Path.Combine(Path.GetTempPath(), $"proofshift-workspace-{Guid.NewGuid():N}");
        try
        {
            var init = await RunAsync("workspace", "init", root, "--TEMPLATE", template);
            Assert.Equal(0, init.ExitCode);
            Assert.Contains("Customer data: data/", init.Output, StringComparison.Ordinal);
            Assert.Contains("Evidence packages: evidence/", init.Output, StringComparison.Ordinal);

            var manifestPath = Path.Combine(root, "proofshift.workspace.json");
            var manifestText = await File.ReadAllTextAsync(manifestPath, TestContext.Current.CancellationToken);
            Assert.DoesNotContain("PROOFSHIFT_TARGET_CONNECTION", manifestText, StringComparison.Ordinal);
            using (var manifest = JsonDocument.Parse(manifestText))
            {
                var document = manifest.RootElement;
                Assert.Equal(1, document.GetProperty("formatVersion").GetInt32());
                Assert.NotEqual(Guid.Empty, document.GetProperty("workspaceId").GetGuid());
                Assert.Equal("proofshift.yaml", document.GetProperty("projectConfiguration").GetString());
                Assert.Equal("data", document.GetProperty("paths").GetProperty("customerData").GetString());
                Assert.Equal(".proofshift/tmp", document.GetProperty("paths").GetProperty("temporary").GetString());
            }

            foreach (var directory in new[] { "data", "discovery", "generated", "runs", "evidence", Path.Combine(".proofshift", "tmp") })
                Assert.True(Directory.Exists(Path.Combine(root, directory)), $"Expected workspace directory '{directory}'.");

            var validation = await RunAsync("workspace", "validate", root, "--json");
            Assert.Equal(0, validation.ExitCode);
            using (var result = JsonDocument.Parse(validation.Output))
            {
                Assert.True(result.RootElement.GetProperty("valid").GetBoolean());
                Assert.Empty(result.RootElement.GetProperty("issues").EnumerateArray());
            }
            Assert.Equal(manifestText, await File.ReadAllTextAsync(manifestPath, TestContext.Current.CancellationToken));

            var duplicate = await RunAsync("workspace", "init", root, "--template", template);
            Assert.Equal(1, duplicate.ExitCode);
            Assert.Contains("PSAUTHOR011", duplicate.Error, StringComparison.Ordinal);
            Assert.Equal(manifestText, await File.ReadAllTextAsync(manifestPath, TestContext.Current.CancellationToken));

            await File.WriteAllTextAsync(manifestPath, manifestText.Replace("\"proofshift.yaml\"", "\"../outside.yaml\"", StringComparison.Ordinal),
                TestContext.Current.CancellationToken);
            var unsafePath = await RunAsync("workspace", "validate", root, "--json");
            Assert.Equal(1, unsafePath.ExitCode);
            using var invalid = JsonDocument.Parse(unsafePath.Output);
            Assert.False(invalid.RootElement.GetProperty("valid").GetBoolean());
            Assert.Contains(invalid.RootElement.GetProperty("issues").EnumerateArray(),
                issue => issue.GetProperty("code").GetString() == "PSWORK014");
            var invalidPreflight = await RunAsync("workspace", "preflight", root, "--json");
            Assert.Equal(1, invalidPreflight.ExitCode);
            using (var invalidPreflightJson = JsonDocument.Parse(invalidPreflight.Output))
            {
                Assert.Equal("BLOCKED", invalidPreflightJson.RootElement.GetProperty("status").GetString());
                Assert.True(invalidPreflightJson.RootElement.GetProperty("completed").GetBoolean());
            }
            var invalidPlan = await RunAsync("workspace", "plan", root, "--json");
            Assert.Equal(1, invalidPlan.ExitCode);
            using (var invalidPlanJson = JsonDocument.Parse(invalidPlan.Output))
            {
                Assert.Equal("BLOCKED", invalidPlanJson.RootElement.GetProperty("status").GetString());
                Assert.Equal(JsonValueKind.Null, invalidPlanJson.RootElement.GetProperty("plan").ValueKind);
                Assert.Equal("Blocking", invalidPlanJson.RootElement.GetProperty("semanticDrift")
                    .GetProperty("status").GetString());
            }
            var invalidPlanText = await RunAsync("workspace", "plan", root);
            Assert.Equal(1, invalidPlanText.ExitCode);
            Assert.Contains("Status: Blocking", invalidPlanText.Output, StringComparison.Ordinal);

            var overlappingPaths = manifestText.Replace("\"evidence\": \"evidence\"", "\"evidence\": \"data\"", StringComparison.Ordinal);
            await File.WriteAllTextAsync(manifestPath, overlappingPaths, TestContext.Current.CancellationToken);
            var overlap = await RunAsync("workspace", "validate", root, "--json");
            Assert.Equal(1, overlap.ExitCode);
            using (var overlapResult = JsonDocument.Parse(overlap.Output))
                Assert.Contains(overlapResult.RootElement.GetProperty("issues").EnumerateArray(),
                    issue => issue.GetProperty("code").GetString() == "PSWORK007");

            var unsupportedVersion = manifestText.Replace("\"formatVersion\": 1", "\"formatVersion\": 99", StringComparison.Ordinal);
            await File.WriteAllTextAsync(manifestPath, unsupportedVersion, TestContext.Current.CancellationToken);
            var version = await RunAsync("workspace", "validate", root, "--json");
            Assert.Equal(1, version.ExitCode);
            using var versionResult = JsonDocument.Parse(version.Output);
            Assert.Contains(versionResult.RootElement.GetProperty("issues").EnumerateArray(),
                issue => issue.GetProperty("code").GetString() == "PSWORK004");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WorkspaceInitFromReusesConfigurationWithoutCopyingHistoryDataOrSecrets()
    {
        var sourceRoot = Path.Combine(Path.GetTempPath(), $"proofshift-workspace-reuse-source-{Guid.NewGuid():N}");
        var derivedRoot = Path.Combine(Path.GetTempPath(), $"proofshift-workspace-reuse-derived-{Guid.NewGuid():N}");
        var occupiedRoot = Path.Combine(Path.GetTempPath(), $"proofshift-workspace-reuse-occupied-{Guid.NewGuid():N}");
        const string resolvedSecret = "Password=C5-private-test-secret;User ID=operator";
        try
        {
            Assert.Equal(0, (await RunAsync("workspace", "init", sourceRoot, "--template", "generic")).ExitCode);
            var sourceRunId = await CreatePackageFixtureRunAsync(sourceRoot, "QUALIFIED", EvidenceResult.Pass);
            await WorkspaceEvidencePackageService.CreateAsync(sourceRoot, sourceRunId.ToString("D"), null,
                diagnostic: false, TestContext.Current.CancellationToken);
            var promotion = await RunAsync("workspace", "baseline", "promote", sourceRoot,
                sourceRunId.ToString("D"), "--json");
            Assert.Equal(0, promotion.ExitCode);
            await File.WriteAllTextAsync(Path.Combine(sourceRoot, "data", "private-source.csv"),
                "private-customer-record", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(sourceRoot, "discovery", "old-schema.json"),
                "historical discovery", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(sourceRoot, "generated", "review.json"),
                "old configuration review", TestContext.Current.CancellationToken);

            var graphPath = Path.Combine(sourceRoot, "migration", "graph.yaml");
            var rulePath = Path.Combine(sourceRoot, "rules", "rules.yaml");
            var graphBefore = await File.ReadAllBytesAsync(graphPath, TestContext.Current.CancellationToken);
            var rulesBefore = await File.ReadAllBytesAsync(rulePath, TestContext.Current.CancellationToken);
            var sourceManifest = await EvaluationWorkspaceManifestStore.ReadAsync(sourceRoot)
                ?? throw new InvalidDataException("Source workspace manifest is missing.");

            var derived = await RunWithEnvironmentAsync("PROOFSHIFT_TARGET_CONNECTION", resolvedSecret,
                "workspace", "init", derivedRoot, "--FROM", sourceRoot);
            Assert.Equal(0, derived.ExitCode);
            Assert.Contains("run history, baselines, reviews, Evidence, discovery, and customer data were not copied.",
                derived.Output, StringComparison.Ordinal);
            var destinationManifest = await EvaluationWorkspaceManifestStore.ReadAsync(derivedRoot)
                ?? throw new InvalidDataException("Derived workspace manifest is missing.");
            Assert.NotEqual(sourceManifest.WorkspaceId, destinationManifest.WorkspaceId);
            Assert.Equal(sourceManifest.ProjectConfiguration, destinationManifest.ProjectConfiguration);
            Assert.True((await EvaluationWorkspaceManifestStore.ValidateAsync(derivedRoot)).Valid);
            Assert.Equal(graphBefore, await File.ReadAllBytesAsync(Path.Combine(derivedRoot, "migration", "graph.yaml"),
                TestContext.Current.CancellationToken));
            Assert.Equal(rulesBefore, await File.ReadAllBytesAsync(Path.Combine(derivedRoot, "rules", "rules.yaml"),
                TestContext.Current.CancellationToken));
            Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(derivedRoot, "runs")));
            Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(derivedRoot, "evidence")));
            Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(derivedRoot, "discovery")));
            Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(derivedRoot, "generated")));
            Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(derivedRoot, "data")));
            Assert.False(File.Exists(Path.Combine(derivedRoot, ".proofshift", "baselines", "catalog.json")));
            Assert.False(Directory.Exists(Path.Combine(derivedRoot, ".proofshift", "reviews")));
            var derivedRunHistory = await RunAsync("workspace", "runs", derivedRoot, "--json");
            Assert.Equal(0, derivedRunHistory.ExitCode);
            Assert.Empty(JsonDocument.Parse(derivedRunHistory.Output).RootElement.EnumerateArray());
            foreach (var file in Directory.EnumerateFiles(derivedRoot, "*", SearchOption.AllDirectories))
                Assert.DoesNotContain(resolvedSecret,
                    await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken),
                    StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(sourceRoot, "data", "private-source.csv")));

            var overlappingRoot = Path.Combine(sourceRoot, "nested-destination");
            var overlap = await RunAsync("workspace", "init", overlappingRoot, "--from", sourceRoot);
            Assert.Equal(1, overlap.ExitCode);
            Assert.Contains("PSWORK017", overlap.Error, StringComparison.Ordinal);
            Assert.False(Directory.Exists(overlappingRoot));

            Directory.CreateDirectory(occupiedRoot);
            var protectedFile = Path.Combine(occupiedRoot, "keep.txt");
            await File.WriteAllTextAsync(protectedFile, "preserve", TestContext.Current.CancellationToken);
            var occupied = await RunAsync("workspace", "init", occupiedRoot, "--from", sourceRoot);
            Assert.Equal(1, occupied.ExitCode);
            Assert.Contains("PSWORK017", occupied.Error, StringComparison.Ordinal);
            Assert.Equal("preserve", await File.ReadAllTextAsync(protectedFile,
                TestContext.Current.CancellationToken));
        }
        finally
        {
            if (Directory.Exists(sourceRoot)) Directory.Delete(sourceRoot, recursive: true);
            if (Directory.Exists(derivedRoot)) Directory.Delete(derivedRoot, recursive: true);
            if (Directory.Exists(occupiedRoot)) Directory.Delete(occupiedRoot, recursive: true);
        }
    }

    [Fact]
    public async Task WorkspaceInitFromRejectsDestinationSymlink()
    {
        var sourceRoot = Path.Combine(Path.GetTempPath(), $"proofshift-workspace-reuse-link-source-{Guid.NewGuid():N}");
        var destinationRoot = Path.Combine(Path.GetTempPath(), $"proofshift-workspace-reuse-link-target-{Guid.NewGuid():N}");
        var destinationLink = Path.Combine(Path.GetTempPath(), $"proofshift-workspace-reuse-link-{Guid.NewGuid():N}");
        try
        {
            Assert.Equal(0, (await RunAsync("workspace", "init", sourceRoot)).ExitCode);
            Directory.CreateDirectory(destinationRoot);
            try
            {
                Directory.CreateSymbolicLink(destinationLink, destinationRoot);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or
                PlatformNotSupportedException or IOException)
            {
                Assert.Skip("Host does not permit symlink creation for workspace derivation coverage.");
            }

            var result = await RunAsync("workspace", "init", destinationLink, "--from", sourceRoot);
            Assert.Equal(1, result.ExitCode);
            Assert.Contains("PSWORK017", result.Error, StringComparison.Ordinal);
            Assert.Empty(Directory.EnumerateFileSystemEntries(destinationRoot));
        }
        finally
        {
            if (Directory.Exists(sourceRoot)) Directory.Delete(sourceRoot, recursive: true);
            if (Directory.Exists(destinationLink)) Directory.Delete(destinationLink);
            if (Directory.Exists(destinationRoot)) Directory.Delete(destinationRoot, recursive: true);
        }
    }

    [Fact]
    public async Task WorkspaceInitFromRejectsConfigurationSymlinkTraversal()
    {
        var sourceRoot = Path.Combine(Path.GetTempPath(), $"proofshift-workspace-reuse-source-link-{Guid.NewGuid():N}");
        var destinationRoot = Path.Combine(Path.GetTempPath(), $"proofshift-workspace-reuse-dest-link-{Guid.NewGuid():N}");
        var outsideConfiguration = Path.Combine(Path.GetTempPath(), $"proofshift-workspace-outside-{Guid.NewGuid():N}.yaml");
        var graphPath = Path.Combine(sourceRoot, "migration", "graph.yaml");
        var linkCreated = false;
        try
        {
            Assert.Equal(0, (await RunAsync("workspace", "init", sourceRoot)).ExitCode);
            File.Move(graphPath, outsideConfiguration);
            try
            {
                File.CreateSymbolicLink(graphPath, outsideConfiguration);
                linkCreated = true;
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or
                PlatformNotSupportedException or IOException)
            {
                Assert.Skip("Host does not permit symlink creation for workspace derivation coverage.");
            }

            var result = await RunAsync("workspace", "init", destinationRoot, "--from", sourceRoot);
            Assert.Equal(1, result.ExitCode);
            Assert.Contains("PSWORK017", result.Error, StringComparison.Ordinal);
            Assert.False(Directory.Exists(destinationRoot));
        }
        finally
        {
            if (linkCreated && File.Exists(graphPath)) File.Delete(graphPath);
            if (Directory.Exists(sourceRoot)) Directory.Delete(sourceRoot, recursive: true);
            if (Directory.Exists(destinationRoot)) Directory.Delete(destinationRoot, recursive: true);
            if (File.Exists(outsideConfiguration)) File.Delete(outsideConfiguration);
        }
    }

    [Fact]
    public async Task WorkspacePreflightDistinguishesStaticValidityFromReadinessAndPlanRedactsCredentials()
    {
        const string connectionSecret = "synthetic-connection-secret-value";
        var root = Path.Combine(Path.GetTempPath(), $"proofshift-workspace-preflight-{Guid.NewGuid():N}");
        try
        {
            var init = await RunAsync("workspace", "init", root);
            Assert.Equal(0, init.ExitCode);

            var validate = await RunWithEnvironmentAsync("PROOFSHIFT_TARGET_CONNECTION", connectionSecret,
                "workspace", "validate", root, "--json");
            Assert.Equal(0, validate.ExitCode);
            Assert.DoesNotContain(connectionSecret, validate.Output, StringComparison.Ordinal);

            var blocked = await RunWithEnvironmentAsync("PROOFSHIFT_TARGET_CONNECTION", null,
                "workspace", "preflight", root, "--json");
            Assert.Equal(1, blocked.ExitCode);
            using (var blockedJson = JsonDocument.Parse(blocked.Output))
            {
                var preflight = blockedJson.RootElement;
                Assert.Equal("BLOCKED", preflight.GetProperty("status").GetString());
                Assert.False(preflight.GetProperty("connectivityAttempted").GetBoolean());
                Assert.Contains(preflight.GetProperty("checks").EnumerateArray(),
                    check => check.GetProperty("id").GetString() == "PSPREF006" &&
                        check.GetProperty("status").GetString() == "BLOCKED");
                Assert.Contains(preflight.GetProperty("checks").EnumerateArray(),
                    check => check.GetProperty("category").GetString() == "connectivity" &&
                        check.GetProperty("status").GetString() == "NOT_CHECKED");
            }

            var ready = await RunWithEnvironmentAsync("PROOFSHIFT_TARGET_CONNECTION", connectionSecret,
                "workspace", "preflight", root, "--json");
            Assert.Equal(0, ready.ExitCode);
            Assert.DoesNotContain(connectionSecret, ready.Output, StringComparison.Ordinal);
            using (var readyJson = JsonDocument.Parse(ready.Output))
            {
                Assert.Equal("READY", readyJson.RootElement.GetProperty("status").GetString());
                Assert.False(readyJson.RootElement.GetProperty("connectivityAttempted").GetBoolean());
                Assert.Contains(readyJson.RootElement.GetProperty("checks").EnumerateArray(),
                    check => check.GetProperty("id").GetString() == "PSPREF008" &&
                        check.GetProperty("status").GetString() == "READY");
            }

            var plan = await RunWithEnvironmentAsync("PROOFSHIFT_TARGET_CONNECTION", connectionSecret,
                "workspace", "plan", root, "--json");
            var repeatedPlan = await RunWithEnvironmentAsync("PROOFSHIFT_TARGET_CONNECTION", connectionSecret,
                "workspace", "plan", root, "--json");
            Assert.Equal(0, plan.ExitCode);
            Assert.Equal(0, repeatedPlan.ExitCode);
            Assert.DoesNotContain(connectionSecret, plan.Output, StringComparison.Ordinal);
            using var planJson = JsonDocument.Parse(plan.Output);
            using var repeatedPlanJson = JsonDocument.Parse(repeatedPlan.Output);
            var planBody = planJson.RootElement.GetProperty("plan");
            var repeatedPlanBody = repeatedPlanJson.RootElement.GetProperty("plan");
            Assert.Equal(planBody.GetProperty("fingerprint").GetString(), repeatedPlanBody.GetProperty("fingerprint").GetString());
            Assert.Equal(64, planBody.GetProperty("verificationRuleSetFingerprint").GetString()?.Length);
            Assert.Equal(64, planBody.GetProperty("expectedExceptionsFingerprint").GetString()?.Length);
            Assert.Equal(2, planBody.GetProperty("nodes").GetArrayLength());
            Assert.Equal(1, planBody.GetProperty("edges").GetArrayLength());
            Assert.Equal(5, planBody.GetProperty("rules").GetArrayLength());
            Assert.Contains("never waive", planBody.GetProperty("expectedExceptionSemantics").GetString(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("checkpointId", plan.Output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("projectionFingerprint", plan.Output, StringComparison.OrdinalIgnoreCase);

            var capabilities = await RunAsync("capabilities", "--json");
            using var capabilityJson = JsonDocument.Parse(capabilities.Output);
            foreach (var system in planBody.GetProperty("systems").EnumerateArray())
            {
                var descriptor = capabilityJson.RootElement.GetProperty("connectors").EnumerateArray()
                    .Single(item => item.GetProperty("id").GetString() == system.GetProperty("connector").GetString());
                var expected = new List<string>();
                foreach (var (property, capability) in new[]
                {
                    ("discovery", "discovery"),
                    ("sourceRead", "sourceRead"),
                    ("checkpointCapture", "checkpointCapture"),
                    ("targetObservation", "targetObservation"),
                    ("shadowWrite", "shadowWrite"),
                    ("binaryStreaming", "binaryStreaming"),
                    ("schemaInspection", "schemaInspection"),
                    ("offlineReplay", "offlineReplay")
                })
                    if (descriptor.GetProperty(property).GetBoolean()) expected.Add(capability);
                Assert.Equal(expected, system.GetProperty("capabilities").EnumerateArray()
                    .Select(item => item.GetString()).ToArray());
            }

            var planChecks = planJson.RootElement.GetProperty("preflight").GetProperty("checks");
            foreach (var node in planBody.GetProperty("nodes").EnumerateArray())
            {
                var system = planBody.GetProperty("systems").EnumerateArray()
                    .Single(item => item.GetProperty("system").GetString() == node.GetProperty("system").GetString() &&
                        item.GetProperty("endpoint").GetString() == node.GetProperty("endpoint").GetString());
                var descriptor = capabilityJson.RootElement.GetProperty("connectors").EnumerateArray()
                    .Single(item => item.GetProperty("id").GetString() == system.GetProperty("connector").GetString());
                var required = node.GetProperty("type").GetString() == "Source"
                    ? new[] { "discovery", "sourceRead", "checkpointCapture" }
                    : system.GetProperty("role").GetString() == "ShadowTarget"
                        ? new[] { "targetObservation", "shadowWrite" }
                        : new[] { "targetObservation" };
                foreach (var capability in required)
                {
                    var supported = descriptor.GetProperty(capability).GetBoolean();
                    var check = planChecks.EnumerateArray().Single(item =>
                        item.GetProperty("category").GetString() == "capability" &&
                        item.GetProperty("component").GetString() == node.GetProperty("key").GetString() &&
                        item.GetProperty("capability").GetString() == capability);
                    Assert.Equal(supported ? "READY" : "BLOCKED", check.GetProperty("status").GetString());
                }
            }

            var humanPlan = await RunWithEnvironmentAsync("PROOFSHIFT_TARGET_CONNECTION", connectionSecret,
                "workspace", "plan", root);
            Assert.Equal(0, humanPlan.ExitCode);
            Assert.Contains("Graph nodes:", humanPlan.Output, StringComparison.Ordinal);
            Assert.Contains("Graph edges and mappings:", humanPlan.Output, StringComparison.Ordinal);
            Assert.Contains("Verification rules:", humanPlan.Output, StringComparison.Ordinal);
            Assert.Contains("Preflight checks:", humanPlan.Output, StringComparison.Ordinal);
            Assert.DoesNotContain(connectionSecret, humanPlan.Output, StringComparison.Ordinal);

            var rulesPath = Path.Combine(root, "rules", "rules.yaml");
            var rulesYaml = new YamlStream();
            rulesYaml.Load(new StringReader(await File.ReadAllTextAsync(rulesPath, TestContext.Current.CancellationToken)));
            var rulesRoot = (YamlMappingNode)rulesYaml.Documents[0].RootNode;
            var configuredRules = (YamlMappingNode)rulesRoot.Children[new YamlScalarNode("rules")];
            var attributesRule = (YamlMappingNode)configuredRules.Children[new YamlScalarNode("attributes")];
            attributesRule.Children[new YamlScalarNode("severity")] = new YamlScalarNode("warning");
            using (var writer = new StringWriter())
            {
                rulesYaml.Save(writer);
                await File.WriteAllTextAsync(rulesPath, writer.ToString(), TestContext.Current.CancellationToken);
            }
            var changedPlan = await RunWithEnvironmentAsync("PROOFSHIFT_TARGET_CONNECTION", connectionSecret,
                "workspace", "plan", root, "--json");
            Assert.Equal(0, changedPlan.ExitCode);
            using var changedPlanJson = JsonDocument.Parse(changedPlan.Output);
            Assert.NotEqual(planBody.GetProperty("fingerprint").GetString(),
                changedPlanJson.RootElement.GetProperty("plan").GetProperty("fingerprint").GetString());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WorkspacePlanReportsConfiguredDomainPackIdentity()
    {
        var root = Path.Combine(Path.GetTempPath(), $"proofshift-workspace-packs-{Guid.NewGuid():N}");
        try
        {
            var init = await RunAsync("workspace", "init", root, "--template", "pension");
            Assert.Equal(0, init.ExitCode);

            var result = await RunWithEnvironmentAsync("PROOFSHIFT_TARGET_CONNECTION", "synthetic-only",
                "workspace", "plan", root, "--json");
            Assert.Equal(0, result.ExitCode);
            using var json = JsonDocument.Parse(result.Output);
            var packs = json.RootElement.GetProperty("plan").GetProperty("packs").EnumerateArray().ToArray();
            var pack = Assert.Single(packs);
            Assert.Equal("pension", pack.GetProperty("id").GetString());
            Assert.Equal("0.9.0", pack.GetProperty("version").GetString());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WorkspacePreflightOnlyChecksConnectivityWhenExplicitlyRequested()
    {
        const string password = "preflight-password-must-not-leak";
        var connection = $"Host=127.0.0.1;Port=1;Database=ProofShift;Username=synthetic;Password={password};Timeout=1";
        var root = Path.Combine(Path.GetTempPath(), $"proofshift-workspace-connect-{Guid.NewGuid():N}");
        try
        {
            Assert.Equal(0, (await RunAsync("workspace", "init", root)).ExitCode);
            var sourcePath = Path.Combine(root, "data", "source.csv");
            var sourceHash = System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(sourcePath,
                TestContext.Current.CancellationToken));
            var result = await RunWithEnvironmentAsync("PROOFSHIFT_TARGET_CONNECTION", connection,
                "workspace", "preflight", root, "--connect", "--json");
            Assert.Equal(1, result.ExitCode);
            Assert.DoesNotContain(password, result.Output, StringComparison.Ordinal);
            using var json = JsonDocument.Parse(result.Output);
            Assert.Equal("BLOCKED", json.RootElement.GetProperty("status").GetString());
            Assert.True(json.RootElement.GetProperty("connectivityAttempted").GetBoolean());
            Assert.Contains(json.RootElement.GetProperty("checks").EnumerateArray(),
                check => check.GetProperty("category").GetString() == "connectivity" &&
                    check.GetProperty("status").GetString() == "BLOCKED");
            Assert.Equal(sourceHash, System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(sourcePath,
                TestContext.Current.CancellationToken)));
            Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(root, "runs")));
            Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(root, "evidence")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WorkspaceRunPersistsBlockedReadinessWithoutDispositionOrRuntimeArtifacts()
    {
        var root = Path.Combine(Path.GetTempPath(), $"proofshift-workspace-run-blocked-{Guid.NewGuid():N}");
        try
        {
            Assert.Equal(0, (await RunAsync("workspace", "init", root)).ExitCode);
            var sourcePath = Path.Combine(root, "data", "source.csv");
            var sourceHash = System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(sourcePath,
                TestContext.Current.CancellationToken));

            var result = await RunAsync("workspace", "run", root, "--json");

            Assert.Equal(1, result.ExitCode);
            var finalLine = result.Output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Last();
            using var json = JsonDocument.Parse(finalLine);
            Assert.Equal("PreflightBlocked", json.RootElement.GetProperty("state").GetString());
            Assert.Equal("PreflightBlocked", json.RootElement.GetProperty("run").GetProperty("state").GetString());
            Assert.True(json.RootElement.GetProperty("run").GetProperty("runId").GetGuid() != Guid.Empty);
            Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("run").GetProperty("disposition").ValueKind);
            Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("run").GetProperty("checkpointId").ValueKind);
            using var firstProgress = JsonDocument.Parse(result.Output.Split(Environment.NewLine,
                StringSplitOptions.RemoveEmptyEntries).First());
            Assert.Equal("progress", firstProgress.RootElement.GetProperty("type").GetString());
            Assert.Contains(json.RootElement.GetProperty("readinessChecks").EnumerateArray(),
                check => check.GetProperty("status").GetString() == "BLOCKED");

            var runManifestPath = Assert.Single(Directory.EnumerateFiles(Path.Combine(root, "runs"), "run.json",
                SearchOption.AllDirectories));
            using var persisted = JsonDocument.Parse(await File.ReadAllTextAsync(runManifestPath,
                TestContext.Current.CancellationToken));
            Assert.Equal("PreflightBlocked", persisted.RootElement.GetProperty("state").GetString());
            Assert.Equal("proofshift-workspace-run-v1", persisted.RootElement.GetProperty("format").GetString());
            Assert.Equal(json.RootElement.GetProperty("run").GetProperty("runId").GetGuid(),
                persisted.RootElement.GetProperty("runId").GetGuid());
            Assert.False(string.IsNullOrWhiteSpace(persisted.RootElement.GetProperty("identityFingerprint").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(persisted.RootElement.GetProperty("manifestHash").GetString()));
            Assert.Contains(persisted.RootElement.GetProperty("progress").EnumerateArray(),
                item => item.GetProperty("stage").GetString() == "readiness" &&
                    item.GetProperty("status").GetString() == "blocked");
            Assert.True(!persisted.RootElement.TryGetProperty("disposition", out var persistedDisposition) ||
                persistedDisposition.ValueKind == JsonValueKind.Null);
            Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(root, "evidence")));
            Assert.False(Directory.Exists(Path.Combine(root, ".proofshift", "checkpoints")));
            Assert.Equal(sourceHash, System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(sourcePath,
                TestContext.Current.CancellationToken)));

            var retry = await RunAsync("workspace", "run", root, "--json");
            Assert.Equal(1, retry.ExitCode);
            var retryJsonLine = retry.Output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Last();
            using var retryJson = JsonDocument.Parse(retryJsonLine);
            Assert.NotEqual(json.RootElement.GetProperty("run").GetProperty("runId").GetGuid(),
                retryJson.RootElement.GetProperty("run").GetProperty("runId").GetGuid());
            Assert.Equal(2, Directory.EnumerateFiles(Path.Combine(root, "runs"), "run.json",
                SearchOption.AllDirectories).Count());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CheckpointBackedProjectionContextDoesNotResolveSourceSecrets()
    {
        var root = Path.Combine(Path.GetTempPath(), $"proofshift-workspace-checkpoint-context-{Guid.NewGuid():N}");
        try
        {
            Assert.Equal(0, (await RunAsync("workspace", "init", root)).ExitCode);
            var sourcePath = Path.Combine(root, "systems", "source.yaml");
            var yaml = new YamlStream();
            yaml.Load(new StringReader(await File.ReadAllTextAsync(sourcePath, TestContext.Current.CancellationToken)));
            var source = (YamlMappingNode)yaml.Documents[0].RootNode;
            var storage = (YamlMappingNode)source.Children[new YamlScalarNode("storage")];
            var endpoint = (YamlMappingNode)storage.Children[new YamlScalarNode("records")];
            endpoint.Children[new YamlScalarNode("root")] = new YamlScalarNode("env:PROOFSHIFT_MISSING_REPLAY_ROOT");
            using (var writer = new StringWriter())
            {
                yaml.Save(writer);
                await File.WriteAllTextAsync(sourcePath, writer.ToString(), TestContext.Current.CancellationToken);
            }

            var loaded = await new ConfigurationLoader(requireEnvironmentValues: false)
                .LoadAsync(Path.Combine(root, "proofshift.yaml"), TestContext.Current.CancellationToken);
            Assert.True(loaded.IsValid, string.Join("; ", loaded.Issues.Select(issue => issue.Message)));
            var graph = MigrationGraphCompiler.Compile(loaded.Configuration!);
            Assert.True(graph.IsValid);
            var sourceNode = graph.Graph!.Nodes.Single(node => node.Type == MigrationNodeType.Source);
            var contextFactory = new RuntimeConnectorContextFactory(new MissingEnvironmentVariableProvider(), root);

            var replayContext = contextFactory.Create(loaded.Configuration!, sourceNode,
                resolveEnvironmentReferences: false);

            Assert.Equal("env:PROOFSHIFT_MISSING_REPLAY_ROOT",
                replayContext.Configuration.GetRequired("root").UseValue(value => value));
            Assert.Throws<ConnectorConfigurationException>(() => contextFactory.Create(loaded.Configuration!, sourceNode));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("QUALIFIED", EvidenceResult.Pass)]
    [InlineData("NOT QUALIFIED", EvidenceResult.Fail)]
    public async Task WorkspaceEvidencePackagesAreOfflineVerifiableForEitherDisposition(
        string disposition, EvidenceResult evidenceResult)
    {
        var root = Path.Combine(Path.GetTempPath(), $"proofshift-evidence-package-{Guid.NewGuid():N}");
        try
        {
            Assert.Equal(0, (await RunAsync("workspace", "init", root)).ExitCode);
            await File.WriteAllTextAsync(Path.Combine(root, "data", "private-input.txt"),
                "never-copy-this-secret-token-123", TestContext.Current.CancellationToken);
            var run = await CreatePackageFixtureRunAsync(root, disposition, evidenceResult);
            var packagePath = Path.Combine(root, "evidence", $"{run:D}.zip");

            var created = await WorkspaceEvidencePackageService.CreateAsync(root, run.ToString("D"),
                packagePath, diagnostic: false, TestContext.Current.CancellationToken);
            var validated = await WorkspaceEvidencePackageService.ValidateAsync(packagePath,
                TestContext.Current.CancellationToken);

            Assert.Equal("final", created.PackageType);
            Assert.Equal(disposition, created.Disposition);
            Assert.True(validated.Valid, string.Join("; ", validated.Failures));
            Assert.Equal(run, validated.RunId);
            Assert.Equal(created.PackageIdentity, validated.PackageIdentity);
            Assert.True(created.EmbeddedArtifactCount >= 4);
            Assert.True(created.ExternalReferenceCount > 0);
            Assert.True(created.OmittedArtifactCount > 0);
            Assert.Equal(disposition, validated.Disposition);

            using var archive = ZipFile.OpenRead(packagePath);
            var summary = Assert.Single(archive.Entries,
                entry => entry.FullName == WorkspaceEvaluationSummaryService.SummaryPath);
            var summaryText = ReadEntryText(summary);
            Assert.Contains($"Run: `{run:D}`", summaryText, StringComparison.Ordinal);
            Assert.Contains("not a guarantee of production migration safety", summaryText, StringComparison.Ordinal);
            var evidence = Assert.Single(archive.Entries,
                entry => entry.FullName == "artifacts/evidence/evidence.ndjson");
            using var reader = new StreamReader(evidence.Open());
            var evidenceText = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
            Assert.Contains("source-artifact-1", evidenceText, StringComparison.Ordinal);
            if (disposition == "NOT QUALIFIED")
                Assert.Contains("proofshift.verification.materialized-state", evidenceText, StringComparison.Ordinal);
            Assert.DoesNotContain("never-copy-this-secret-token-123", string.Join("",
                archive.Entries.Select(ReadEntryText)));
            var projectionJournal = Assert.Single(archive.Entries,
                entry => entry.FullName == "artifacts/projection/journal.ndjson");
            var journalText = ReadEntryText(projectionJournal);
            Assert.Contains("target-artifact-a", journalText, StringComparison.Ordinal);
            Assert.Contains("target-artifact-b", journalText, StringComparison.Ordinal);
            var embeddedRun = Assert.Single(archive.Entries, entry => entry.FullName == "artifacts/run.json");
            using var embeddedRunJson = JsonDocument.Parse(await ReadEntryAsync(embeddedRun));
            Assert.True(embeddedRunJson.RootElement.GetProperty("replayedCheckpoint").GetBoolean());

            var repeatedPath = Path.Combine(root, "evidence", $"{run:D}-copy.zip");
            var repeated = await WorkspaceEvidencePackageService.CreateAsync(root, run.ToString("D"),
                repeatedPath, diagnostic: false, TestContext.Current.CancellationToken);
            Assert.Equal(created.PackageIdentity, repeated.PackageIdentity);

            var cliPackagePath = Path.Combine(root, "evidence", $"{run:D}-cli.zip");
            var cliCreate = await RunAsync("workspace", "evidence", "package", root, run.ToString("D"),
                "--output", cliPackagePath, "--json");
            Assert.Equal(0, cliCreate.ExitCode);
            Assert.DoesNotContain("never-copy-this-secret-token-123", cliCreate.Output, StringComparison.Ordinal);
            using var cliCreatedJson = JsonDocument.Parse(cliCreate.Output);
            Assert.Equal(created.PackageIdentity, cliCreatedJson.RootElement.GetProperty("packageIdentity").GetString());
            var cliValidate = await RunAsync("workspace", "evidence", "validate-package", cliPackagePath, "--json");
            Assert.Equal(0, cliValidate.ExitCode);
            using var cliValidatedJson = JsonDocument.Parse(cliValidate.Output);
            Assert.True(cliValidatedJson.RootElement.GetProperty("valid").GetBoolean());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WorkspaceBaselinePromotionRetainsImmutableHistoryAndExposesActiveBaseline()
    {
        var root = Path.Combine(Path.GetTempPath(), $"proofshift-workspace-baseline-{Guid.NewGuid():N}");
        try
        {
            Assert.Equal(0, (await RunAsync("workspace", "init", root)).ExitCode);
            var emptyHistory = await RunAsync("workspace", "baseline", "list", root, "--json");
            Assert.Equal(0, emptyHistory.ExitCode);
            Assert.Empty(JsonDocument.Parse(emptyHistory.Output).RootElement.EnumerateArray());
            var emptyActive = await RunAsync("workspace", "baseline", "active", root, "--json");
            Assert.Equal(0, emptyActive.ExitCode);
            Assert.Equal(JsonValueKind.Null, JsonDocument.Parse(emptyActive.Output).RootElement.ValueKind);

            var qualifiedRun = await CreatePackageFixtureRunAsync(root, "QUALIFIED", EvidenceResult.Pass);
            await WorkspaceEvidencePackageService.CreateAsync(root, qualifiedRun.ToString("D"), null,
                diagnostic: false, TestContext.Current.CancellationToken);
            var firstPromotion = await RunAsync("workspace", "baseline", "promote", root,
                qualifiedRun.ToString("D"), "--json");
            Assert.Equal(0, firstPromotion.ExitCode);
            using var firstJson = JsonDocument.Parse(firstPromotion.Output);
            var firstBaselineId = firstJson.RootElement.GetProperty("baselineId").GetGuid();
            Assert.Equal("QUALIFIED", firstJson.RootElement.GetProperty("disposition").GetString());
            Assert.Equal(64, firstJson.RootElement.GetProperty("runManifestIdentityFingerprint").GetString()!.Length);
            Assert.Equal(64, firstJson.RootElement.GetProperty("runManifestHash").GetString()!.Length);
            Assert.Equal(64, firstJson.RootElement.GetProperty("effectivePlanFingerprint").GetString()!.Length);
            Assert.Equal(64, firstJson.RootElement.GetProperty("configurationHash").GetString()!.Length);
            Assert.Equal(64, firstJson.RootElement.GetProperty("graphHash").GetString()!.Length);
            Assert.Equal(64, firstJson.RootElement.GetProperty("evidencePackageIdentity").GetString()!.Length);
            Assert.Equal(64, firstJson.RootElement.GetProperty("recordHash").GetString()!.Length);
            Assert.Equal("0.1.0-preview.1", firstJson.RootElement.GetProperty("productVersion").GetString());
            Assert.Equal(64, firstJson.RootElement.GetProperty("reconciliationFingerprint").GetString()!.Length);
            Assert.False(string.IsNullOrWhiteSpace(firstJson.RootElement.GetProperty("checkpointId").GetString()));
            Assert.Equal(64, firstJson.RootElement.GetProperty("checkpointManifestHash").GetString()!.Length);
            Assert.Equal(64, firstJson.RootElement.GetProperty("checkpointSourceFingerprint").GetString()!.Length);
            Assert.False(string.IsNullOrWhiteSpace(firstJson.RootElement.GetProperty("verificationRunId").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(firstJson.RootElement.GetProperty("verificationOutcome").GetString()));
            Assert.Equal(64, firstJson.RootElement.GetProperty("verificationEvidenceFingerprint").GetString()!.Length);
            Assert.Equal(64, firstJson.RootElement.GetProperty("verificationLedgerFingerprint").GetString()!.Length);
            Assert.Equal(Path.Combine("evidence", $"{qualifiedRun:D}.zip").Replace('\\', '/'),
                firstJson.RootElement.GetProperty("evidencePackageRelativePath").GetString());
            Assert.Equal(JsonValueKind.Array,
                firstJson.RootElement.GetProperty("discoveryFingerprints").ValueKind);
            var firstBaselinePath = Path.Combine(root, ".proofshift", "baselines",
                $"{firstBaselineId:D}.json");
            var originalFirstRecord = await File.ReadAllBytesAsync(firstBaselinePath,
                TestContext.Current.CancellationToken);

            var notQualifiedRun = await CreatePackageFixtureRunAsync(root, "NOT QUALIFIED",
                EvidenceResult.Fail, detailedFindings: true, evidenceValue: "sensitive-fixture-value",
                exceptionReason: "sensitive-fixture-reason");
            await WorkspaceEvidencePackageService.CreateAsync(root, notQualifiedRun.ToString("D"), null,
                diagnostic: false, TestContext.Current.CancellationToken);
            var secondPromotion = await RunAsync("workspace", "baseline", "promote", root,
                notQualifiedRun.ToString("D"), "--json");
            Assert.Equal(0, secondPromotion.ExitCode);
            using var secondJson = JsonDocument.Parse(secondPromotion.Output);
            var secondBaselineId = secondJson.RootElement.GetProperty("baselineId").GetGuid();
            Assert.Equal("NOT QUALIFIED", secondJson.RootElement.GetProperty("disposition").GetString());
            Assert.Equal(firstBaselineId,
                secondJson.RootElement.GetProperty("previousActiveBaselineId").GetGuid());

            Assert.Equal(originalFirstRecord, await File.ReadAllBytesAsync(firstBaselinePath,
                TestContext.Current.CancellationToken));
            var secondBaselineText = await File.ReadAllTextAsync(
                Path.Combine(root, ".proofshift", "baselines", $"{secondBaselineId:D}.json"),
                TestContext.Current.CancellationToken);
            Assert.DoesNotContain("sensitive-fixture-value", secondBaselineText, StringComparison.Ordinal);
            Assert.DoesNotContain("sensitive-fixture-reason", secondBaselineText, StringComparison.Ordinal);

            var history = await RunAsync("workspace", "baseline", "list", root, "--json");
            Assert.Equal(0, history.ExitCode);
            using (var historyJson = JsonDocument.Parse(history.Output))
            {
                var records = historyJson.RootElement.EnumerateArray().ToArray();
                Assert.Equal(2, records.Length);
                Assert.Equal(firstBaselineId, records[0].GetProperty("baselineId").GetGuid());
                Assert.Equal(secondBaselineId, records[1].GetProperty("baselineId").GetGuid());
            }

            var shown = await RunAsync("workspace", "baseline", "show", root,
                firstBaselineId.ToString("D"), "--json");
            Assert.Equal(0, shown.ExitCode);
            using var shownJson = JsonDocument.Parse(shown.Output);
            Assert.Equal("QUALIFIED", shownJson.RootElement.GetProperty("disposition").GetString());

            var active = await RunAsync("workspace", "baseline", "active", root, "--json");
            Assert.Equal(0, active.ExitCode);
            using var activeJson = JsonDocument.Parse(active.Output);
            Assert.Equal(secondBaselineId, activeJson.RootElement.GetProperty("baselineId").GetGuid());
            Assert.Equal("NOT QUALIFIED", activeJson.RootElement.GetProperty("disposition").GetString());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WorkspacePilotHistorySupportsExplicitNotQualifiedProgressionAndRebuildableRunListing()
    {
        var root = Path.Combine(Path.GetTempPath(), $"proofshift-workspace-pilot-history-{Guid.NewGuid():N}");
        try
        {
            Assert.Equal(0, (await RunAsync("workspace", "init", root)).ExitCode);
            var baselineARunId = await CreatePackageFixtureRunAsync(root, "NOT QUALIFIED",
                EvidenceResult.Fail, detailedFindings: true);
            var baselineAPackage = await WorkspaceEvidencePackageService.CreateAsync(root,
                baselineARunId.ToString("D"), null, diagnostic: false, TestContext.Current.CancellationToken);
            var baselineAPackageHash = SHA256.HashData(
                await File.ReadAllBytesAsync(baselineAPackage.PackagePath, TestContext.Current.CancellationToken));
            var promotionA = await RunAsync("workspace", "baseline", "promote", root,
                baselineARunId.ToString("D"), "--json");
            Assert.Equal(0, promotionA.ExitCode);
            var baselineAId = JsonDocument.Parse(promotionA.Output).RootElement.GetProperty("baselineId").GetGuid();
            var baselineABytes = await File.ReadAllBytesAsync(
                WorkspaceBaselineService.GetBaselineRecordPath(root, baselineAId),
                TestContext.Current.CancellationToken);
            var baselineA = await WorkspaceBaselineService.ShowAsync(root, baselineAId.ToString("D"),
                TestContext.Current.CancellationToken);

            var candidateB = await CreateBoundCandidateAsync(baselineA, "NOT QUALIFIED", EvidenceResult.Fail,
                detailedFindings: false);
            Assert.Equal("StillNotQualified", candidateB.Comparison.OutcomeClassification);
            Assert.Contains(candidateB.Comparison.DetailedComparison.Findings,
                item => item.Category == "Resolved");
            var summaryB = await WorkspaceEvaluationSummaryService.CreateAsync(root,
                candidateB.RunId.ToString("D"), null, TestContext.Current.CancellationToken);
            var summaryTextB = await File.ReadAllTextAsync(summaryB.SummaryPath,
                TestContext.Current.CancellationToken);
            Assert.Contains("Post-run outcome: **StillNotQualified**", summaryTextB, StringComparison.Ordinal);
            Assert.Contains("Finding resolved:", summaryTextB, StringComparison.Ordinal);

            var candidateBPackage = Path.Combine(root, "evidence", $"{candidateB.RunId:D}.zip");
            var candidateBPackageHash = SHA256.HashData(
                await File.ReadAllBytesAsync(candidateBPackage, TestContext.Current.CancellationToken));
            var promotionB = await RunAsync("workspace", "baseline", "promote", root,
                candidateB.RunId.ToString("D"), "--json");
            Assert.Equal(0, promotionB.ExitCode);
            using (var promotionBJson = JsonDocument.Parse(promotionB.Output))
            {
                var record = promotionBJson.RootElement;
                Assert.Equal(baselineAId, record.GetProperty("previousActiveBaselineId").GetGuid());
                Assert.Equal(baselineAId, record.GetProperty("promotionComparisonBaselineId").GetGuid());
                Assert.Equal("StillNotQualified",
                    record.GetProperty("promotionComparisonOutcomeClassification").GetString());
                Assert.Equal("NOT QUALIFIED", record.GetProperty("disposition").GetString());
            }
            var baselineBId = JsonDocument.Parse(promotionB.Output).RootElement.GetProperty("baselineId").GetGuid();
            Assert.Equal(baselineABytes, await File.ReadAllBytesAsync(
                WorkspaceBaselineService.GetBaselineRecordPath(root, baselineAId),
                TestContext.Current.CancellationToken));
            Assert.Equal(baselineAPackageHash, SHA256.HashData(await File.ReadAllBytesAsync(
                baselineAPackage.PackagePath, TestContext.Current.CancellationToken)));
            Assert.Equal(candidateBPackageHash, SHA256.HashData(await File.ReadAllBytesAsync(
                candidateBPackage, TestContext.Current.CancellationToken)));

            var duplicateActive = await RunAsync("workspace", "baseline", "promote", root,
                candidateB.RunId.ToString("D"), "--json");
            Assert.Equal(0, duplicateActive.ExitCode);
            Assert.Equal("AlreadyActive",
                JsonDocument.Parse(duplicateActive.Output).RootElement.GetProperty("promotionStatus").GetString());
            var duplicateHistorical = await RunAsync("workspace", "baseline", "promote", root,
                baselineARunId.ToString("D"), "--json");
            Assert.Equal(1, duplicateHistorical.ExitCode);
            Assert.Contains("PSWORKBASE098", duplicateHistorical.Error, StringComparison.Ordinal);
            Assert.Contains("already represented by a historical baseline", duplicateHistorical.Error,
                StringComparison.Ordinal);
            Assert.Equal(2, (await WorkspaceBaselineService.ListAsync(root,
                TestContext.Current.CancellationToken)).Count);

            var baselineB = await WorkspaceBaselineService.ShowAsync(root, baselineBId.ToString("D"),
                TestContext.Current.CancellationToken);
            var candidateC = await CreateBoundCandidateAsync(baselineB, "QUALIFIED", EvidenceResult.Pass,
                detailedFindings: false);
            Assert.Equal("Improved", candidateC.Comparison.OutcomeClassification);
            Assert.Contains(candidateC.Comparison.DetailedComparison.Findings,
                item => item.Category == "Resolved");

            var runs = await RunAsync("workspace", "runs", root, "--json");
            Assert.Equal(0, runs.ExitCode);
            using (var runHistory = JsonDocument.Parse(runs.Output))
            {
                var records = runHistory.RootElement.EnumerateArray().ToArray();
                var historicalCandidate = records.Single(item =>
                    item.GetProperty("runId").GetGuid() == candidateB.RunId);
                Assert.True(historicalCandidate.GetProperty("trusted").GetBoolean());
                Assert.Equal(baselineAId, historicalCandidate.GetProperty("baselineId").GetGuid());
                Assert.Equal("StillNotQualified",
                    historicalCandidate.GetProperty("outcomeClassification").GetString());
                Assert.Equal("Valid", historicalCandidate.GetProperty("packageStatus").GetString());
                Assert.Equal("Valid", historicalCandidate.GetProperty("comparisonStatus").GetString());
                Assert.True(records.Single(item => item.GetProperty("runId").GetGuid() == candidateC.RunId)
                    .GetProperty("trusted").GetBoolean());
            }

            var corruptRunDirectory = Path.Combine(root, "runs", Guid.NewGuid().ToString("D"));
            Directory.CreateDirectory(corruptRunDirectory);
            await File.WriteAllTextAsync(Path.Combine(corruptRunDirectory, "run.json"), "{}",
                TestContext.Current.CancellationToken);
            var historyWithInvalid = await RunAsync("workspace", "runs", root, "--json");
            Assert.Equal(0, historyWithInvalid.ExitCode);
            using var invalidRows = JsonDocument.Parse(historyWithInvalid.Output);
            var invalid = invalidRows.RootElement.EnumerateArray().Single(item =>
                item.GetProperty("directoryName").GetString() == Path.GetFileName(corruptRunDirectory));
            Assert.False(invalid.GetProperty("trusted").GetBoolean());
            Assert.Equal("Invalid", invalid.GetProperty("validationStatus").GetString());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }

        async Task<(Guid RunId, WorkspaceBaselineComparisonRecord Comparison)> CreateBoundCandidateAsync(
            WorkspaceBaselineRecord baseline, string disposition, EvidenceResult evidenceResult,
            bool detailedFindings, IReadOnlyList<string>? discoveryFingerprints = null)
        {
            var runId = await CreatePackageFixtureRunAsync(root, disposition, evidenceResult, detailedFindings,
                discoveryFingerprints: discoveryFingerprints);
            var plan = await WorkspaceEvaluationService.PlanAsync(root, TestContext.Current.CancellationToken);
            var drift = await WorkspaceSemanticDriftService.CompareAsync(root, plan,
                baseline.BaselineId.ToString("D"), TestContext.Current.CancellationToken);
            var workspace = await EvaluationWorkspaceManifestStore.ReadAsync(root)
                ?? throw new InvalidDataException("Test workspace manifest is missing.");
            var artifact = await WorkspaceSemanticDriftArtifactStore.WriteAsync(root, workspace.WorkspaceId,
                runId, drift, TestContext.Current.CancellationToken);
            var runDirectory = Path.Combine(root, "runs", runId.ToString("D"));
            var run = await WorkspaceRunManifestStore.ReadAsync(runDirectory, TestContext.Current.CancellationToken);
            run = run with
            {
                BaselineId = baseline.BaselineId,
                BaselineRunId = baseline.RunId,
                BaselineRunManifestIdentityFingerprint = baseline.RunManifestIdentityFingerprint,
                BaselinePlanFingerprint = baseline.EffectivePlanFingerprint,
                BaselineEvidencePackageIdentity = baseline.EvidencePackageIdentity,
                PreRunSemanticDriftIdentityFingerprint = WorkspacePlanReviewService.IdentityFingerprint(drift),
                PreRunSemanticDriftStatus = drift.Status.ToString(),
                SemanticDriftArtifactReference = artifact.Reference,
                SemanticDriftArtifactIntegrityHash = artifact.IntegrityHash,
                ReviewDecision = "NotRequired"
            };
            await WorkspaceRunManifestStore.WriteAsync(runDirectory, run, createNew: false,
                TestContext.Current.CancellationToken);
            run = await WorkspaceRunManifestStore.ReadAsync(runDirectory, TestContext.Current.CancellationToken);
            var comparison = await WorkspaceBaselineComparisonService.CompareAndPersistAsync(root, run,
                TestContext.Current.CancellationToken)
                ?? throw new InvalidDataException("Expected a candidate outcome comparison.");
            await WorkspaceEvidencePackageService.CreateAsync(root, runId.ToString("D"), null,
                diagnostic: false, TestContext.Current.CancellationToken);
            return (runId, comparison);
        }
    }

    [Fact]
    public async Task WorkspacePilotHistoryPromotesChangedQualifiedCandidatesOnlyExplicitly()
    {
        var root = Path.Combine(Path.GetTempPath(), $"proofshift-workspace-qualified-history-{Guid.NewGuid():N}");
        try
        {
            Assert.Equal(0, (await RunAsync("workspace", "init", root)).ExitCode);
            var baselineRunId = await CreatePackageFixtureRunAsync(root, "QUALIFIED", EvidenceResult.Pass);
            await WorkspaceEvidencePackageService.CreateAsync(root, baselineRunId.ToString("D"), null,
                diagnostic: false, TestContext.Current.CancellationToken);
            var firstPromotion = await RunAsync("workspace", "baseline", "promote", root,
                baselineRunId.ToString("D"), "--json");
            Assert.Equal(0, firstPromotion.ExitCode);
            var baselineId = JsonDocument.Parse(firstPromotion.Output).RootElement.GetProperty("baselineId").GetGuid();
            var baseline = await WorkspaceBaselineService.ShowAsync(root, baselineId.ToString("D"),
                TestContext.Current.CancellationToken);

            var runId = await CreatePackageFixtureRunAsync(root, "QUALIFIED", EvidenceResult.Pass,
                discoveryFingerprints: ["new-physical-schema"]);
            var plan = await WorkspaceEvaluationService.PlanAsync(root, TestContext.Current.CancellationToken);
            var drift = await WorkspaceSemanticDriftService.CompareAsync(root, plan,
                baselineId.ToString("D"), TestContext.Current.CancellationToken);
            var workspace = await EvaluationWorkspaceManifestStore.ReadAsync(root)
                ?? throw new InvalidDataException("Test workspace manifest is missing.");
            var artifact = await WorkspaceSemanticDriftArtifactStore.WriteAsync(root, workspace.WorkspaceId,
                runId, drift, TestContext.Current.CancellationToken);
            var runDirectory = Path.Combine(root, "runs", runId.ToString("D"));
            var run = await WorkspaceRunManifestStore.ReadAsync(runDirectory, TestContext.Current.CancellationToken);
            run = run with
            {
                BaselineId = baseline.BaselineId,
                BaselineRunId = baseline.RunId,
                BaselineRunManifestIdentityFingerprint = baseline.RunManifestIdentityFingerprint,
                BaselinePlanFingerprint = baseline.EffectivePlanFingerprint,
                BaselineEvidencePackageIdentity = baseline.EvidencePackageIdentity,
                PreRunSemanticDriftIdentityFingerprint = WorkspacePlanReviewService.IdentityFingerprint(drift),
                PreRunSemanticDriftStatus = drift.Status.ToString(),
                SemanticDriftArtifactReference = artifact.Reference,
                SemanticDriftArtifactIntegrityHash = artifact.IntegrityHash,
                ReviewDecision = "NotRequired"
            };
            await WorkspaceRunManifestStore.WriteAsync(runDirectory, run, createNew: false,
                TestContext.Current.CancellationToken);
            run = await WorkspaceRunManifestStore.ReadAsync(runDirectory, TestContext.Current.CancellationToken);
            var comparison = await WorkspaceBaselineComparisonService.CompareAndPersistAsync(root, run,
                TestContext.Current.CancellationToken);
            Assert.Equal("ChangedStillQualified", comparison!.OutcomeClassification);
            var candidatePackage = await WorkspaceEvidencePackageService.CreateAsync(root, runId.ToString("D"),
                null, diagnostic: false, TestContext.Current.CancellationToken);
            var packageBytes = await File.ReadAllBytesAsync(candidatePackage.PackagePath,
                TestContext.Current.CancellationToken);
            var activeBeforePromotion = await WorkspaceBaselineService.ActiveAsync(root,
                TestContext.Current.CancellationToken);
            Assert.Equal(baselineId, activeBeforePromotion!.BaselineId);

            var promotion = await RunAsync("workspace", "baseline", "promote", root,
                runId.ToString("D"), "--json");
            Assert.Equal(0, promotion.ExitCode);
            using var promoted = JsonDocument.Parse(promotion.Output);
            Assert.Equal("ChangedStillQualified",
                promoted.RootElement.GetProperty("promotionComparisonOutcomeClassification").GetString());
            Assert.Equal(baselineId,
                promoted.RootElement.GetProperty("promotionComparisonBaselineId").GetGuid());
            Assert.Equal(packageBytes, await File.ReadAllBytesAsync(candidatePackage.PackagePath,
                TestContext.Current.CancellationToken));
            Assert.NotEqual(baselineId, (await WorkspaceBaselineService.ActiveAsync(root,
                TestContext.Current.CancellationToken))!.BaselineId);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WorkspacePlanReportsNoBaselineAndIgnoresRunOnlyAndOutputRootChanges()
    {
        var root = Path.Combine(Path.GetTempPath(), $"proofshift-workspace-drift-stable-{Guid.NewGuid():N}");
        try
        {
            Assert.Equal(0, (await RunAsync("workspace", "init", root)).ExitCode);
            var noBaseline = await RunWithEnvironmentAsync("PROOFSHIFT_TARGET_CONNECTION", "synthetic-only",
                "workspace", "plan", root, "--baseline", "none", "--json");
            Assert.Equal(0, noBaseline.ExitCode);
            using (var noBaselineJson = JsonDocument.Parse(noBaseline.Output))
                Assert.Equal("NoBaseline", noBaselineJson.RootElement.GetProperty("semanticDrift")
                    .GetProperty("status").GetString());
                Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(root, "runs")));

                var firstRun = await CreatePackageFixtureRunAsync(root, "NOT QUALIFIED", EvidenceResult.Fail);
            await WorkspaceEvidencePackageService.CreateAsync(root, firstRun.ToString("D"), null,
                diagnostic: false, TestContext.Current.CancellationToken);
            var firstManifest = await WorkspaceRunManifestStore.ReadAsync(
                Path.Combine(root, "runs", firstRun.ToString("D")), TestContext.Current.CancellationToken);
            var promotion = await RunAsync("workspace", "baseline", "promote", root, firstRun.ToString("D"), "--json");
            Assert.True(promotion.ExitCode == 0, promotion.Output + promotion.Error);
            using var firstPromotionJson = JsonDocument.Parse(promotion.Output);
            var firstBaselineId = firstPromotionJson.RootElement.GetProperty("baselineId").GetGuid();
            var embeddedPlan = await WorkspaceEvidencePackageService.ReadValidatedEffectivePlanAsync(
                Path.Combine(root, "evidence", $"{firstRun:D}.zip"), TestContext.Current.CancellationToken);
            Assert.Equal(firstManifest!.PlanFingerprint, embeddedPlan.Fingerprint);
            Assert.Equal(firstManifest.ConfigurationHash, embeddedPlan.ConfigurationFingerprint);

            var secondRun = await CreatePackageFixtureRunAsync(root, "NOT QUALIFIED", EvidenceResult.Fail,
                replayedCheckpoint: false);
            await WorkspaceEvidencePackageService.CreateAsync(root, secondRun.ToString("D"), null,
                diagnostic: false, TestContext.Current.CancellationToken);
            var secondManifest = await WorkspaceRunManifestStore.ReadAsync(
                Path.Combine(root, "runs", secondRun.ToString("D")), TestContext.Current.CancellationToken);
            Assert.NotEqual(firstRun, secondRun);
            Assert.Equal(firstManifest!.PlanFingerprint, secondManifest!.PlanFingerprint);
            Assert.NotEqual(firstManifest.ReplayedCheckpoint, secondManifest.ReplayedCheckpoint);
            var secondPromotion = await RunAsync("workspace", "baseline", "promote", root, secondRun.ToString("D"),
                "--json");
            Assert.True(secondPromotion.ExitCode == 0, secondPromotion.Output + secondPromotion.Error);
            var catalogPath = Path.Combine(root, ".proofshift", "baselines", "catalog.json");
            var catalogBeforePlan = await File.ReadAllBytesAsync(catalogPath, TestContext.Current.CancellationToken);

            var workspaceManifestPath = Path.Combine(root, "proofshift.workspace.json");
            var workspaceManifest = JsonNode.Parse(await File.ReadAllTextAsync(workspaceManifestPath,
                TestContext.Current.CancellationToken))!.AsObject();
            var paths = workspaceManifest["paths"]!.AsObject();
            paths["runs"] = "run-output";
            paths["evidence"] = "evidence-output";
            Directory.CreateDirectory(Path.Combine(root, "run-output"));
            Directory.CreateDirectory(Path.Combine(root, "evidence-output"));
            await File.WriteAllTextAsync(workspaceManifestPath, workspaceManifest.ToJsonString(),
                TestContext.Current.CancellationToken);

            var unchanged = await RunWithEnvironmentAsync("PROOFSHIFT_TARGET_CONNECTION", "synthetic-only",
                "workspace", "plan", root, "--json");
            Assert.True(unchanged.ExitCode == 0, unchanged.Output + unchanged.Error);
            using (var unchangedJson = JsonDocument.Parse(unchanged.Output))
            {
                var drift = unchangedJson.RootElement.GetProperty("semanticDrift");
                Assert.Equal("NoSemanticChange", drift.GetProperty("status").GetString());
                Assert.False(drift.GetProperty("reviewRequired").GetBoolean());
                Assert.Equal(secondRun, drift.GetProperty("baselineRunId").GetGuid());
                Assert.Equal(secondManifest.PlanFingerprint,
                    drift.GetProperty("currentPlanFingerprint").GetString());
                Assert.Empty(drift.GetProperty("changes").EnumerateArray());
            }
            Assert.Equal(catalogBeforePlan,
                await File.ReadAllBytesAsync(catalogPath, TestContext.Current.CancellationToken));

            var historical = await RunWithEnvironmentAsync("PROOFSHIFT_TARGET_CONNECTION", "synthetic-only",
                "workspace", "plan", root, "--baseline", firstBaselineId.ToString("D"), "--json");
            Assert.True(historical.ExitCode == 0, historical.Output + historical.Error);
            using (var historicalJson = JsonDocument.Parse(historical.Output))
                Assert.Equal(firstRun, historicalJson.RootElement.GetProperty("semanticDrift")
                    .GetProperty("baselineRunId").GetGuid());

            var runtimeSecretChanged = await RunWithEnvironmentAsync("PROOFSHIFT_TARGET_CONNECTION",
                "runtime-secret-value-must-not-appear", "workspace", "plan", root, "--json");
            Assert.True(runtimeSecretChanged.ExitCode == 0,
                runtimeSecretChanged.Output + runtimeSecretChanged.Error);
            Assert.DoesNotContain("runtime-secret-value-must-not-appear", runtimeSecretChanged.Output,
                StringComparison.Ordinal);
            using (var runtimeSecretJson = JsonDocument.Parse(runtimeSecretChanged.Output))
                Assert.Equal("NoSemanticChange", runtimeSecretJson.RootElement.GetProperty("semanticDrift")
                    .GetProperty("status").GetString());

            var targetConfigurationPath = Path.Combine(root, "systems", "target.yaml");
            var targetConfiguration = await File.ReadAllTextAsync(targetConfigurationPath,
                TestContext.Current.CancellationToken);
            Assert.Contains("PROOFSHIFT_TARGET_CONNECTION", targetConfiguration, StringComparison.Ordinal);
            await File.WriteAllTextAsync(targetConfigurationPath,
                targetConfiguration.Replace("PROOFSHIFT_TARGET_CONNECTION", "PROOFSHIFT_PRIVATE_SENTINEL",
                    StringComparison.Ordinal), TestContext.Current.CancellationToken);
            var bindingChanged = await RunWithEnvironmentAsync("PROOFSHIFT_PRIVATE_SENTINEL",
                "another-runtime-secret-that-must-not-appear", "workspace", "plan", root, "--json");
            Assert.True(bindingChanged.ExitCode == 0, bindingChanged.Output + bindingChanged.Error);
            Assert.DoesNotContain("another-runtime-secret-that-must-not-appear", bindingChanged.Output,
                StringComparison.Ordinal);
            using (var bindingJson = JsonDocument.Parse(bindingChanged.Output))
            {
                var drift = bindingJson.RootElement.GetProperty("semanticDrift");
                Assert.Equal("ReviewRequired", drift.GetProperty("status").GetString());
                Assert.Contains(drift.GetProperty("changes").EnumerateArray(), change =>
                    change.GetProperty("category").GetString() == "configuration-binding");
            }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WorkspacePlanReviewBindsExactBaselineAndDriftAndBlocksBeforeCheckpointCapture()
    {
        var root = Path.Combine(Path.GetTempPath(), $"proofshift-workspace-review-{Guid.NewGuid():N}");
        try
        {
            Assert.Equal(0, (await RunAsync("workspace", "init", root)).ExitCode);
            var baselineRun = await CreatePackageFixtureRunAsync(root, "QUALIFIED", EvidenceResult.Pass);
            await WorkspaceEvidencePackageService.CreateAsync(root, baselineRun.ToString("D"), null,
                diagnostic: false, TestContext.Current.CancellationToken);
            var promotion = await RunAsync("workspace", "baseline", "promote", root,
                baselineRun.ToString("D"), "--json");
            Assert.Equal(0, promotion.ExitCode);
            var baselineId = JsonDocument.Parse(promotion.Output).RootElement.GetProperty("baselineId").GetGuid();

            var targetConfigurationPath = Path.Combine(root, "systems", "target.yaml");
            var targetConfiguration = await File.ReadAllTextAsync(targetConfigurationPath,
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(targetConfigurationPath,
                targetConfiguration.Replace("PROOFSHIFT_TARGET_CONNECTION", "PROOFSHIFT_PRIVATE_SENTINEL",
                    StringComparison.Ordinal), TestContext.Current.CancellationToken);

            var driftPlan = await RunWithEnvironmentAsync("PROOFSHIFT_PRIVATE_SENTINEL",
                "review-test-secret-must-not-persist", "workspace", "plan", root,
                "--baseline", baselineId.ToString("D"), "--json");
            Assert.Equal(0, driftPlan.ExitCode);
            using (var driftPlanJson = JsonDocument.Parse(driftPlan.Output))
                Assert.Equal("ReviewRequired", driftPlanJson.RootElement.GetProperty("semanticDrift")
                    .GetProperty("status").GetString());

            var blockedRun = await RunWithEnvironmentAsync("PROOFSHIFT_PRIVATE_SENTINEL",
                "review-test-secret-must-not-persist", "workspace", "run", root, "--json");
            Assert.Equal(1, blockedRun.ExitCode);
            Assert.Contains("PSWORKRUN099", blockedRun.Error, StringComparison.Ordinal);
            Assert.False(Directory.Exists(Path.Combine(root, ".proofshift", "checkpoints")));
            Assert.Single(Directory.EnumerateDirectories(Path.Combine(root, "runs")));

            var reviewed = await RunWithEnvironmentAsync("PROOFSHIFT_PRIVATE_SENTINEL",
                "review-test-secret-must-not-persist", "workspace", "review-plan", root,
                "--baseline", baselineId.ToString("D"), "--json");
            Assert.Equal(0, reviewed.ExitCode);
            using var reviewedJson = JsonDocument.Parse(reviewed.Output);
            var review = reviewedJson.RootElement.GetProperty("review");
            var reviewId = review.GetProperty("reviewId").GetGuid();
            Assert.Equal(baselineId, review.GetProperty("baselineId").GetGuid());
            Assert.Equal("Reviewed", review.GetProperty("decision").GetString());
            Assert.Equal("ReviewRequired", review.GetProperty("driftStatus").GetString());
            Assert.Equal(64, review.GetProperty("semanticDriftIdentityFingerprint").GetString()!.Length);
            var reviewPath = Path.Combine(root, ".proofshift", "reviews", $"{reviewId:D}.json");
            var reviewText = await File.ReadAllTextAsync(reviewPath, TestContext.Current.CancellationToken);
            Assert.DoesNotContain("review-test-secret-must-not-persist", reviewText, StringComparison.Ordinal);

            var currentPlan = await WorkspaceEvaluationService.PlanAsync(root, TestContext.Current.CancellationToken);
            var exactDrift = await WorkspaceSemanticDriftService.CompareAsync(root, currentPlan,
                baselineId.ToString("D"), TestContext.Current.CancellationToken);
            var validatedReview = await WorkspacePlanReviewService.ValidateForExecutionAsync(root,
                reviewId.ToString("D"), exactDrift, TestContext.Current.CancellationToken);
            Assert.Equal(review.GetProperty("integrityHash").GetString(), validatedReview.IntegrityHash);

            var laterRun = await CreatePackageFixtureRunAsync(root, "QUALIFIED", EvidenceResult.Pass);
            await WorkspaceEvidencePackageService.CreateAsync(root, laterRun.ToString("D"), null,
                diagnostic: false, TestContext.Current.CancellationToken);
            var laterPromotion = await RunAsync("workspace", "baseline", "promote", root,
                laterRun.ToString("D"), "--json");
            Assert.Equal(0, laterPromotion.ExitCode);
            var laterBaselineId = JsonDocument.Parse(laterPromotion.Output).RootElement.GetProperty("baselineId").GetGuid();
            var laterPlan = await WorkspaceEvaluationService.PlanAsync(root, TestContext.Current.CancellationToken);
            var laterDrift = await WorkspaceSemanticDriftService.CompareAsync(root, laterPlan,
                laterBaselineId.ToString("D"), TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                WorkspacePlanReviewService.ValidateForExecutionAsync(root, reviewId.ToString("D"),
                    laterDrift, TestContext.Current.CancellationToken));

            await File.WriteAllTextAsync(targetConfigurationPath,
                targetConfiguration.Replace("PROOFSHIFT_TARGET_CONNECTION", "PROOFSHIFT_PRIVATE_SENTINEL_TWO",
                    StringComparison.Ordinal), TestContext.Current.CancellationToken);
            var changedPlan = await WorkspaceEvaluationService.PlanAsync(root, TestContext.Current.CancellationToken);
            var changedDrift = await WorkspaceSemanticDriftService.CompareAsync(root, changedPlan,
                baselineId.ToString("D"), TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                WorkspacePlanReviewService.ValidateForExecutionAsync(root, reviewId.ToString("D"),
                    changedDrift, TestContext.Current.CancellationToken));
            Assert.False(Directory.Exists(Path.Combine(root, ".proofshift", "checkpoints")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void WorkspaceRunRejectsBlockingDriftRegardlessOfReview()
    {
        var blockingDrift = new WorkspaceSemanticDrift(Guid.NewGuid(), Guid.NewGuid(),
            new string('a', 64), new string('b', 64), WorkspaceSemanticDriftStatus.Blocking, true,
            [new WorkspaceSemanticDriftChange("capability", "removed", "source",
                WorkspaceSemanticDriftClassification.Blocking, "Required source-read capability is unavailable.")],
            ["Required source-read capability is unavailable."]);

        Assert.Throws<InvalidOperationException>(() => WorkspaceRunService.ValidateDriftCanRun(blockingDrift));

        var explicitNoBaseline = new WorkspaceSemanticDrift(null, null, null, new string('b', 64),
            WorkspaceSemanticDriftStatus.NoBaseline, false, [], []);
        WorkspaceRunService.ValidateDriftCanRun(explicitNoBaseline);
    }

    [Fact]
    public async Task WorkspacePlanSemanticDriftCategorizesMeaningfulPlanChangesAndBlocksMissingCapabilities()
    {
        var root = Path.Combine(Path.GetTempPath(), $"proofshift-workspace-drift-categories-{Guid.NewGuid():N}");
        try
        {
            Assert.Equal(0, (await RunAsync("workspace", "init", root)).ExitCode);
            var result = await WorkspaceEvaluationService.PlanAsync(root, TestContext.Current.CancellationToken);
            var baseline = result.Plan ?? throw new InvalidDataException("Test workspace has no effective plan.");

            var edge = baseline.Edges[0];
            var field = edge.Fields[0];
            var changedMapping = baseline with
            {
                Edges = baseline.Edges.Select(item => item.Key == edge.Key
                    ? item with
                    {
                        SemanticFingerprint = new string('d', 64),
                        Fields = item.Fields.Select(mapped => mapped.Target == field.Target
                            ? mapped with
                            {
                                Source = "changed-source",
                                SemanticFingerprint = new string('a', 64)
                            }
                            : mapped).ToArray()
                    }
                    : item).ToArray()
            };
            Assert.Contains(WorkspaceSemanticDriftService.ComparePlans(baseline, changedMapping),
                change => change.Category == "mapping" &&
                    change.Classification == WorkspaceSemanticDriftClassification.ReviewRequired);
            var changedOperationSemantics = baseline with
            {
                Edges = baseline.Edges.Select(item => item.Key == edge.Key
                    ? item with { SemanticFingerprint = new string('e', 64) }
                    : item).ToArray()
            };
            Assert.Contains(WorkspaceSemanticDriftService.ComparePlans(baseline, changedOperationSemantics),
                change => change.Category == "mapping" && change.Kind == "semantics-changed");

            var rule = baseline.Rules[0];
            var changedRule = baseline with
            {
                Rules = baseline.Rules.Select(item => item.Id == rule.Id
                    ? item with { SemanticFingerprint = new string('b', 64) }
                    : item).ToArray()
            };
            Assert.Contains(WorkspaceSemanticDriftService.ComparePlans(baseline, changedRule),
                change => change.Category == "verification-rule");

            var changedException = baseline with
            {
                ExpectedExceptions = baseline.ExpectedExceptions.Append(new WorkspacePlanExpectedException(
                    "annotation-new", "rule-new", "FindingChanged", "target", new string('c', 64), "scope-new"))
                    .ToArray()
            };
            Assert.Contains(WorkspaceSemanticDriftService.ComparePlans(baseline, changedException),
                change => change.Category == "expected-exception");
            var changedExceptionMetadata = baseline with
            {
                ExpectedExceptionsFingerprint = new string('f', 64)
            };
            Assert.Contains(WorkspaceSemanticDriftService.ComparePlans(baseline, changedExceptionMetadata),
                change => change.Category == "expected-exception" &&
                    change.Kind == "annotation-metadata-changed");

            var changedReconciliation = baseline with
            {
                ReconciliationRules = baseline.ReconciliationRules
                    .Append(rule.Id).Distinct(StringComparer.Ordinal).ToArray()
            };
            Assert.Contains(WorkspaceSemanticDriftService.ComparePlans(baseline, changedReconciliation),
                change => change.Category == "reconciliation");

            var node = baseline.Nodes[0];
            var changedScope = baseline with
            {
                Nodes = baseline.Nodes.Select(item => item.Key == node.Key
                    ? item with { ScopeFingerprint = "changed-scope" }
                    : item).ToArray()
            };
            Assert.Contains(WorkspaceSemanticDriftService.ComparePlans(baseline, changedScope),
                change => change.Category == "scope");
            var changedSelector = baseline with
            {
                Nodes = baseline.Nodes.Select(item => item.Key == node.Key
                    ? item with { SelectorFingerprint = new string('9', 64) }
                    : item).ToArray()
            };
            Assert.Contains(WorkspaceSemanticDriftService.ComparePlans(baseline, changedSelector),
                change => change.Category == "graph" && change.Kind == "node-changed");

            var sourceSystem = baseline.Systems.Single(system => system.Role == "Source");
            var missingCapability = baseline with
            {
                Systems = baseline.Systems.Select(system => system.System == sourceSystem.System &&
                    system.Endpoint == sourceSystem.Endpoint
                    ? system with
                    {
                        Capabilities = system.Capabilities.Except(["sourceRead"], StringComparer.Ordinal).ToArray()
                    }
                    : system).ToArray()
            };
            Assert.Contains(WorkspaceSemanticDriftService.ComparePlans(baseline, missingCapability),
                change => change.Category == "capability" &&
                    change.Classification == WorkspaceSemanticDriftClassification.Blocking);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WorkspacePlanDeduplicatesSystemsByEndpointIdentityForMultiNodeGraph()
    {
        var root = Path.Combine(Path.GetTempPath(), $"proofshift-workspace-endpoint-plan-{Guid.NewGuid():N}");
        try
        {
            Assert.Equal(0, (await RunAsync("workspace", "init", root)).ExitCode);
            var scenarioRoot = Path.Combine(DemoScenarioTestPaths.FindRepositoryRoot(),
                "scenarios", "byom-public-works", "shadow");
            foreach (var sourcePath in Directory.GetFiles(scenarioRoot, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(scenarioRoot, sourcePath);
                var destinationPath = Path.Combine(root, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                File.Copy(sourcePath, destinationPath, overwrite: true);
            }

            var result = await WorkspaceEvaluationService.PlanAsync(root, TestContext.Current.CancellationToken);
            var plan = Assert.IsType<WorkspaceEvaluationPlan>(result.Plan);
            Assert.Equal(2, plan.Systems.Count);
            Assert.Equal(plan.Systems.Count,
                plan.Systems.Select(system => (system.System, system.Endpoint)).Distinct().Count());
            Assert.Empty(WorkspaceSemanticDriftService.ComparePlans(plan, plan));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WorkspacePlanComparesPersistedDiscoveryAndReportsMissingHistoryWithoutLiveDiscovery()
    {
        var root = Path.Combine(Path.GetTempPath(), $"proofshift-workspace-drift-discovery-{Guid.NewGuid():N}");
        try
        {
            Assert.Equal(0, (await RunAsync("workspace", "init", root)).ExitCode);
            var configurationPath = Path.Combine(root, "proofshift.yaml");
            var firstDiscoveryPath = Path.Combine(root, "discovery", "source-before");
            var firstDiscovery = await RunAsync("discover", configurationPath, "--system", "source", "--endpoint",
                "records", "--output", firstDiscoveryPath, "--json");
            Assert.True(firstDiscovery.ExitCode == 0, firstDiscovery.Output + firstDiscovery.Error);
            var firstArtifact = await PhysicalDiscoveryStore.ReadAsync(firstDiscoveryPath,
                TestContext.Current.CancellationToken);

            var run = await CreatePackageFixtureRunAsync(root, "NOT QUALIFIED", EvidenceResult.Fail,
                discoveryFingerprints: [firstArtifact.Fingerprint]);
            await WorkspaceEvidencePackageService.CreateAsync(root, run.ToString("D"), null,
                diagnostic: false, TestContext.Current.CancellationToken);
            var promotion = await RunAsync("workspace", "baseline", "promote", root, run.ToString("D"), "--json");
            Assert.True(promotion.ExitCode == 0, promotion.Output + promotion.Error);

            var staleHistory = await RunWithEnvironmentAsync("PROOFSHIFT_TARGET_CONNECTION", "synthetic-only",
                "workspace", "plan", root, "--json");
            using (var staleJson = JsonDocument.Parse(staleHistory.Output))
            {
                Assert.Equal("NotComparable", staleJson.RootElement.GetProperty("semanticDrift")
                    .GetProperty("status").GetString());
                Assert.True(staleJson.RootElement.GetProperty("semanticDrift")
                    .GetProperty("reviewRequired").GetBoolean());
            }

            await File.WriteAllTextAsync(Path.Combine(root, "data", "source.csv"), "id;amount;new-field\n",
                TestContext.Current.CancellationToken);
            var currentDiscoveryPath = Path.Combine(root, "discovery", "source-after");
            var currentDiscovery = await RunAsync("discover", configurationPath, "--system", "source", "--endpoint",
                "records", "--output", currentDiscoveryPath, "--json");
            Assert.True(currentDiscovery.ExitCode == 0, currentDiscovery.Output + currentDiscovery.Error);

            var plan = await RunWithEnvironmentAsync("PROOFSHIFT_TARGET_CONNECTION", "synthetic-only",
                "workspace", "plan", root, "--json");
            using (var planJson = JsonDocument.Parse(plan.Output))
            {
                var drift = planJson.RootElement.GetProperty("semanticDrift");
                Assert.Equal("ReviewRequired", drift.GetProperty("status").GetString());
                Assert.Contains(drift.GetProperty("changes").EnumerateArray(), change =>
                    change.GetProperty("category").GetString() == "discovery" &&
                    change.GetProperty("kind").GetString() == "field-added");
            }

            Directory.Delete(firstDiscoveryPath, recursive: true);
            var missingHistory = await RunWithEnvironmentAsync("PROOFSHIFT_TARGET_CONNECTION", "synthetic-only",
                "workspace", "plan", root, "--json");
            using var missingJson = JsonDocument.Parse(missingHistory.Output);
            var missingDrift = missingJson.RootElement.GetProperty("semanticDrift");
            Assert.Equal("NotComparable", missingDrift.GetProperty("status").GetString());
            Assert.Contains(missingDrift.GetProperty("changes").EnumerateArray(), change =>
                change.GetProperty("category").GetString() == "discovery" &&
                change.GetProperty("classification").GetString() == "NotChecked");

            var acknowledged = await RunWithEnvironmentAsync("PROOFSHIFT_TARGET_CONNECTION", "synthetic-only",
                "workspace", "review-plan", root, "--json");
            Assert.Equal(0, acknowledged.ExitCode);
            using var acknowledgedJson = JsonDocument.Parse(acknowledged.Output);
            var review = acknowledgedJson.RootElement.GetProperty("review");
            Assert.Equal("NotComparable", review.GetProperty("driftStatus").GetString());
            Assert.Equal("ComparisonUnavailableAcknowledged", review.GetProperty("decision").GetString());
            var currentPlan = await WorkspaceEvaluationService.PlanAsync(root,
                TestContext.Current.CancellationToken);
            var exactDrift = await WorkspaceSemanticDriftService.CompareAsync(root, currentPlan, "active",
                TestContext.Current.CancellationToken);
            var validatedReview = await WorkspacePlanReviewService.ValidateForExecutionAsync(root,
                review.GetProperty("reviewId").GetGuid().ToString("D"), exactDrift,
                TestContext.Current.CancellationToken);
            Assert.Equal("ComparisonUnavailableAcknowledged", validatedReview.Decision);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WorkspaceBaselinePromotionAndInspectionFailClosedOnIncompleteOrTamperedReferences()
    {
        var root = Path.Combine(Path.GetTempPath(), $"proofshift-workspace-baseline-invalid-{Guid.NewGuid():N}");
        try
        {
            Assert.Equal(0, (await RunAsync("workspace", "init", root)).ExitCode);
            var incompleteRun = await RunAsync("workspace", "run", root, "--json");
            Assert.Equal(1, incompleteRun.ExitCode);
            using var runOutput = JsonDocument.Parse(incompleteRun.Output
                .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Last());
            var incompleteRunId = runOutput.RootElement.GetProperty("run").GetProperty("runId").GetGuid();
            var incompletePromotion = await RunAsync("workspace", "baseline", "promote", root,
                incompleteRunId.ToString("D"));
            Assert.Equal(1, incompletePromotion.ExitCode);
            Assert.Contains("PSWORKBASE099", incompletePromotion.Error, StringComparison.Ordinal);
            Assert.False(Directory.Exists(Path.Combine(root, ".proofshift", "baselines")));

            var run = await CreatePackageFixtureRunAsync(root, "QUALIFIED", EvidenceResult.Pass);
            var missingPackagePromotion = await RunAsync("workspace", "baseline", "promote", root,
                run.ToString("D"));
            Assert.Equal(1, missingPackagePromotion.ExitCode);
            Assert.Contains("PSWORKBASE099", missingPackagePromotion.Error, StringComparison.Ordinal);

            var packagePath = Path.Combine(root, "evidence", $"{run:D}.zip");
            await WorkspaceEvidencePackageService.CreateAsync(root, run.ToString("D"), packagePath,
                diagnostic: false, TestContext.Current.CancellationToken);
            var otherRun = await CreatePackageFixtureRunAsync(root, "QUALIFIED", EvidenceResult.Pass);
            var otherPackagePath = Path.Combine(root, "evidence", $"{otherRun:D}.zip");
            await WorkspaceEvidencePackageService.CreateAsync(root, otherRun.ToString("D"), otherPackagePath,
                diagnostic: false, TestContext.Current.CancellationToken);
            File.Copy(otherPackagePath, packagePath, overwrite: true);
            var mismatchedPackagePromotion = await RunAsync("workspace", "baseline", "promote", root,
                run.ToString("D"));
            Assert.Equal(1, mismatchedPackagePromotion.ExitCode);
            Assert.Contains("PSWORKBASE099", mismatchedPackagePromotion.Error, StringComparison.Ordinal);
            await WorkspaceEvidencePackageService.CreateAsync(root, run.ToString("D"), packagePath,
                diagnostic: false, TestContext.Current.CancellationToken);

            var runPath = Path.Combine(root, "runs", run.ToString("D"), "run.json");
            await File.AppendAllTextAsync(runPath, "not-json", TestContext.Current.CancellationToken);
            var invalidRunPromotion = await RunAsync("workspace", "baseline", "promote", root, run.ToString("D"));
            Assert.Equal(1, invalidRunPromotion.ExitCode);
            Assert.Contains("PSWORKBASE099", invalidRunPromotion.Error, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }

        var tamperedRoot = Path.Combine(Path.GetTempPath(), $"proofshift-workspace-baseline-tamper-{Guid.NewGuid():N}");
        try
        {
            Assert.Equal(0, (await RunAsync("workspace", "init", tamperedRoot)).ExitCode);
            var run = await CreatePackageFixtureRunAsync(tamperedRoot, "NOT QUALIFIED", EvidenceResult.Fail);
            var packagePath = Path.Combine(tamperedRoot, "evidence", $"{run:D}.zip");
            await WorkspaceEvidencePackageService.CreateAsync(tamperedRoot, run.ToString("D"), packagePath,
                diagnostic: false, TestContext.Current.CancellationToken);
            var promoted = await RunAsync("workspace", "baseline", "promote", tamperedRoot,
                run.ToString("D"), "--json");
            Assert.Equal(0, promoted.ExitCode);

            await File.WriteAllTextAsync(packagePath, "modified evidence archive",
                TestContext.Current.CancellationToken);
            var inspection = await RunAsync("workspace", "baseline", "show", tamperedRoot,
                JsonDocument.Parse(promoted.Output).RootElement.GetProperty("baselineId").GetGuid().ToString("D"));
            Assert.Equal(1, inspection.ExitCode);
            Assert.Contains("PSWORKBASE099", inspection.Error, StringComparison.Ordinal);
            var history = await RunAsync("workspace", "baseline", "list", tamperedRoot);
            Assert.Equal(1, history.ExitCode);
            Assert.Contains("PSWORKBASE099", history.Error, StringComparison.Ordinal);
            var invalidBaselinePlan = await RunWithEnvironmentAsync("PROOFSHIFT_TARGET_CONNECTION",
                "synthetic-only", "workspace", "plan", tamperedRoot, "--json");
            Assert.Equal(1, invalidBaselinePlan.ExitCode);
            Assert.Contains("PSPLAN099", invalidBaselinePlan.Error, StringComparison.Ordinal);

            var laterRun = await CreatePackageFixtureRunAsync(tamperedRoot, "QUALIFIED", EvidenceResult.Pass);
            await WorkspaceEvidencePackageService.CreateAsync(tamperedRoot, laterRun.ToString("D"), null,
                diagnostic: false, TestContext.Current.CancellationToken);
            var laterPromotion = await RunAsync("workspace", "baseline", "promote", tamperedRoot,
                laterRun.ToString("D"));
            Assert.Equal(1, laterPromotion.ExitCode);
            Assert.Contains("PSWORKBASE099", laterPromotion.Error, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(tamperedRoot)) Directory.Delete(tamperedRoot, recursive: true);
        }
    }

    [Fact]
    public async Task WorkspaceEvidencePackageReportsSafeDiagnosticWhenSavedSummaryDisagrees()
    {
        var root = Path.Combine(Path.GetTempPath(), $"proofshift-evidence-package-summary-{Guid.NewGuid():N}");
        try
        {
            Assert.Equal(0, (await RunAsync("workspace", "init", root)).ExitCode);
            var run = await CreatePackageFixtureRunAsync(root, "QUALIFIED", EvidenceResult.Pass);
            var runDirectory = Path.Combine(root, "runs", run.ToString("D"));
            const string sensitiveSummary = "sensitive summary fixture";
            await File.WriteAllTextAsync(Path.Combine(runDirectory, "summary.md"), sensitiveSummary,
                TestContext.Current.CancellationToken);

            var result = await RunAsync("workspace", "evidence", "package", root, run.ToString("D"),
                "--output", Path.Combine(root, "evidence", "tampered-summary.zip"), "--json");

            Assert.Equal(1, result.ExitCode);
            Assert.Contains("PSWORKPKG102", result.Error, StringComparison.Ordinal);
            Assert.DoesNotContain(sensitiveSummary, result.Error, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WorkspaceEvidencePackageReportsSafeDiagnosticWhenRecoveryArtifactIntegrityFails()
    {
        var root = Path.Combine(Path.GetTempPath(), $"proofshift-evidence-package-recovery-{Guid.NewGuid():N}");
        try
        {
            Assert.Equal(0, (await RunAsync("workspace", "init", root)).ExitCode);
            var run = await CreatePackageFixtureRunAsync(root, "NOT QUALIFIED", EvidenceResult.Fail);
            var runDirectory = Path.Combine(root, "runs", run.ToString("D"));
            var runManifest = await WorkspaceRunManifestStore.ReadAsync(runDirectory,
                TestContext.Current.CancellationToken);
            var recoveryManifest = Assert.IsType<string>(runManifest.RecoveryArtifactReference);
            var recoveryDirectory = Path.GetDirectoryName(Path.Combine(root,
                recoveryManifest.Replace('/', Path.DirectorySeparatorChar)))!;
            var recoveryArtifact = Path.Combine(recoveryDirectory, "assessment.json");
            await File.WriteAllTextAsync(recoveryArtifact, "sensitive recovery fixture",
                TestContext.Current.CancellationToken);
            var packagePath = Path.Combine(root, "evidence", "tampered-recovery.zip");

            var result = await RunAsync("workspace", "evidence", "package", root, run.ToString("D"),
                "--output", packagePath, "--json");

            Assert.Equal(1, result.ExitCode);
            Assert.Contains("PSWORKPKG203", result.Error, StringComparison.Ordinal);
            Assert.DoesNotContain("sensitive recovery fixture", result.Error, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("QUALIFIED", EvidenceResult.Pass)]
    [InlineData("NOT QUALIFIED", EvidenceResult.Fail)]
    public async Task WorkspaceSummaryIsRunBoundUsefulAndRedactsEvidenceValues(
        string disposition, EvidenceResult evidenceResult)
    {
        var root = Path.Combine(Path.GetTempPath(), $"proofshift-workspace-summary-{Guid.NewGuid():N}");
        const string secretValue = "do-not-print-sensitive-evidence-value";
        try
        {
            Assert.Equal(0, (await RunAsync("workspace", "init", root)).ExitCode);
            var run = await CreatePackageFixtureRunAsync(root, disposition, evidenceResult,
                detailedFindings: disposition == "NOT QUALIFIED", evidenceValue: secretValue);
            var result = await WorkspaceEvaluationSummaryService.CreateAsync(root, run.ToString("D"), null,
                TestContext.Current.CancellationToken);
            var content = await File.ReadAllTextAsync(result.SummaryPath, TestContext.Current.CancellationToken);
            var metadata = WorkspaceEvaluationSummaryService.ReadMetadata(content);

            Assert.Equal(run, metadata.RunId);
            Assert.Equal(result.ReportIdentity, metadata.ReportIdentity);
            var nullIdentity = content.Replace(
                $"\"runManifestIdentityFingerprint\":\"{metadata.RunManifestIdentityFingerprint}\"",
                "\"runManifestIdentityFingerprint\":null", StringComparison.Ordinal);
            Assert.Throws<InvalidDataException>(() => WorkspaceEvaluationSummaryService.ReadMetadata(nullIdentity));
            Assert.Equal(disposition, result.Disposition);
            Assert.DoesNotContain(secretValue, content, StringComparison.Ordinal);
            Assert.DoesNotContain("sensitive note", content, StringComparison.Ordinal);
            if (disposition == "QUALIFIED")
            {
                Assert.Contains("Execution state: **Completed**", content, StringComparison.Ordinal);
                Assert.Contains("Final disposition: **QUALIFIED**", content, StringComparison.Ordinal);
                Assert.Contains("No unresolved failed Verification findings", content, StringComparison.Ordinal);
            }
            else
            {
                Assert.Contains("Final disposition: **NOT QUALIFIED**", content, StringComparison.Ordinal);
                Assert.Contains("`budget-attributes`", content, StringComparison.Ordinal);
                Assert.Contains("`budget-total`", content, StringComparison.Ordinal);
                Assert.Contains("`proofshift.verification.materialized-state`", content, StringComparison.Ordinal);
                Assert.Contains("Expected exceptions matched: 1", content, StringComparison.Ordinal);
                Assert.Contains("Expected exceptions waive no findings", content, StringComparison.Ordinal);
                Assert.Contains("Reconciliation: failed", content, StringComparison.Ordinal);
                Assert.Contains("Materialized-state/integrity change detected: yes", content, StringComparison.Ordinal);
                Assert.Equal(3, result.FailedFindingCount);
            }

            var cli = await RunAsync("workspace", "summary", root, run.ToString("D"), "--json");
            Assert.Equal(0, cli.ExitCode);
            Assert.DoesNotContain(secretValue, cli.Output, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WorkspaceSummaryForIncompleteRunDoesNotInventDisposition()
    {
        var root = Path.Combine(Path.GetTempPath(), $"proofshift-workspace-summary-incomplete-{Guid.NewGuid():N}");
        try
        {
            Assert.Equal(0, (await RunAsync("workspace", "init", root)).ExitCode);
            var runId = await CreatePackageFixtureRunAsync(root, "QUALIFIED", EvidenceResult.Pass);
            var runDirectory = Path.Combine(root, "runs", runId.ToString("D"));
            var run = await WorkspaceRunManifestStore.ReadAsync(runDirectory, TestContext.Current.CancellationToken);
            await WorkspaceRunManifestStore.WriteAsync(runDirectory, run with
            {
                State = "Failed",
                Disposition = null,
                FailureCode = "PSWORKRUN_TEST_INTERRUPTED"
            }, createNew: false, TestContext.Current.CancellationToken);

            var result = await WorkspaceEvaluationSummaryService.CreateAsync(root, runId.ToString("D"), null,
                TestContext.Current.CancellationToken);
            var content = await File.ReadAllTextAsync(result.SummaryPath, TestContext.Current.CancellationToken);

            Assert.Null(result.Disposition);
            Assert.Contains("Execution state: **Failed**", content, StringComparison.Ordinal);
            Assert.Contains("Final business disposition: **not established**", content, StringComparison.Ordinal);
            Assert.Contains("PSWORKRUN_TEST_INTERRUPTED", content, StringComparison.Ordinal);
            Assert.DoesNotContain("Final disposition: **QUALIFIED**", content, StringComparison.Ordinal);
            Assert.DoesNotContain("Final disposition: **NOT QUALIFIED**", content, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WorkspaceRunComparisonPreservesFindingIdentityAndExpectedExceptionChanges()
    {
        var root = Path.Combine(Path.GetTempPath(), $"proofshift-workspace-compare-{Guid.NewGuid():N}");
        try
        {
            Assert.Equal(0, (await RunAsync("workspace", "init", root)).ExitCode);
            var before = await CreatePackageFixtureRunAsync(root, "NOT QUALIFIED", EvidenceResult.Fail,
                detailedFindings: true);
            var after = await CreatePackageFixtureRunAsync(root, "QUALIFIED", EvidenceResult.Pass);

            var comparison = await WorkspaceRunComparisonService.CompareAsync(root, before.ToString("D"),
                after.ToString("D"), TestContext.Current.CancellationToken);

            Assert.True(comparison.DispositionChanged);
            Assert.False(comparison.SemanticallyEquivalent);
            Assert.Contains(comparison.Findings, item =>
                item.Category == "Resolved" && item.RuleId == "budget-attributes");
            Assert.Contains(comparison.Findings, item =>
                item.Category == "Resolved" && item.RuleId == "budget-total");
            Assert.True(comparison.MaterializedStateFindingResolved);
            Assert.Contains(comparison.ExpectedExceptions, item =>
                item.Category == "Removed" && item.AnnotationIdentity == "budget-attributes-expected");
            Assert.False(comparison.SameDispositionImpliesEquivalent);

            var cli = await RunAsync("workspace", "compare", root, before.ToString("D"), after.ToString("D"), "--json");
            Assert.Equal(0, cli.ExitCode);
            Assert.Contains("materializedStateFindingResolved", cli.Output, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WorkspaceRunComparisonIdentifiesChangedFindingAndKeepsFanOutTargetsDistinct()
    {
        var root = Path.Combine(Path.GetTempPath(), $"proofshift-workspace-compare-changed-{Guid.NewGuid():N}");
        try
        {
            Assert.Equal(0, (await RunAsync("workspace", "init", root)).ExitCode);
            var first = await CreatePackageFixtureRunAsync(root, "NOT QUALIFIED", EvidenceResult.Fail,
                evidenceValue: "first-value");
            var second = await CreatePackageFixtureRunAsync(root, "NOT QUALIFIED", EvidenceResult.Fail,
                evidenceValue: "second-value");

            var comparison = await WorkspaceRunComparisonService.CompareAsync(root, first.ToString("D"),
                second.ToString("D"), TestContext.Current.CancellationToken);

            Assert.Contains(comparison.Findings, item =>
                item.Category == "Changed" && item.RuleId == "proofshift.verification.materialized-state");
            var beforeEvidence = await WorkspaceEvaluationSummaryService.ReadEvidenceAsync(
                Path.Combine(root, "runs", first.ToString("D")),
                await WorkspaceRunManifestStore.ReadAsync(Path.Combine(root, "runs", first.ToString("D")),
                    TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
            Assert.Single(beforeEvidence.Findings.Select(item => item.TargetIdentity).Distinct(StringComparer.Ordinal));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WorkspaceRunComparisonIgnoresRunOnlyIdentityAndDetectsChangedExceptionAnnotation()
    {
        var root = Path.Combine(Path.GetTempPath(), $"proofshift-workspace-compare-annotations-{Guid.NewGuid():N}");
        try
        {
            Assert.Equal(0, (await RunAsync("workspace", "init", root)).ExitCode);
            var before = await CreatePackageFixtureRunAsync(root, "NOT QUALIFIED", EvidenceResult.Fail,
                detailedFindings: true, exceptionReason: "reason one");
            var semanticallySame = await CreatePackageFixtureRunAsync(root, "NOT QUALIFIED", EvidenceResult.Fail,
                detailedFindings: true, exceptionReason: "reason one");
            var annotationChanged = await CreatePackageFixtureRunAsync(root, "NOT QUALIFIED", EvidenceResult.Fail,
                detailedFindings: true, exceptionReason: "reason two");

            var sameComparison = await WorkspaceRunComparisonService.CompareAsync(root, before.ToString("D"),
                semanticallySame.ToString("D"), TestContext.Current.CancellationToken);
            Assert.NotEqual(before, semanticallySame);
            Assert.True(sameComparison.SemanticallyEquivalent);
            Assert.False(sameComparison.DispositionChanged);
            Assert.False(sameComparison.SameDispositionImpliesEquivalent);

            var changedComparison = await WorkspaceRunComparisonService.CompareAsync(root, before.ToString("D"),
                annotationChanged.ToString("D"), TestContext.Current.CancellationToken);
            Assert.False(changedComparison.SemanticallyEquivalent);
            Assert.Contains(changedComparison.ExpectedExceptions, item =>
                item.Category == "Changed" && item.AnnotationIdentity == "budget-attributes-expected");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WorkspaceBaselineOutcomeComparisonClassifiesQualificationAndReplayWithoutPromoting()
    {
        var root = Path.Combine(Path.GetTempPath(), $"proofshift-workspace-outcomes-{Guid.NewGuid():N}");
        try
        {
            Assert.Equal(0, (await RunAsync("workspace", "init", root)).ExitCode);
            var qualifiedBaseline = await PromoteBaselineAsync("QUALIFIED", EvidenceResult.Pass, detailedFindings: false);

            var equivalent = await CompareCandidateAsync(qualifiedBaseline, "QUALIFIED", EvidenceResult.Pass,
                detailedFindings: false, replayedCheckpoint: false);
            Assert.Equal("Equivalent", equivalent.OutcomeClassification);
            Assert.True(equivalent.DetailedComparison.ExecutionModeChanged);
            Assert.Equal(qualifiedBaseline.BaselineId, equivalent.BaselineId);

            var changedStillQualified = await CompareCandidateAsync(qualifiedBaseline, "QUALIFIED",
                EvidenceResult.Pass, detailedFindings: false, discoveryFingerprints: ["new-schema"]);
            Assert.Equal("ChangedStillQualified", changedStillQualified.OutcomeClassification);
            Assert.True(changedStillQualified.DetailedComparison.DiscoveryChanged);

            var regressed = await CompareCandidateAsync(qualifiedBaseline, "NOT QUALIFIED", EvidenceResult.Fail,
                detailedFindings: true);
            Assert.Equal("Regressed", regressed.OutcomeClassification);
            Assert.Contains(regressed.DetailedComparison.Findings, item => item.Category == "Added" &&
                item.RuleId == "budget-attributes");

            var notQualifiedBaseline = await PromoteBaselineAsync("NOT QUALIFIED", EvidenceResult.Fail,
                detailedFindings: true);
            var improved = await CompareCandidateAsync(notQualifiedBaseline, "QUALIFIED", EvidenceResult.Pass,
                detailedFindings: false);
            Assert.Equal("Improved", improved.OutcomeClassification);
            Assert.Contains(improved.DetailedComparison.Findings, item => item.Category == "Resolved");
            var improvedSummary = await WorkspaceEvaluationSummaryService.CreateAsync(root,
                improved.CandidateRunId.ToString("D"), null, TestContext.Current.CancellationToken);
            var improvedSummaryText = await File.ReadAllTextAsync(improvedSummary.SummaryPath,
                TestContext.Current.CancellationToken);
            Assert.Contains("disposition **NOT QUALIFIED**", improvedSummaryText, StringComparison.Ordinal);
            Assert.Contains("Candidate disposition: **QUALIFIED**", improvedSummaryText, StringComparison.Ordinal);
            Assert.Contains("Post-run outcome: **Improved**", improvedSummaryText, StringComparison.Ordinal);

            var notComparable = await CompareCandidateAsync(qualifiedBaseline, null, EvidenceResult.Pass,
                detailedFindings: false);
            Assert.Equal("NotComparable", notComparable.OutcomeClassification);

            var unavailableDrift = await CompareCandidateAsync(qualifiedBaseline, "QUALIFIED", EvidenceResult.Pass,
                detailedFindings: false, preRunDriftStatus: "NotComparable");
            Assert.Equal("NotComparable", unavailableDrift.OutcomeClassification);
            var unavailableSummary = await WorkspaceEvaluationSummaryService.CreateAsync(root,
                unavailableDrift.CandidateRunId.ToString("D"), null, TestContext.Current.CancellationToken);
            var unavailableText = await File.ReadAllTextAsync(unavailableSummary.SummaryPath,
                TestContext.Current.CancellationToken);
            Assert.Contains("Baseline comparison was not fully available", unavailableText, StringComparison.Ordinal);
            Assert.Contains("Post-run outcome: **NotComparable**", unavailableText, StringComparison.Ordinal);
            Assert.DoesNotContain("Post-run outcome: **Equivalent**", unavailableText, StringComparison.Ordinal);

            var stillNotQualified = await CompareCandidateAsync(notQualifiedBaseline, "NOT QUALIFIED",
                EvidenceResult.Fail, detailedFindings: false);
            Assert.Equal("StillNotQualified", stillNotQualified.OutcomeClassification);
            Assert.Contains(stillNotQualified.DetailedComparison.Findings, item => item.Category == "Resolved");
            var notQualifiedSummary = await WorkspaceEvaluationSummaryService.CreateAsync(root,
                stillNotQualified.CandidateRunId.ToString("D"), null, TestContext.Current.CancellationToken);
            var notQualifiedSummaryText = await File.ReadAllTextAsync(notQualifiedSummary.SummaryPath,
                TestContext.Current.CancellationToken);
            Assert.Contains("disposition **NOT QUALIFIED**", notQualifiedSummaryText, StringComparison.Ordinal);
            Assert.Contains("Post-run outcome: **StillNotQualified**", notQualifiedSummaryText, StringComparison.Ordinal);
            Assert.Contains("Finding resolved:", notQualifiedSummaryText, StringComparison.Ordinal);
            Assert.Contains("Expected-exception annotation removed:", notQualifiedSummaryText, StringComparison.Ordinal);

            var incompleteRunId = await CreatePackageFixtureRunAsync(root, "QUALIFIED", EvidenceResult.Pass);
            var incompleteRunDirectory = Path.Combine(root, "runs", incompleteRunId.ToString("D"));
            var incompleteRun = await WorkspaceRunManifestStore.ReadAsync(incompleteRunDirectory,
                TestContext.Current.CancellationToken);
            incompleteRun = incompleteRun with
            {
                State = "Interrupted",
                BaselineId = qualifiedBaseline.BaselineId
            };
            await WorkspaceRunManifestStore.WriteAsync(incompleteRunDirectory, incompleteRun, createNew: false,
                TestContext.Current.CancellationToken);
            incompleteRun = await WorkspaceRunManifestStore.ReadAsync(incompleteRunDirectory,
                TestContext.Current.CancellationToken);
            Assert.Null(await WorkspaceBaselineComparisonService.CompareAndPersistAsync(root, incompleteRun,
                TestContext.Current.CancellationToken));
            Assert.False(File.Exists(Path.Combine(root, ".proofshift", "comparisons",
                $"{incompleteRunId:D}.json")));

            var stored = await WorkspaceBaselineComparisonService.ReadAsync(root, changedStillQualified.CandidateRunId,
                await WorkspaceRunManifestStore.ReadAsync(
                    Path.Combine(root, "runs", changedStillQualified.CandidateRunId.ToString("D")),
                    TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
            Assert.Equal(changedStillQualified.IntegrityHash, stored!.IntegrityHash);
            var summary = await WorkspaceEvaluationSummaryService.CreateAsync(root,
                changedStillQualified.CandidateRunId.ToString("D"), null, TestContext.Current.CancellationToken);
            Assert.Equal("ChangedStillQualified", summary.OutcomeClassification);
            var summaryText = await File.ReadAllTextAsync(summary.SummaryPath, TestContext.Current.CancellationToken);
            Assert.Contains("Post-run outcome: **ChangedStillQualified**", summaryText, StringComparison.Ordinal);
            Assert.Equal(notQualifiedBaseline.BaselineId,
                (await WorkspaceBaselineService.ActiveAsync(root, TestContext.Current.CancellationToken))!.BaselineId);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }

        async Task<WorkspaceBaselineRecord> PromoteBaselineAsync(string disposition, EvidenceResult result,
            bool detailedFindings)
        {
            var run = await CreatePackageFixtureRunAsync(root, disposition, result, detailedFindings);
            await WorkspaceEvidencePackageService.CreateAsync(root, run.ToString("D"), null,
                diagnostic: false, TestContext.Current.CancellationToken);
            var promotion = await RunAsync("workspace", "baseline", "promote", root, run.ToString("D"), "--json");
            Assert.Equal(0, promotion.ExitCode);
            var baselineId = JsonDocument.Parse(promotion.Output).RootElement.GetProperty("baselineId").GetGuid();
            return await WorkspaceBaselineService.ShowAsync(root, baselineId.ToString("D"),
                TestContext.Current.CancellationToken);
        }

        async Task<WorkspaceBaselineComparisonRecord> CompareCandidateAsync(WorkspaceBaselineRecord baseline,
            string? disposition, EvidenceResult result, bool detailedFindings,
            IReadOnlyList<string>? discoveryFingerprints = null, bool replayedCheckpoint = true,
            string preRunDriftStatus = "NoSemanticChange")
        {
            var runId = await CreatePackageFixtureRunAsync(root, disposition, result, detailedFindings,
                discoveryFingerprints: discoveryFingerprints, replayedCheckpoint: replayedCheckpoint);
            var runDirectory = Path.Combine(root, "runs", runId.ToString("D"));
            var run = await WorkspaceRunManifestStore.ReadAsync(runDirectory, TestContext.Current.CancellationToken);
            run = run with
            {
                BaselineId = baseline.BaselineId,
                BaselineRunId = baseline.RunId,
                BaselineRunManifestIdentityFingerprint = baseline.RunManifestIdentityFingerprint,
                BaselinePlanFingerprint = baseline.EffectivePlanFingerprint,
                BaselineEvidencePackageIdentity = baseline.EvidencePackageIdentity,
                PreRunSemanticDriftIdentityFingerprint = new string('a', 64),
                PreRunSemanticDriftStatus = preRunDriftStatus,
                ReviewDecision = "NotRequired"
            };
            await WorkspaceRunManifestStore.WriteAsync(runDirectory, run, createNew: false,
                TestContext.Current.CancellationToken);
            var boundRun = await WorkspaceRunManifestStore.ReadAsync(runDirectory, TestContext.Current.CancellationToken);
            var manifestPath = Path.Combine(runDirectory, "run.json");
            var tamperedManifest = JsonNode.Parse(
                await File.ReadAllTextAsync(manifestPath, TestContext.Current.CancellationToken))
                ?? throw new InvalidDataException("Test run manifest was empty.");
            tamperedManifest["preRunSemanticDriftIdentityFingerprint"] = new string('b', 64);
            Assert.Throws<InvalidDataException>(() => WorkspaceRunManifestStore.ReadAndValidate(
                Encoding.UTF8.GetBytes(tamperedManifest.ToJsonString())));
            return await WorkspaceBaselineComparisonService.CompareAndPersistAsync(root, boundRun,
                TestContext.Current.CancellationToken)
                ?? throw new InvalidDataException("Expected a persisted baseline outcome comparison.");
        }
    }

    [Fact]
    public async Task BaselineGovernancePackageValidatesAndComparesOffline()
    {
        var root = Path.Combine(Path.GetTempPath(), $"proofshift-workspace-offline-governance-{Guid.NewGuid():N}");
        try
        {
            Assert.Equal(0, (await RunAsync("workspace", "init", root)).ExitCode);
            var baselineRunId = await CreatePackageFixtureRunAsync(root, "QUALIFIED", EvidenceResult.Pass);
            var baselinePackage = await WorkspaceEvidencePackageService.CreateAsync(root,
                baselineRunId.ToString("D"), null, diagnostic: false,
                TestContext.Current.CancellationToken);
            var baselinePackagePath = baselinePackage.PackagePath;
            Assert.True((await WorkspaceEvidencePackageService.ValidateAsync(baselinePackagePath,
                TestContext.Current.CancellationToken)).Valid);
            var promotion = await RunAsync("workspace", "baseline", "promote", root,
                baselineRunId.ToString("D"), "--json");
            Assert.Equal(0, promotion.ExitCode);
            var baselineId = JsonDocument.Parse(promotion.Output).RootElement.GetProperty("baselineId").GetGuid();
            var baseline = await WorkspaceBaselineService.ShowAsync(root, baselineId.ToString("D"),
                TestContext.Current.CancellationToken);

            var targetConfigurationPath = Path.Combine(root, "systems", "target.yaml");
            var originalTargetConfiguration = await File.ReadAllTextAsync(targetConfigurationPath,
                TestContext.Current.CancellationToken);
            var changedTargetConfiguration = originalTargetConfiguration.Replace(
                "PROOFSHIFT_TARGET_CONNECTION", "PROOFSHIFT_REVIEWED_TARGET_CONNECTION",
                StringComparison.Ordinal);
            await File.WriteAllTextAsync(targetConfigurationPath, changedTargetConfiguration,
                TestContext.Current.CancellationToken);
            var driftPlan = await WorkspaceEvaluationService.PlanAsync(root, TestContext.Current.CancellationToken);
            var drift = await WorkspaceSemanticDriftService.CompareAsync(root, driftPlan, "active",
                TestContext.Current.CancellationToken);
            Assert.Equal(WorkspaceSemanticDriftStatus.ReviewRequired, drift.Status);
            var review = await WorkspacePlanReviewService.ReviewAsync(root, "active",
                TestContext.Current.CancellationToken);

            var candidateRunId = await CreatePackageFixtureRunAsync(root, "QUALIFIED", EvidenceResult.Pass);
            var workspace = await EvaluationWorkspaceManifestStore.ReadAsync(root)
                ?? throw new InvalidDataException("Test workspace manifest is missing.");
            var driftArtifact = await WorkspaceSemanticDriftArtifactStore.WriteAsync(root, workspace.WorkspaceId,
                candidateRunId, drift, TestContext.Current.CancellationToken);
            var candidateRunDirectory = Path.Combine(root, "runs", candidateRunId.ToString("D"));
            var candidateRun = await WorkspaceRunManifestStore.ReadAsync(candidateRunDirectory,
                TestContext.Current.CancellationToken);
            candidateRun = candidateRun with
            {
                BaselineId = baseline.BaselineId,
                BaselineRunId = baseline.RunId,
                BaselineRunManifestIdentityFingerprint = baseline.RunManifestIdentityFingerprint,
                BaselinePlanFingerprint = baseline.EffectivePlanFingerprint,
                BaselineEvidencePackageIdentity = baseline.EvidencePackageIdentity,
                PreRunSemanticDriftIdentityFingerprint = WorkspacePlanReviewService.IdentityFingerprint(drift),
                PreRunSemanticDriftStatus = drift.Status.ToString(),
                SemanticDriftArtifactReference = driftArtifact.Reference,
                SemanticDriftArtifactIntegrityHash = driftArtifact.IntegrityHash,
                ReviewRecordId = review.Review.ReviewId,
                ReviewRecordIntegrityHash = review.Review.IntegrityHash,
                ReviewDecision = review.Review.Decision
            };
            await WorkspaceRunManifestStore.WriteAsync(candidateRunDirectory, candidateRun, createNew: false,
                TestContext.Current.CancellationToken);
            candidateRun = await WorkspaceRunManifestStore.ReadAsync(candidateRunDirectory,
                TestContext.Current.CancellationToken);
            var outcome = await WorkspaceBaselineComparisonService.CompareAndPersistAsync(root, candidateRun,
                TestContext.Current.CancellationToken);
            Assert.NotNull(outcome);

            var candidatePackagePath = Path.Combine(root, "candidate.zip");
            var candidatePackage = await WorkspaceEvidencePackageService.CreateAsync(root,
                candidateRunId.ToString("D"), candidatePackagePath, diagnostic: false,
                TestContext.Current.CancellationToken);
            Assert.Equal(candidatePackage.PackageIdentity,
                (await WorkspaceEvidencePackageService.CreateAsync(root, candidateRunId.ToString("D"),
                    Path.Combine(root, "candidate-repeat.zip"), diagnostic: false,
                    TestContext.Current.CancellationToken)).PackageIdentity);
            var validation = await WorkspaceEvidencePackageService.ValidateAsync(candidatePackagePath,
                TestContext.Current.CancellationToken);
            Assert.True(validation.Valid, string.Join("; ", validation.Failures));
            var candidateEntries = await ReadPackageEntriesAsync(candidatePackagePath);
            using (var packageManifest = JsonDocument.Parse(candidateEntries["package.json"]))
            {
                var roles = packageManifest.RootElement.GetProperty("artifacts").EnumerateArray()
                    .Select(item => item.GetProperty("role").GetString()).ToHashSet(StringComparer.Ordinal);
                Assert.Contains("baseline-reference", roles);
                Assert.Contains("pre-run-semantic-drift", roles);
                Assert.Contains("plan-review", roles);
                Assert.Contains("baseline-outcome-comparison", roles);
                Assert.Equal("proofshift-baseline-governance-package-v1",
                    packageManifest.RootElement.GetProperty("governanceFormat").GetString());
            }

            var offline = await WorkspacePackageComparisonService.CompareAsync(baselinePackagePath,
                candidatePackagePath, TestContext.Current.CancellationToken);
            Assert.Equal("BaselineGoverned", offline.ComparisonMode);
            Assert.True(offline.BaselineGovernanceBindingValid);
            Assert.Equal(baselinePackage.PackageIdentity, offline.BaselinePackageIdentity);
            Assert.Equal("Reviewed", offline.ReviewDecision);
            Assert.Equal("ChangedStillQualified", offline.OutcomeClassification);
            Assert.False(offline.DetailedComparison.MaterializedStateFindingAdded);
            var cliComparison = await RunAsync("workspace", "evidence", "compare-packages",
                baselinePackagePath, candidatePackagePath, "--json");
            Assert.Equal(0, cliComparison.ExitCode);
            using (var cliJson = JsonDocument.Parse(cliComparison.Output))
                Assert.Equal("BaselineGoverned", cliJson.RootElement.GetProperty("comparisonMode").GetString());

            var defectiveRunId = await CreatePackageFixtureRunAsync(root, "NOT QUALIFIED", EvidenceResult.Fail,
                detailedFindings: true);
            var defectiveDrift = await WorkspaceSemanticDriftArtifactStore.WriteAsync(root, workspace.WorkspaceId,
                defectiveRunId, drift, TestContext.Current.CancellationToken);
            var defectiveRunDirectory = Path.Combine(root, "runs", defectiveRunId.ToString("D"));
            var defectiveRun = await WorkspaceRunManifestStore.ReadAsync(defectiveRunDirectory,
                TestContext.Current.CancellationToken);
            defectiveRun = defectiveRun with
            {
                BaselineId = baseline.BaselineId,
                BaselineRunId = baseline.RunId,
                BaselineRunManifestIdentityFingerprint = baseline.RunManifestIdentityFingerprint,
                BaselinePlanFingerprint = baseline.EffectivePlanFingerprint,
                BaselineEvidencePackageIdentity = baseline.EvidencePackageIdentity,
                PreRunSemanticDriftIdentityFingerprint = WorkspacePlanReviewService.IdentityFingerprint(drift),
                PreRunSemanticDriftStatus = drift.Status.ToString(),
                SemanticDriftArtifactReference = defectiveDrift.Reference,
                SemanticDriftArtifactIntegrityHash = defectiveDrift.IntegrityHash,
                ReviewRecordId = review.Review.ReviewId,
                ReviewRecordIntegrityHash = review.Review.IntegrityHash,
                ReviewDecision = review.Review.Decision
            };
            await WorkspaceRunManifestStore.WriteAsync(defectiveRunDirectory, defectiveRun, createNew: false,
                TestContext.Current.CancellationToken);
            defectiveRun = await WorkspaceRunManifestStore.ReadAsync(defectiveRunDirectory,
                TestContext.Current.CancellationToken);
            Assert.NotNull(await WorkspaceBaselineComparisonService.CompareAndPersistAsync(root, defectiveRun,
                TestContext.Current.CancellationToken));
            var defectivePackagePath = Path.Combine(root, "defective-candidate.zip");
            await WorkspaceEvidencePackageService.CreateAsync(root, defectiveRunId.ToString("D"),
                defectivePackagePath, diagnostic: false, TestContext.Current.CancellationToken);
            var defectiveOffline = await WorkspacePackageComparisonService.CompareAsync(baselinePackagePath,
                defectivePackagePath, TestContext.Current.CancellationToken);
            Assert.Equal("Regressed", defectiveOffline.OutcomeClassification);
            Assert.True(defectiveOffline.MaterializedStateFindingAdded);
            Assert.Contains(defectiveOffline.DetailedComparison.Findings, item =>
                item.Category == "Added" && item.RuleId == "proofshift.verification.materialized-state");
            Assert.Contains(defectiveOffline.DetailedComparison.ExpectedExceptions, item =>
                item.Category == "Added");

            var interruptedRunId = await CreatePackageFixtureRunAsync(root, "QUALIFIED", EvidenceResult.Pass);
            var interruptedDrift = await WorkspaceSemanticDriftArtifactStore.WriteAsync(root,
                workspace.WorkspaceId, interruptedRunId, drift, TestContext.Current.CancellationToken);
            var interruptedDirectory = Path.Combine(root, "runs", interruptedRunId.ToString("D"));
            var interrupted = await WorkspaceRunManifestStore.ReadAsync(interruptedDirectory,
                TestContext.Current.CancellationToken);
            interrupted = interrupted with
            {
                State = "Interrupted",
                Disposition = null,
                BaselineId = baseline.BaselineId,
                BaselineRunId = baseline.RunId,
                BaselineRunManifestIdentityFingerprint = baseline.RunManifestIdentityFingerprint,
                BaselinePlanFingerprint = baseline.EffectivePlanFingerprint,
                BaselineEvidencePackageIdentity = baseline.EvidencePackageIdentity,
                PreRunSemanticDriftIdentityFingerprint = WorkspacePlanReviewService.IdentityFingerprint(drift),
                PreRunSemanticDriftStatus = drift.Status.ToString(),
                SemanticDriftArtifactReference = interruptedDrift.Reference,
                SemanticDriftArtifactIntegrityHash = interruptedDrift.IntegrityHash,
                ReviewRecordId = review.Review.ReviewId,
                ReviewRecordIntegrityHash = review.Review.IntegrityHash,
                ReviewDecision = review.Review.Decision
            };
            await WorkspaceRunManifestStore.WriteAsync(interruptedDirectory, interrupted, createNew: false,
                TestContext.Current.CancellationToken);
            var diagnosticPath = Path.Combine(root, "interrupted-diagnostic.zip");
            await WorkspaceEvidencePackageService.CreateAsync(root, interruptedRunId.ToString("D"),
                diagnosticPath, diagnostic: true, TestContext.Current.CancellationToken);
            var diagnosticValidation = await WorkspaceEvidencePackageService.ValidateAsync(diagnosticPath,
                TestContext.Current.CancellationToken);
            Assert.True(diagnosticValidation.Valid, string.Join("; ", diagnosticValidation.Failures));
            var diagnosticEntries = await ReadPackageEntriesAsync(diagnosticPath);
            using (var diagnosticManifest = JsonDocument.Parse(diagnosticEntries["package.json"]))
            {
                Assert.Equal("diagnostic", diagnosticManifest.RootElement.GetProperty("packageType").GetString());
                Assert.DoesNotContain(diagnosticManifest.RootElement.GetProperty("artifacts").EnumerateArray(),
                    item => item.GetProperty("role").GetString() == "baseline-outcome-comparison");
            }

            var unrelatedBaselineRun = await CreatePackageFixtureRunAsync(root, "QUALIFIED", EvidenceResult.Pass);
            var unrelatedBaselinePackage = await WorkspaceEvidencePackageService.CreateAsync(root,
                unrelatedBaselineRun.ToString("D"), null, diagnostic: false,
                TestContext.Current.CancellationToken);
            var unrelatedBaselinePath = unrelatedBaselinePackage.PackagePath;
            var unrelatedPromotion = await RunAsync("workspace", "baseline", "promote", root,
                unrelatedBaselineRun.ToString("D"), "--json");
            Assert.Equal(0, unrelatedPromotion.ExitCode);
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                WorkspacePackageComparisonService.CompareAsync(unrelatedBaselinePath, candidatePackagePath,
                    TestContext.Current.CancellationToken));

            var unrelatedCandidateRun = await CreatePackageFixtureRunAsync(root, "QUALIFIED", EvidenceResult.Pass);
            var unrelatedCandidatePath = Path.Combine(root, "unrelated-candidate.zip");
            await WorkspaceEvidencePackageService.CreateAsync(root, unrelatedCandidateRun.ToString("D"),
                unrelatedCandidatePath, diagnostic: false, TestContext.Current.CancellationToken);
            var adHoc = await WorkspacePackageComparisonService.CompareAsync(baselinePackagePath,
                unrelatedCandidatePath, TestContext.Current.CancellationToken);
            Assert.Equal("AdHoc", adHoc.ComparisonMode);
            Assert.Null(adHoc.BaselineGovernanceBindingValid);
            Assert.Null(adHoc.OutcomeClassification);

            var currentTargetConfiguration = await File.ReadAllTextAsync(targetConfigurationPath,
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(targetConfigurationPath,
                currentTargetConfiguration.Replace("PROOFSHIFT_REVIEWED_TARGET_CONNECTION",
                    "PROOFSHIFT_OTHER_REVIEW_TARGET_CONNECTION", StringComparison.Ordinal),
                TestContext.Current.CancellationToken);
            var substitutedReview = await WorkspacePlanReviewService.ReviewAsync(root, "active",
                TestContext.Current.CancellationToken);
            Assert.NotEqual(review.Review.ReviewId, substitutedReview.Review.ReviewId);
            await RejectArtifactSubstitutionAsync("plan-review",
                WorkspacePlanReviewService.SerializeRecord(substitutedReview.Review));
            await RejectArtifactSubstitutionAsync("pre-run-semantic-drift",
                await ReadGovernanceArtifactFromPackageAsync(diagnosticPath, "pre-run-semantic-drift"));
            await RejectArtifactSubstitutionAsync("baseline-outcome-comparison",
                await ReadGovernanceArtifactFromPackageAsync(defectivePackagePath,
                    "baseline-outcome-comparison"));
            await RejectArtifactSubstitutionAsync("baseline-outcome-comparison",
                Encoding.UTF8.GetBytes("comparison from another pair"));
            var otherBaselineId = JsonDocument.Parse(unrelatedPromotion.Output).RootElement
                .GetProperty("baselineId").GetGuid();
            await RejectArtifactSubstitutionAsync("baseline-reference",
                await File.ReadAllBytesAsync(WorkspaceBaselineService.GetBaselineRecordPath(root, otherBaselineId),
                    TestContext.Current.CancellationToken));
            var summaryText = Encoding.UTF8.GetString(candidateEntries["artifacts/summary.md"])
                .Replace("ChangedStillQualified", "Improved", StringComparison.Ordinal);
            await RejectArtifactSubstitutionAsync(WorkspaceEvaluationSummaryService.SummaryRole,
                Encoding.UTF8.GetBytes(summaryText));

            async Task RejectArtifactSubstitutionAsync(string role, byte[] replacement)
            {
                var tamperedEntries = await ReadPackageEntriesAsync(candidatePackagePath);
                using var candidateManifest = JsonDocument.Parse(tamperedEntries["package.json"]);
                var artifactPath = candidateManifest.RootElement.GetProperty("artifacts").EnumerateArray()
                    .Single(item => item.GetProperty("role").GetString() == role)
                    .GetProperty("path").GetString()!;
                tamperedEntries[artifactPath] = replacement;
                var tamperedPath = Path.Combine(root, $"tampered-{role}.zip");
                await WritePackageEntriesAsync(tamperedPath, tamperedEntries);
                var result = await WorkspaceEvidencePackageService.ValidateAsync(tamperedPath,
                    TestContext.Current.CancellationToken);
                Assert.False(result.Valid, $"Tampered {role} artifact unexpectedly validated.");
            }

            async Task<byte[]> ReadGovernanceArtifactFromPackageAsync(string packagePath, string role)
            {
                var entries = await ReadPackageEntriesAsync(packagePath);
                using var sourceManifest = JsonDocument.Parse(entries["package.json"]);
                var artifactPath = sourceManifest.RootElement.GetProperty("artifacts").EnumerateArray()
                    .Single(item => item.GetProperty("role").GetString() == role)
                    .GetProperty("path").GetString()!;
                return entries[artifactPath];
            }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WorkspaceCleanupPreviewsAndRemovesOnlyOwnedTemporaryData()
    {
        var root = Path.Combine(Path.GetTempPath(), $"proofshift-workspace-cleanup-{Guid.NewGuid():N}");
        try
        {
            Assert.Equal(0, (await RunAsync("workspace", "init", root)).ExitCode);
            var runId = await CreatePackageFixtureRunAsync(root, "NOT QUALIFIED", EvidenceResult.Fail);
            var runDirectory = Path.Combine(root, "runs", runId.ToString("D"));
            var package = Path.Combine(root, "evidence", "retained.zip");
            await WorkspaceEvidencePackageService.CreateAsync(root, runId.ToString("D"), package,
                diagnostic: false, TestContext.Current.CancellationToken);
            var baselinePackage = await WorkspaceEvidencePackageService.CreateAsync(root, runId.ToString("D"),
                null, diagnostic: false, TestContext.Current.CancellationToken);
            var baselinePromotion = await RunAsync("workspace", "baseline", "promote", root,
                runId.ToString("D"), "--json");
            Assert.Equal(0, baselinePromotion.ExitCode);
            var activeBaselineId = JsonDocument.Parse(baselinePromotion.Output)
                .RootElement.GetProperty("baselineId").GetGuid();
            var baselinePackageBytes = await File.ReadAllBytesAsync(baselinePackage.PackagePath,
                TestContext.Current.CancellationToken);
            var run = await WorkspaceRunManifestStore.ReadAsync(runDirectory, TestContext.Current.CancellationToken);
            await WorkspaceCleanupService.MarkTemporaryOwnedAsync(runDirectory, run,
                TestContext.Current.CancellationToken);
            var temporaryData = Path.Combine(runDirectory, "temporary", "scratch.db");
            await File.WriteAllTextAsync(temporaryData, "scratch", TestContext.Current.CancellationToken);

            var cliPreview = await RunAsync("workspace", "cleanup", root, runId.ToString("D"), "--json");
            Assert.Equal(0, cliPreview.ExitCode);
            Assert.Contains("\"executed\":false", cliPreview.Output, StringComparison.Ordinal);
            Assert.True(File.Exists(temporaryData));
            var preview = await WorkspaceCleanupService.RunAsync(root, runId.ToString("D"), execute: false,
                TestContext.Current.CancellationToken);
            Assert.False(preview.Executed);
            Assert.Equal("Ready", Assert.Single(preview.Resources).Status);
            Assert.True(File.Exists(temporaryData));

            var cliExecute = await RunAsync("workspace", "cleanup", root, runId.ToString("D"),
                "--execute", "--json");
            Assert.Equal(0, cliExecute.ExitCode);
            using var executed = JsonDocument.Parse(cliExecute.Output);
            Assert.True(executed.RootElement.GetProperty("executed").GetBoolean());
            Assert.Equal("Removed", executed.RootElement.GetProperty("resources")[0].GetProperty("status").GetString());
            Assert.False(Directory.Exists(Path.Combine(runDirectory, "temporary")));
            Assert.True(File.Exists(Path.Combine(runDirectory, "run.json")));
            Assert.True(File.Exists(package));
            Assert.True(File.Exists(Path.Combine(runDirectory, "evidence",
                (await WorkspaceRunManifestStore.ReadAsync(runDirectory, TestContext.Current.CancellationToken))
                .VerificationRunId!.Replace("-", string.Empty, StringComparison.Ordinal), "manifest.json")));
            Assert.True((await WorkspaceEvidencePackageService.ValidateAsync(package,
                TestContext.Current.CancellationToken)).Valid);
            Assert.Equal(baselinePackageBytes, await File.ReadAllBytesAsync(baselinePackage.PackagePath,
                TestContext.Current.CancellationToken));
            Assert.Equal(activeBaselineId, (await WorkspaceBaselineService.ActiveAsync(root,
                TestContext.Current.CancellationToken))!.BaselineId);

            var retried = await WorkspaceCleanupService.RunAsync(root, runId.ToString("D"), execute: true,
                TestContext.Current.CancellationToken);
            Assert.True(retried.SafeToExecute);
            Assert.Equal("AlreadyAbsent", Assert.Single(retried.Resources).Status);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WorkspaceCleanupRefusesTemporaryDataWithoutMatchingOwnershipMarker()
    {
        var root = Path.Combine(Path.GetTempPath(), $"proofshift-workspace-cleanup-unowned-{Guid.NewGuid():N}");
        try
        {
            Assert.Equal(0, (await RunAsync("workspace", "init", root)).ExitCode);
            var runId = await CreatePackageFixtureRunAsync(root, "NOT QUALIFIED", EvidenceResult.Fail);
            var temporary = Path.Combine(root, "runs", runId.ToString("D"), "temporary");
            Directory.CreateDirectory(temporary);
            var userFile = Path.Combine(temporary, "unowned.txt");
            await File.WriteAllTextAsync(userFile, "preserve", TestContext.Current.CancellationToken);

            var preview = await WorkspaceCleanupService.RunAsync(root, runId.ToString("D"), execute: false,
                TestContext.Current.CancellationToken);
            var refused = await WorkspaceCleanupService.RunAsync(root, runId.ToString("D"), execute: true,
                TestContext.Current.CancellationToken);

            Assert.Equal("OwnershipUnverified", Assert.Single(preview.Resources).Status);
            Assert.False(refused.SafeToExecute);
            Assert.False(refused.Executed);
            Assert.True(File.Exists(userFile));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WorkspaceEvidencePackageRejectsTamperingAndUnsafeArchivePaths()
    {
        var root = Path.Combine(Path.GetTempPath(), $"proofshift-evidence-package-tamper-{Guid.NewGuid():N}");
        try
        {
            Assert.Equal(0, (await RunAsync("workspace", "init", root)).ExitCode);
            var firstRun = await CreatePackageFixtureRunAsync(root, "QUALIFIED", EvidenceResult.Pass);
            var secondRun = await CreatePackageFixtureRunAsync(root, "NOT QUALIFIED", EvidenceResult.Fail);
            var firstPackage = Path.Combine(root, "evidence", "first.zip");
            await WorkspaceEvidencePackageService.CreateAsync(root, firstRun.ToString("D"),
                firstPackage, diagnostic: false, TestContext.Current.CancellationToken);
            var secondRunManifest = await File.ReadAllBytesAsync(
                Path.Combine(root, "runs", secondRun.ToString("D"), "run.json"),
                TestContext.Current.CancellationToken);

            var modifiedEvidence = await ReadPackageEntriesAsync(firstPackage);
            modifiedEvidence["artifacts/evidence/evidence.ndjson"][0] ^= 0x01;
            Assert.False((await ValidateChangedPackageAsync(root, "modified-evidence.zip", modifiedEvidence)).Valid);

            var modifiedSummary = await ReadPackageEntriesAsync(firstPackage);
            modifiedSummary[WorkspaceEvaluationSummaryService.SummaryPath][0] ^= 0x01;
            Assert.False((await ValidateChangedPackageAsync(root, "modified-summary.zip", modifiedSummary)).Valid);

            var deletedArtifact = await ReadPackageEntriesAsync(firstPackage);
            deletedArtifact.Remove("artifacts/evidence/evidence.ndjson");
            Assert.False((await ValidateChangedPackageAsync(root, "deleted-evidence.zip", deletedArtifact)).Valid);

            var modifiedManifest = await ReadPackageEntriesAsync(firstPackage);
            var packageManifest = System.Text.Encoding.UTF8.GetString(modifiedManifest["package.json"]);
            modifiedManifest["package.json"] = System.Text.Encoding.UTF8.GetBytes(packageManifest.Replace(
                "\"disposition\": \"QUALIFIED\"", "\"disposition\": \"NOT QUALIFIED\"", StringComparison.Ordinal));
            Assert.False((await ValidateChangedPackageAsync(root, "modified-manifest.zip", modifiedManifest)).Valid);

            var substitutedRun = await ReadPackageEntriesAsync(firstPackage);
            substitutedRun["artifacts/run.json"] = secondRunManifest;
            Assert.False((await ValidateChangedPackageAsync(root, "substituted-run.zip", substitutedRun)).Valid);

            var unsafePath = await ReadPackageEntriesAsync(firstPackage);
            unsafePath.Add("../escape.txt", [0x41]);
            Assert.False((await ValidateChangedPackageAsync(root, "unsafe-path.zip", unsafePath)).Valid);

            var unexpectedArtifact = await ReadPackageEntriesAsync(firstPackage);
            var packageManifestJson = System.Text.Json.Nodes.JsonNode.Parse(
                Encoding.UTF8.GetString(unexpectedArtifact["package.json"]))!.AsObject();
            packageManifestJson["artifacts"]!.AsArray().Add(new System.Text.Json.Nodes.JsonObject
            {
                ["role"] = "source-data",
                ["classification"] = "embedded",
                ["path"] = "artifacts/source/customer.csv",
                ["sha256"] = new string('0', 64),
                ["length"] = 1,
                ["externalReference"] = null,
                ["reason"] = "Unexpected source data.",
                ["required"] = true
            });
            unexpectedArtifact["package.json"] = Encoding.UTF8.GetBytes(packageManifestJson.ToJsonString());
            unexpectedArtifact.Add("artifacts/source/customer.csv", [0x41]);
            var unexpectedArtifactResult = await ValidateChangedPackageAsync(root, "unexpected-artifact.zip",
                unexpectedArtifact);
            Assert.False(unexpectedArtifactResult.Valid);
            Assert.Contains(unexpectedArtifactResult.Failures,
                failure => failure.Contains("invalid classification or required flag: source-data", StringComparison.Ordinal));

            var duplicatePath = await ReadPackageEntriesAsync(firstPackage);
            duplicatePath.Add("ARTIFACTS/RUN.JSON", [0x41]);
            Assert.False((await ValidateChangedPackageAsync(root, "duplicate-path.zip", duplicatePath)).Valid);

            var symbolicLink = await ReadPackageEntriesAsync(firstPackage);
            symbolicLink.Add("link", [0x41]);
            Assert.False((await ValidateChangedPackageAsync(root, "symbolic-link.zip", symbolicLink, "link")).Valid);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WorkspaceEvidencePackageRequiresExplicitDiagnosticModeForIncompleteRuns()
    {
        var root = Path.Combine(Path.GetTempPath(), $"proofshift-evidence-package-diagnostic-{Guid.NewGuid():N}");
        try
        {
            Assert.Equal(0, (await RunAsync("workspace", "init", root)).ExitCode);
            var runId = await CreatePackageFixtureRunAsync(root, "QUALIFIED", EvidenceResult.Pass);
            var workspace = await EvaluationWorkspaceManifestStore.ReadAsync(root)
                ?? throw new InvalidDataException("Test workspace manifest is missing.");
            var runDirectory = Path.Combine(root, workspace.Paths.Runs, runId.ToString("D"));
            var run = await WorkspaceRunManifestStore.ReadAsync(runDirectory, TestContext.Current.CancellationToken);
            await WorkspaceRunManifestStore.WriteAsync(runDirectory, run with
            {
                State = "Failed",
                Disposition = null,
                FailureCode = "PSWORKRUN_TEST_INCOMPLETE"
            }, createNew: false, TestContext.Current.CancellationToken);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                WorkspaceEvidencePackageService.CreateAsync(root, runId.ToString("D"), null,
                    diagnostic: false, TestContext.Current.CancellationToken));
            var created = await WorkspaceEvidencePackageService.CreateAsync(root, runId.ToString("D"), null,
                diagnostic: true, TestContext.Current.CancellationToken);
            var validation = await WorkspaceEvidencePackageService.ValidateAsync(created.PackagePath,
                TestContext.Current.CancellationToken);

            Assert.True(validation.Valid, string.Join("; ", validation.Failures));
            Assert.Equal("diagnostic", validation.PackageType);
            Assert.Null(validation.Disposition);
            Assert.Equal("Failed", validation.ExecutionState);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WorkspaceReplayReadinessDoesNotResolveOrInspectLiveSource()
    {
        var root = Path.Combine(Path.GetTempPath(), $"proofshift-workspace-replay-readiness-{Guid.NewGuid():N}");
        try
        {
            Assert.Equal(0, (await RunAsync("workspace", "init", root)).ExitCode);
            var sourcePath = Path.Combine(root, "systems", "source.yaml");
            var yaml = new YamlStream();
            yaml.Load(new StringReader(await File.ReadAllTextAsync(sourcePath, TestContext.Current.CancellationToken)));
            var source = (YamlMappingNode)yaml.Documents[0].RootNode;
            var storage = (YamlMappingNode)source.Children[new YamlScalarNode("storage")];
            var endpoint = (YamlMappingNode)storage.Children[new YamlScalarNode("records")];
            endpoint.Children[new YamlScalarNode("root")] = new YamlScalarNode("env:PROOFSHIFT_UNAVAILABLE_SOURCE_ROOT");
            using (var writer = new StringWriter())
            {
                yaml.Save(writer);
                await File.WriteAllTextAsync(sourcePath, writer.ToString(), TestContext.Current.CancellationToken);
            }

            var result = await RunWithEnvironmentAsync("PROOFSHIFT_UNAVAILABLE_SOURCE_ROOT", null,
                "workspace", "run", root, "--checkpoint", Guid.NewGuid().ToString("N"), "--json");

            Assert.Equal(1, result.ExitCode);
            var finalLine = result.Output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Last();
            using var json = JsonDocument.Parse(finalLine);
            Assert.Equal("PreflightBlocked", json.RootElement.GetProperty("state").GetString());
            Assert.Contains(json.RootElement.GetProperty("readinessChecks").EnumerateArray(),
                check => check.GetProperty("category").GetString() == "credentials" &&
                    check.GetProperty("component").GetString() == "source/records" &&
                    check.GetProperty("status").GetString() == "READY");
            Assert.Contains(json.RootElement.GetProperty("readinessChecks").EnumerateArray(),
                check => check.GetProperty("category").GetString() == "connectivity" &&
                    check.GetProperty("component").GetString() == "source-records" &&
                    check.GetProperty("status").GetString() == "READY");
            Assert.DoesNotContain("PROOFSHIFT_UNAVAILABLE_SOURCE_ROOT", result.Output, StringComparison.Ordinal);
            Assert.False(Directory.Exists(Path.Combine(root, ".proofshift", "checkpoints")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WorkspacePreflightBlocksUnapprovedRequiredMappingsWithoutChangingStaticValidation()
    {
        var root = Path.Combine(Path.GetTempPath(), $"proofshift-workspace-mapping-{Guid.NewGuid():N}");
        try
        {
            Assert.Equal(0, (await RunAsync("workspace", "init", root)).ExitCode);
            var graphPath = Path.Combine(root, "migration", "graph.yaml");
            var yaml = new YamlStream();
            yaml.Load(new StringReader(await File.ReadAllTextAsync(graphPath, TestContext.Current.CancellationToken)));
            var graph = (YamlMappingNode)yaml.Documents[0].RootNode;
            var edges = (YamlMappingNode)graph.Children[new YamlScalarNode("edges")];
            var edge = (YamlMappingNode)edges.Children.Values.Single();
            var operation = (YamlMappingNode)edge.Children[new YamlScalarNode("operation")];
            var fields = (YamlMappingNode)operation.Children[new YamlScalarNode("fields")];
            var field = (YamlMappingNode)fields.Children.Values.First();
            field.Add(new YamlScalarNode("approval"), new YamlScalarNode("suggested"));
            using (var writer = new StringWriter())
            {
                yaml.Save(writer);
                await File.WriteAllTextAsync(graphPath, writer.ToString(), TestContext.Current.CancellationToken);
            }

            var validation = await RunAsync("workspace", "validate", root, "--json");
            Assert.Equal(0, validation.ExitCode);
            var preflight = await RunWithEnvironmentAsync("PROOFSHIFT_TARGET_CONNECTION", "synthetic-only",
                "workspace", "preflight", root, "--json");
            Assert.Equal(1, preflight.ExitCode);
            using var result = JsonDocument.Parse(preflight.Output);
            Assert.Equal("BLOCKED", result.RootElement.GetProperty("status").GetString());
            Assert.Contains(result.RootElement.GetProperty("checks").EnumerateArray(),
                check => check.GetProperty("id").GetString() == "PSAUTHOR001" &&
                    check.GetProperty("category").GetString() == "mappings");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WorkspaceReportsNeverSerializeLiteralCredentialMaterial()
    {
        var root = Path.Combine(Path.GetTempPath(), $"proofshift-workspace-redaction-{Guid.NewGuid():N}");
        const string password = "redaction-password-42";
        const string token = "redaction-token-43";
        const string sas = "redaction-sas-44";
        const string privateKey = "redaction-private-key-45";
        var credential = $"Host=127.0.0.1;Password={password};Token={token};SharedAccessSignature={sas};PrivateKey={privateKey}";
        try
        {
            Assert.Equal(0, (await RunAsync("workspace", "init", root)).ExitCode);
            var targetPath = Path.Combine(root, "systems", "target.yaml");
            var yaml = new YamlStream();
            yaml.Load(new StringReader(await File.ReadAllTextAsync(targetPath, TestContext.Current.CancellationToken)));
            var target = (YamlMappingNode)yaml.Documents[0].RootNode;
            var storage = (YamlMappingNode)target.Children[new YamlScalarNode("storage")];
            var endpoint = (YamlMappingNode)storage.Children[new YamlScalarNode("records")];
            endpoint.Children[new YamlScalarNode("connection")] = new YamlScalarNode(credential);
            using (var writer = new StringWriter())
            {
                yaml.Save(writer);
                await File.WriteAllTextAsync(targetPath, writer.ToString(), TestContext.Current.CancellationToken);
            }

            var validate = await RunAsync("workspace", "validate", root, "--json");
            var preflight = await RunAsync("workspace", "preflight", root, "--json");
            var plan = await RunAsync("workspace", "plan", root, "--json");
            Assert.Equal(1, validate.ExitCode);
            Assert.Equal(1, preflight.ExitCode);
            Assert.Equal(1, plan.ExitCode);
            foreach (var output in new[] { validate.Output, preflight.Output, plan.Output })
            {
                Assert.DoesNotContain(credential, output, StringComparison.Ordinal);
                Assert.DoesNotContain(password, output, StringComparison.Ordinal);
                Assert.DoesNotContain(token, output, StringComparison.Ordinal);
                Assert.DoesNotContain(sas, output, StringComparison.Ordinal);
                Assert.DoesNotContain(privateKey, output, StringComparison.Ordinal);
            }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RuleCatalogAndSchemaAreDeterministicAndUnknownRulesFailClosed()
    {
        var first = await RunAsync("rules", "schema");
        var second = await RunAsync("rules", "schema");
        Assert.Equal(0, first.ExitCode);
        Assert.Equal(first.Output, second.Output);
        using var schema = JsonDocument.Parse(first.Output);
        Assert.Equal(2, schema.RootElement.GetProperty("properties").GetProperty("version").GetProperty("const").GetInt32());
        var description = await RunAsync("rules", "describe", "pension-contribution-total", "--json");
        using var descriptor = JsonDocument.Parse(description.Output);
        var tolerance = descriptor.RootElement.GetProperty("options").EnumerateArray()
            .Single(option => option.GetProperty("name").GetString() == "tolerance");
        Assert.Equal(0.01m, tolerance.GetProperty("schema").GetProperty("default").GetDecimal());
        var unknown = await RunAsync("rules", "describe", "not-installed");
        Assert.Equal(1, unknown.ExitCode);
        Assert.Contains("PSRULE008", unknown.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FivePacksShareDeterministicAuthoringAndEffectiveConfiguration()
    {
        var root = Path.Combine(Path.GetTempPath(), $"proofshift-five-pack-authoring-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var firstPackListing = await RunAsync("packs", "list", "--json");
            var secondPackListing = await RunAsync("packs", "list", "--json");
            Assert.Equal(0, firstPackListing.ExitCode);
            Assert.Equal(firstPackListing.Output, secondPackListing.Output);
            using var packs = JsonDocument.Parse(firstPackListing.Output);
            var packEntries = packs.RootElement.EnumerateArray().ToArray();
            var packIds = packEntries.Select(pack => pack.GetProperty("id").GetString()!).ToArray();
            Assert.Equal(packIds.Order(StringComparer.Ordinal), packIds);
            Assert.Equal(ExpectedPackIds, packIds);

            var firstRuleListing = await RunAsync("rules", "list", "--json");
            var secondRuleListing = await RunAsync("rules", "list", "--json");
            Assert.Equal(0, firstRuleListing.ExitCode);
            Assert.Equal(firstRuleListing.Output, secondRuleListing.Output);
            using var rules = JsonDocument.Parse(firstRuleListing.Output);
            var ruleEntries = rules.RootElement.EnumerateArray().ToArray();
            var ruleTypes = ruleEntries.Select(rule => rule.GetProperty("type").GetString()!).ToArray();
            Assert.Equal(ruleTypes.Order(StringComparer.Ordinal), ruleTypes);

            var allPackSchemaTypes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var pack in packEntries)
            {
                var packId = pack.GetProperty("id").GetString()!;
                var packVersion = pack.GetProperty("version").GetString()!;
                var description = await RunAsync("packs", "describe", packId, "--json");
                Assert.Equal(0, description.ExitCode);
                using var metadata = JsonDocument.Parse(description.Output);
                Assert.Equal(packId, metadata.RootElement.GetProperty("id").GetString());
                Assert.Equal(packVersion, metadata.RootElement.GetProperty("version").GetString());
                Assert.NotEmpty(metadata.RootElement.GetProperty("concepts").EnumerateArray());

                var ownedRules = metadata.RootElement.GetProperty("ruleProviders").EnumerateArray()
                    .Single().GetProperty("rules").EnumerateArray()
                    .Select(rule => (Type: rule.GetProperty("type").GetString()!, Version: rule.GetProperty("version").GetString()!))
                    .OrderBy(rule => rule.Type, StringComparer.Ordinal).ToArray();
                Assert.NotEmpty(ownedRules);
                var representative = await RunAsync("rules", "describe", ownedRules[0].Type, "--json");
                Assert.Equal(0, representative.ExitCode);
                using var ruleDescription = JsonDocument.Parse(representative.Output);
                Assert.Equal(packId, ruleDescription.RootElement.GetProperty("provider").GetString());
                Assert.Equal(ownedRules[0].Version, ruleDescription.RootElement.GetProperty("version").GetString());

                var schema = await RunAsync("packs", "schema", packId);
                Assert.Equal(0, schema.ExitCode);
                using var rulesSchema = JsonDocument.Parse(schema.Output);
                var schemaTypes = rulesSchema.RootElement.GetProperty("properties").GetProperty("rules")
                    .GetProperty("additionalProperties").GetProperty("oneOf").EnumerateArray()
                    .Select(rule => rule.GetProperty("properties").GetProperty("type").GetProperty("const").GetString()!)
                    .Order(StringComparer.Ordinal).ToArray();
                Assert.Equal(ownedRules.Select(rule => rule.Type).Order(StringComparer.Ordinal), schemaTypes);
                foreach (var type in schemaTypes)
                {
                    Assert.True(allPackSchemaTypes.Add(type), $"Rule type '{type}' is contributed by multiple pack schemas.");
                    Assert.Contains(ruleEntries, entry => entry.GetProperty("type").GetString() == type &&
                        entry.GetProperty("provider").GetString() == packId);
                }

                var projectRoot = Path.Combine(root, packId[(packId.LastIndexOf('.') + 1)..]);
                Assert.Equal(0, (await RunAsync("init", projectRoot)).ExitCode);
                var projectPath = Path.Combine(projectRoot, "proofshift.yaml");
                await SetPackSelectionAsync(projectPath, packId, packVersion);
                var effective = await RunAsync("config", "show", projectPath, "--effective", "--json");
                Assert.Equal(0, effective.ExitCode);
                using var effectiveConfiguration = JsonDocument.Parse(effective.Output);
                Assert.Equal(packVersion, effectiveConfiguration.RootElement.GetProperty("packs").GetProperty(packId).GetString());
                Assert.NotEmpty(effectiveConfiguration.RootElement.GetProperty("rules").EnumerateArray());
                Assert.Contains(packId, effective.Output, StringComparison.Ordinal);
                Assert.Contains("PROOFSHIFT_TARGET_CONNECTION", effective.Output, StringComparison.Ordinal);
                Assert.DoesNotContain("Password=", effective.Output, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private static async Task SetPackSelectionAsync(string path, string packId, string version)
    {
        var yaml = new YamlStream();
        using (var reader = new StringReader(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)))
            yaml.Load(reader);
        var root = Assert.IsType<YamlMappingNode>(yaml.Documents.Single().RootNode);
        var pack = new YamlMappingNode
        {
            { new YamlScalarNode("id"), new YamlScalarNode(packId) },
            { new YamlScalarNode("version"), new YamlScalarNode(version) }
        };
        root.Children[new YamlScalarNode("packs")] = new YamlSequenceNode(pack);
        using var writer = new StringWriter();
        yaml.Save(writer, assignAnchors: false);
        await File.WriteAllTextAsync(path, writer.ToString(), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CapabilitiesReportEnterpriseReadersAsReadOnlyWithNoShadowWrite()
    {
        var result = await RunAsync("capabilities", "--json");
        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        var db2 = document.RootElement.GetProperty("connectors").EnumerateArray()
            .Single(connector => connector.GetProperty("id").GetString() == "db2");
        Assert.True(db2.GetProperty("discovery").GetBoolean());
        Assert.True(db2.GetProperty("sourceRead").GetBoolean());
        Assert.True(db2.GetProperty("checkpointCapture").GetBoolean());
        Assert.True(db2.GetProperty("targetObservation").GetBoolean());
        Assert.False(db2.GetProperty("shadowWrite").GetBoolean());
        Assert.True(db2.GetProperty("binaryStreaming").GetBoolean());
        var oracle = document.RootElement.GetProperty("connectors").EnumerateArray()
            .Single(connector => connector.GetProperty("id").GetString() == "oracle");
        Assert.True(oracle.GetProperty("discovery").GetBoolean());
        Assert.True(oracle.GetProperty("sourceRead").GetBoolean());
        Assert.True(oracle.GetProperty("checkpointCapture").GetBoolean());
        Assert.True(oracle.GetProperty("targetObservation").GetBoolean());
        Assert.False(oracle.GetProperty("shadowWrite").GetBoolean());
        Assert.True(oracle.GetProperty("binaryStreaming").GetBoolean());
    }

    [Theory]
    [InlineData("oracle")]
    [InlineData("db2")]
    [InlineData("fixed-width")]
    [InlineData("json")]
    [InlineData("ndjson")]
    [InlineData("xml")]
    public async Task ConnectorSchemasExposeClosedEndpointAndSelectorContracts(string connectorId)
    {
        var result = await RunAsync("connectors", "schema", connectorId);
        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        var schema = document.RootElement;
        Assert.Equal("https://json-schema.org/draft/2020-12/schema", schema.GetProperty("$schema").GetString());
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        var endpoint = schema.GetProperty("properties").GetProperty("endpoint");
        Assert.False(endpoint.GetProperty("additionalProperties").GetBoolean());
        var selector = schema.GetProperty("properties").GetProperty("selector");
        var expectedSelectorKind = connectorId is "oracle" or "db2" ? "table" : connectorId is "s3" or "azure-blob" or "sftp" ? "object-pattern" : connectorId;
        Assert.Equal(expectedSelectorKind, selector.GetProperty("properties").GetProperty("kind").GetProperty("const").GetString());
        Assert.False(selector.GetProperty("additionalProperties").GetBoolean());
        Assert.False(selector.GetProperty("properties").GetProperty("properties").GetProperty("additionalProperties").GetBoolean());
    }

    [Fact]
    public async Task StrictValidationRejectsUnknownOracleEndpointPropertyAtConfigurationPath()
    {
        var root = Path.Combine(Path.GetTempPath(), $"proofshift-connector-schema-{Guid.NewGuid():N}");
        try
        {
            Assert.Equal(0, (await RunAsync("init", root)).ExitCode);
            var systemPath = Path.Combine(root, "systems", "source.yaml");
            var systemYaml = new YamlStream();
            systemYaml.Load(new StringReader(await File.ReadAllTextAsync(systemPath, TestContext.Current.CancellationToken)));
            var system = (YamlMappingNode)systemYaml.Documents[0].RootNode;
            var storage = (YamlMappingNode)system["storage"];
            var endpoint = (YamlMappingNode)storage["records"];
            endpoint.Children.Clear();
            endpoint.Add("connector", new YamlScalarNode("oracle"));
            endpoint.Add("connection", new YamlMappingNode { { new YamlScalarNode("secret"), new YamlScalarNode("ORACLE_TEST_CONNECTION") } });
            endpoint.Add("schemma", new YamlScalarNode("APP"));
            using (var writer = new StringWriter())
            {
                systemYaml.Save(writer, assignAnchors: false);
                await File.WriteAllTextAsync(systemPath, writer.ToString(), TestContext.Current.CancellationToken);
            }

            var graphPath = Path.Combine(root, "migration", "graph.yaml");
            var graphYaml = new YamlStream();
            graphYaml.Load(new StringReader(await File.ReadAllTextAsync(graphPath, TestContext.Current.CancellationToken)));
            var graph = (YamlMappingNode)graphYaml.Documents[0].RootNode;
            var nodes = (YamlMappingNode)graph["nodes"];
            var sourceNode = (YamlMappingNode)nodes["source-records"];
            var selector = (YamlMappingNode)sourceNode["selector"];
            selector.Children[new YamlScalarNode("kind")] = new YamlScalarNode("table");
            var selectorProperties = (YamlMappingNode)selector["properties"];
            selectorProperties.Children.Clear();
            selectorProperties.Add("name", new YamlScalarNode("APP.RECORDS"));
            var identities = new YamlSequenceNode { new YamlScalarNode("id") };
            selector.Children[new YamlScalarNode("identity")] = identities;
            using (var writer = new StringWriter())
            {
                graphYaml.Save(writer, assignAnchors: false);
                await File.WriteAllTextAsync(graphPath, writer.ToString(), TestContext.Current.CancellationToken);
            }

            var validation = await RunAsync("validate", Path.Combine(root, "proofshift.yaml"), "--strict", "--json");
            Assert.Equal(1, validation.ExitCode);
            using var result = JsonDocument.Parse(validation.Output);
            var issue = result.RootElement.GetProperty("issues").EnumerateArray()
                .Single(item => item.GetProperty("code").GetString() == "PSCONN021");
            Assert.Equal("systems/source.yaml", issue.GetProperty("file").GetString());
            Assert.Contains("schemma", issue.GetProperty("path").GetString(), StringComparison.Ordinal);
            Assert.DoesNotContain("ORACLE_TEST_CONNECTION", validation.Output, StringComparison.Ordinal);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task StrictValidationReportsMissingEndpointRequirementAndInvalidFixedWidthBoundary()
    {
        var root = Path.Combine(Path.GetTempPath(), $"proofshift-fixed-width-schema-{Guid.NewGuid():N}");
        try
        {
            Assert.Equal(0, (await RunAsync("init", root)).ExitCode);
            var systemPath = Path.Combine(root, "systems", "source.yaml");
            var systemYaml = new YamlStream();
            systemYaml.Load(new StringReader(await File.ReadAllTextAsync(systemPath, TestContext.Current.CancellationToken)));
            var system = (YamlMappingNode)systemYaml.Documents[0].RootNode;
            var storage = (YamlMappingNode)system["storage"];
            var endpoint = (YamlMappingNode)storage["records"];
            endpoint.Children.Clear();
            endpoint.Add("connector", new YamlScalarNode("fixed-width"));
            endpoint.Add("unexpected", new YamlScalarNode("synthetic-value"));
            using (var writer = new StringWriter())
            {
                systemYaml.Save(writer, assignAnchors: false);
                await File.WriteAllTextAsync(systemPath, writer.ToString(), TestContext.Current.CancellationToken);
            }

            var graphPath = Path.Combine(root, "migration", "graph.yaml");
            var graphYaml = new YamlStream();
            graphYaml.Load(new StringReader(await File.ReadAllTextAsync(graphPath, TestContext.Current.CancellationToken)));
            var graph = (YamlMappingNode)graphYaml.Documents[0].RootNode;
            var nodes = (YamlMappingNode)graph["nodes"];
            var sourceNode = (YamlMappingNode)nodes["source-records"];
            var selector = (YamlMappingNode)sourceNode["selector"];
            selector.Children[new YamlScalarNode("kind")] = new YamlScalarNode("fixed-width");
            var properties = (YamlMappingNode)selector["properties"];
            properties.Children.Clear();
            properties.Add("path", new YamlScalarNode("members.txt"));
            properties.Add("width", new YamlScalarNode("0"));
            properties.Add("fields.id.start", new YamlScalarNode("1"));
            properties.Add("fields.id.length", new YamlScalarNode("4"));
            properties.Add("fields.id.type", new YamlScalarNode("integer"));
            selector.Children[new YamlScalarNode("identity")] = new YamlSequenceNode { new YamlScalarNode("id") };
            using (var writer = new StringWriter())
            {
                graphYaml.Save(writer, assignAnchors: false);
                await File.WriteAllTextAsync(graphPath, writer.ToString(), TestContext.Current.CancellationToken);
            }

            var validation = await RunAsync("validate", Path.Combine(root, "proofshift.yaml"), "--strict", "--json");
            Assert.Equal(1, validation.ExitCode);
            using var result = JsonDocument.Parse(validation.Output);
            var issues = result.RootElement.GetProperty("issues").EnumerateArray().ToArray();
            var missing = Assert.Single(issues, issue => issue.GetProperty("code").GetString() == "PSCONN022");
            Assert.Contains("root", missing.GetProperty("path").GetString(), StringComparison.Ordinal);
            var invalid = Assert.Single(issues, issue => issue.GetProperty("code").GetString() == "PSCONN024");
            Assert.Contains("width", invalid.GetProperty("path").GetString(), StringComparison.Ordinal);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task EffectiveProjectRuleAndMappingExplainUseCompiledConfigurationAndNeverResolveSecrets()
    {
        var root = Path.Combine(Path.GetTempPath(), $"proofshift-explain-{Guid.NewGuid():N}");
        try
        {
            Assert.Equal(0, (await RunAsync("init", root)).ExitCode);
            var path = Path.Combine(root, "proofshift.yaml");
            var project = await RunAsync("explain", path, "project", "--json");
            Assert.Equal(0, project.ExitCode);
            using var projectJson = JsonDocument.Parse(project.Output);
            Assert.Equal(64, projectJson.RootElement.GetProperty("configurationHash").GetString()!.Length);
            Assert.Equal(64, projectJson.RootElement.GetProperty("graphHash").GetString()!.Length);
            var rule = await RunAsync("explain", path, "rule", "attributes", "--json");
            Assert.Equal(0, rule.ExitCode);
            Assert.Contains("attribute-comparison", rule.Output, StringComparison.Ordinal);
            var mapping = await RunAsync("explain", path, "mapping", "record-copy", "--json");
            Assert.Equal(0, mapping.ExitCode);
            Assert.Contains("source-records", mapping.Output, StringComparison.Ordinal);
            Assert.Contains("target-records", mapping.Output, StringComparison.Ordinal);
            var effective = await RunAsync("config", "show", path, "--effective", "--json");
            Assert.Equal(0, effective.ExitCode);
            Assert.Contains("PROOFSHIFT_TARGET_CONNECTION", effective.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("Password=", effective.Output, StringComparison.OrdinalIgnoreCase);
            using var effectiveJson = JsonDocument.Parse(effective.Output);
            Assert.Equal(projectJson.RootElement.GetProperty("configurationHash").GetString(),
                effectiveJson.RootElement.GetProperty("configurationHash").GetString());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ScaffoldAndReviewedCsvImportCompileNormallyWhileStrictRejectsSuggestions()
    {
        var root = Path.Combine(Path.GetTempPath(), $"proofshift-scaffold-cli-{Guid.NewGuid():N}");
        try
        {
            var context = new ConnectorContext("logical", "records", new ConnectorId("postgres"), "discovery", "Physical.Uninterpreted", new RuntimeConfiguration([]));
            var source = PhysicalDiscovery.Create(context, "postgres", "0.1.0",
                [new("public", "records", "table", [new("id", "integer", false, 1)], [new("pk", true, ["id"])], [])]);
            var target = PhysicalDiscovery.Create(context, "postgres", "0.1.0", source.Objects);
            var sourcePath = Path.Combine(root, "source-discovery");
            var targetPath = Path.Combine(root, "target-discovery");
            await PhysicalDiscoveryStore.SaveAsync(sourcePath, source, TestContext.Current.CancellationToken);
            await PhysicalDiscoveryStore.SaveAsync(targetPath, target, TestContext.Current.CancellationToken);
            var generated = Path.Combine(root, "suggested");
            var scaffold = await RunAsync("scaffold", "--source-discovery", sourcePath, "--target-discovery", targetPath, "--output", generated);
            Assert.True(scaffold.ExitCode == 0, scaffold.Error);
            var defaultTargetSystem = await File.ReadAllTextAsync(Path.Combine(generated, "systems", "target.yaml"), TestContext.Current.CancellationToken);
            Assert.Contains("\"role\": \"shadow-target\"", defaultTargetSystem, StringComparison.Ordinal);
            var strict = await RunAsync("validate", Path.Combine(generated, "proofshift.yaml"), "--strict", "--json");
            Assert.Equal(1, strict.ExitCode);
            Assert.Contains("PSAUTHOR001", strict.Output, StringComparison.Ordinal);
            var csv = Path.Combine(root, "reviewed.csv");
            await File.WriteAllTextAsync(csv, "Source Entity,Source Field,Target Entity,Target Field,Transformation,Required,Notes,Review Status\npublic.records,id,public.records,id,copy,true,Reviewed,confirmed\n", TestContext.Current.CancellationToken);
            var reviewed = Path.Combine(root, "confirmed");
            var import = await RunAsync("mapping", "import", "--csv", csv, "--source-discovery", sourcePath,
                "--target-discovery", targetPath, "--output", reviewed, "--target-role", "target",
                "--verification-baseline", "generic", "--evaluation-scope-id", "customer-scope",
                "--evaluation-scope-type", "full-declared-source");
            Assert.True(import.ExitCode == 0, import.Error);
            var graph = await File.ReadAllTextAsync(Path.Combine(reviewed, "migration", "graph.yaml"), TestContext.Current.CancellationToken);
            Assert.Contains("\"evaluationScope\"", graph, StringComparison.Ordinal);
            Assert.Contains("\"id\": \"customer-scope\"", graph, StringComparison.Ordinal);
            Assert.Contains("\"type\": \"full-declared-source\"", graph, StringComparison.Ordinal);
            var targetSystem = await File.ReadAllTextAsync(Path.Combine(reviewed, "systems", "target.yaml"), TestContext.Current.CancellationToken);
            Assert.Contains("\"role\": \"target\"", targetSystem, StringComparison.Ordinal);
            var rules = await File.ReadAllTextAsync(Path.Combine(reviewed, "rules", "rules.yaml"), TestContext.Current.CancellationToken);
            Assert.Contains("attribute-comparison", rules, StringComparison.Ordinal);
            Assert.Contains("target-presence", rules, StringComparison.Ordinal);
            var valid = await RunAsync("validate", Path.Combine(reviewed, "proofshift.yaml"), "--strict", "--json");
            Assert.True(valid.ExitCode == 0, valid.Output + valid.Error);
            Assert.Contains("\"valid\": true", valid.Output, StringComparison.Ordinal);
            var incompleteScope = await RunAsync("scaffold", "--source-discovery", sourcePath,
                "--target-discovery", targetPath, "--output", Path.Combine(root, "incomplete-scope"),
                "--evaluation-scope-id", "missing-type");
            Assert.Equal(2, incompleteScope.ExitCode);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task MappingImportEmitsExplicitFieldFanOutEdgesWithoutImplicitMerge()
    {
        var repositoryDirectory = new DirectoryInfo(AppContext.BaseDirectory);
        while (repositoryDirectory is not null && !File.Exists(Path.Combine(repositoryDirectory.FullName, "ProofShift.sln")))
            repositoryDirectory = repositoryDirectory.Parent;
        Assert.NotNull(repositoryDirectory);
        var root = Path.Combine(repositoryDirectory.FullName, "TestResults", $"fanout-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var context = new ConnectorContext("logical", "records", new ConnectorId("postgres"),
                "discovery", "Physical.Uninterpreted", new RuntimeConfiguration([]));
            var source = PhysicalDiscovery.Create(context, "postgres", "0.1.0",
                [new("dbo", "records", "table",
                    [new("id", "integer", false, 1), new("display_name", "text", false, 2)],
                    [new("pk", true, ["id"])], [])]);
            var target = PhysicalDiscovery.Create(context, "postgres", "0.1.0",
                [
                    new("public", "records", "table",
                        [new("id", "integer", false, 1), new("display_name", "text", false, 2)],
                        [new("pk", true, ["id"])], []),
                    new("public", "search", "table",
                        [new("id", "integer", false, 1), new("search_label", "text", false, 2)],
                        [new("pk", true, ["search_label"])], [])
                ]);
            var sourcePath = Path.Combine(root, "source-discovery");
            var targetPath = Path.Combine(root, "target-discovery");
            await PhysicalDiscoveryStore.SaveAsync(sourcePath, source, TestContext.Current.CancellationToken);
            await PhysicalDiscoveryStore.SaveAsync(targetPath, target, TestContext.Current.CancellationToken);
            var csv = Path.Combine(root, "fanout.csv");
            await File.WriteAllTextAsync(csv, """
                Source Entity,Source Field,Target Entity,Target Field,Transformation,Required,Notes,Review Status,Role
                dbo.records,id,public.records,id,copy,true,,confirmed,identity
                dbo.records,display_name,public.records,display_name,copy,true,,confirmed,attribute
                dbo.records,display_name,public.search,search_label,copy,true,,confirmed,identity
                dbo.records,id,public.search,id,copy,true,,confirmed,attribute
                """, TestContext.Current.CancellationToken);
            var project = Path.Combine(root, "project");
            var import = await RunAsync("mapping", "import", "--csv", csv,
                "--source-discovery", sourcePath, "--target-discovery", targetPath,
                "--output", project, "--target-role", "shadow-target", "--verification-baseline", "generic",
                "--evaluation-scope-id", "customer-cohort",
                "--evaluation-scope-type", "full-declared-source");
            Assert.True(import.ExitCode == 0, import.Output + import.Error);

            var graphText = await File.ReadAllTextAsync(
                Path.Combine(project, "migration", "graph.yaml"), TestContext.Current.CancellationToken);
            using var reader = new StringReader(graphText);
            var yaml = new YamlStream();
            yaml.Load(reader);
            var graph = Assert.IsType<YamlMappingNode>(yaml.Documents.Single().RootNode);
            var nodes = Assert.IsType<YamlMappingNode>(graph.Children[new YamlScalarNode("nodes")]);
            var edges = Assert.IsType<YamlMappingNode>(graph.Children[new YamlScalarNode("edges")]);
            Assert.Equal(2, edges.Children.Count);
            Assert.Equal(2, nodes.Children.Keys.Count(node =>
                node is YamlScalarNode scalar && scalar.Value?.StartsWith("target-", StringComparison.Ordinal) == true));
            var emittedTargets = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var edgeNode in edges.Children.Values)
            {
                var edge = Assert.IsType<YamlMappingNode>(edgeNode);
                var sources = Assert.IsType<YamlSequenceNode>(edge.Children[new YamlScalarNode("from")]);
                var targets = Assert.IsType<YamlSequenceNode>(edge.Children[new YamlScalarNode("to")]);
                Assert.Single(sources.Children);
                Assert.Single(targets.Children);
                Assert.Equal("source-0", Assert.IsType<YamlScalarNode>(sources.Children.Single()).Value);
                var targetNode = Assert.IsType<YamlScalarNode>(targets.Children.Single()).Value;
                Assert.NotNull(targetNode);
                emittedTargets.Add(targetNode);
                var operation = Assert.IsType<YamlMappingNode>(edge.Children[new YamlScalarNode("operation")]);
                Assert.Equal("transform", Assert.IsType<YamlScalarNode>(
                    operation.Children[new YamlScalarNode("type")]).Value);
            }
            Assert.Equal(2, emittedTargets.Count);
            Assert.Contains("target-0", emittedTargets);
            Assert.Contains("target-1", emittedTargets);
            var validation = await RunAsync("validate", Path.Combine(project, "proofshift.yaml"), "--strict", "--json");
            Assert.True(validation.ExitCode == 0, validation.Output + validation.Error);

            var loaded = await new ConfigurationLoader(requireEnvironmentValues: false)
                .LoadAsync(Path.Combine(project, "proofshift.yaml"), TestContext.Current.CancellationToken);
            Assert.NotNull(loaded.Configuration);
            var compiled = MigrationGraphCompiler.Compile(loaded.Configuration);
            Assert.True(compiled.IsValid, string.Join(Environment.NewLine, compiled.Issues.Select(issue => issue.Message)));
            var compiledGraph = compiled.Graph!;
            var sourceNode = compiledGraph.Nodes.Single(node => node.Name == "source-0");
            var targetsByName = compiledGraph.Nodes.Where(node => node.Type == MigrationNodeType.Target)
                .ToDictionary(node => node.Name, StringComparer.Ordinal);
            Assert.Equal(2, targetsByName.Count);
            Assert.Contains("target-0", targetsByName.Keys);
            Assert.Contains("target-1", targetsByName.Keys);
            Assert.Equal("search_label", Assert.Single(targetsByName["target-1"].Selector.IdentityFields));

            var edgesByTarget = compiledGraph.Edges.ToDictionary(
                edge => compiledGraph.Nodes.Single(node => edge.Targets.Contains(node.Id)).Name,
                StringComparer.Ordinal);
            Assert.Equal(2, edgesByTarget.Count);
            Assert.Contains("target-0", edgesByTarget.Keys);
            Assert.Contains("target-1", edgesByTarget.Keys);
            Assert.All(edgesByTarget.Values, edge =>
            {
                Assert.Equal(MigrationOperationType.Transform, edge.Operation.Type);
                Assert.Equal(sourceNode.Id, Assert.Single(edge.Sources));
                Assert.Single(edge.Targets);
            });
            var primaryMappings = edgesByTarget["target-0"].Operation.Fields.ToDictionary(field => field.Target, field => field.Source);
            Assert.Equal(2, primaryMappings.Count);
            Assert.Equal("id", primaryMappings["id"]);
            Assert.Equal("display_name", primaryMappings["display_name"]);
            var searchMappings = edgesByTarget["target-1"].Operation.Fields.ToDictionary(field => field.Target, field => field.Source);
            Assert.Equal(2, searchMappings.Count);
            Assert.Equal("id", searchMappings["id"]);
            Assert.Equal("display_name", searchMappings["search_label"]);

            var sourceRecord = new RecordEnvelope(
                new ArtifactReference(new ArtifactId("source-record-1"), sourceNode.SystemId,
                    sourceNode.EndpointId, "record", "id:7"),
                sourceNode.SemanticType,
                [
                    new KeyValuePair<string, ValueNode>("id", new IntegerValue(7)),
                    new KeyValuePair<string, ValueNode>("display_name", new StringValue("Fanout Example"))
                ],
                new ProvenanceMetadata(new ConnectorId("postgres"), sourceNode.EndpointId,
                    "source-record-1", DateTimeOffset.UtcNow));
            var outputs = compiledGraph.Edges.SelectMany(edge => edge.Targets.Select(targetId =>
                GraphTransformationRuntime.Transform(sourceRecord, edge,
                    compiledGraph.Nodes.Single(node => node.Id == targetId)))).ToArray();
            Assert.Equal(2, outputs.Length);
            foreach (var output in outputs)
            {
                Assert.Equal(new IntegerValue(7), output.Values["id"]);
                Assert.Equal("source-record-1", output.Provenance.Metadata["projectionSource"]);
                var edge = compiledGraph.Edges.Single(edge =>
                    edge.Name == output.Provenance.Metadata["projectionEdge"]);
                Assert.Equal(sourceNode.Id, Assert.Single(edge.Sources));
                var targetNode = compiledGraph.Nodes.Single(node => node.Id == Assert.Single(edge.Targets));
                if (targetNode.Name == "target-0")
                {
                    Assert.Equal("Fanout Example", Assert.IsType<StringValue>(output.Values["display_name"]).Value);
                    Assert.False(output.Values.ContainsKey("search_label"));
                }
                else
                {
                    Assert.Equal("target-1", targetNode.Name);
                    Assert.Equal("Fanout Example", Assert.IsType<StringValue>(output.Values["search_label"]).Value);
                    Assert.False(output.Values.ContainsKey("display_name"));
                }
            }

            var environment = new FanoutEnvironmentProvider(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["PROOFSHIFT_SOURCE_CONNECTION"] = "unused-synthetic-source",
                ["PROOFSHIFT_TARGET_CONNECTION"] = "unused-synthetic-target"
            });
            var contextFactory = new RuntimeConnectorContextFactory(environment);
            var checkpointStore = new FileSystemSnapshotStore(Path.Combine(root, "checkpoint-store"));
            var sourceConnector = new FanoutCheckpointConnector(sourceRecord);
            var capture = await new SnapshotCaptureService(new ConnectorRegistry([sourceConnector]), checkpointStore,
                contextFactory).CaptureAsync(loaded.Configuration, compiledGraph, TestContext.Current.CancellationToken);
            Assert.Equal(CheckpointStatus.Complete, capture.Status);
            Assert.Equal(1, capture.CapturedArtifacts);
            var checkpointEndpoint = Assert.Single(capture.Checkpoint!.Endpoints);
            Assert.Equal(sourceNode.Name, checkpointEndpoint.SourceNodeKey);
            Assert.Equal(1, checkpointEndpoint.ArtifactCount);
            Assert.Equal(1, sourceConnector.CheckpointReadCount);

            await using var loadedCheckpoint = await checkpointStore.OpenCompleteAsync(
                capture.Id.Value.ToString("N"), TestContext.Current.CancellationToken);
            var replayedSources = new List<RecordEnvelope>();
            await foreach (var replayed in loadedCheckpoint.ReadAsync(sourceNode.Name, sourceNode.Selector,
                TestContext.Current.CancellationToken))
                replayedSources.Add(replayed);
            var replayedSource = Assert.Single(replayedSources);
            Assert.Equal(sourceRecord.Artifact, replayedSource.Artifact);
            Assert.Equal(sourceRecord.Artifact.Id, replayedSource.Artifact.Id);
            Assert.Equal(sourceRecord.Artifact.Identity, replayedSource.Artifact.Identity);

            var shadowTargetConnector = new FanoutShadowTargetConnector();
            var projectionService = new ShadowProjectionService(new ConnectorRegistry([sourceConnector]),
                new ShadowTargetConnectorRegistry([shadowTargetConnector]), contextFactory,
                new CheckpointSourceArtifactStreamProvider(loadedCheckpoint));
            var projection = await projectionService.ProjectAsync(loaded.Configuration, compiledGraph, project,
                TestContext.Current.CancellationToken);
            Assert.Equal(ProjectionStatus.Succeeded, projection.Status);
            Assert.Equal(2, projection.SourceArtifactCount);
            Assert.Equal(2, projection.TargetArtifactCount);
            Assert.Equal(0, sourceConnector.LiveReadCount);
            Assert.Equal(capture.Id.Value.ToString("N"), projection.CheckpointId);
            Assert.Equal(capture.Checkpoint.SourceFingerprint, projection.CheckpointSourceFingerprint);
            Assert.Equal(capture.Checkpoint.ManifestHash, projection.CheckpointManifestHash);

            var projectedTarget0 = Assert.Single(shadowTargetConnector.ReadRecords(projection.Id, "target-0"));
            var projectedTarget1 = Assert.Single(shadowTargetConnector.ReadRecords(projection.Id, "target-1"));
            Assert.NotEqual(projectedTarget0.Artifact.Id, projectedTarget1.Artifact.Id);
            Assert.Equal("2:id=9:integer:7", projectedTarget0.Artifact.Identity);
            Assert.Equal("12:search_label=21:string:Fanout Example", projectedTarget1.Artifact.Identity);
            Assert.Equal(StableArtifactIdentity.CreateArtifactId(projectedTarget0.Artifact.SystemId.Value,
                projectedTarget0.Artifact.EndpointId.Value, projectedTarget0.Artifact.ArtifactType,
                projectedTarget0.Artifact.Identity), projectedTarget0.Artifact.Id.Value);
            Assert.Equal(StableArtifactIdentity.CreateArtifactId(projectedTarget1.Artifact.SystemId.Value,
                projectedTarget1.Artifact.EndpointId.Value, projectedTarget1.Artifact.ArtifactType,
                projectedTarget1.Artifact.Identity), projectedTarget1.Artifact.Id.Value);
            Assert.Equal("Fanout Example", Assert.IsType<StringValue>(projectedTarget0.Values["display_name"]).Value);
            Assert.False(projectedTarget0.Values.ContainsKey("search_label"));
            Assert.Equal("Fanout Example", Assert.IsType<StringValue>(projectedTarget1.Values["search_label"]).Value);
            Assert.Equal(new IntegerValue(7), projectedTarget1.Values["id"]);
            Assert.False(projectedTarget1.Values.ContainsKey("display_name"));
            Assert.Equal(sourceRecord.Artifact.Id.Value, projectedTarget0.Provenance.Metadata["projectionSource"]);
            Assert.Equal(sourceRecord.Artifact.Id.Value, projectedTarget1.Provenance.Metadata["projectionSource"]);
            Assert.Equal(edgesByTarget["target-0"].Name, projectedTarget0.Provenance.Metadata["projectionEdge"]);
            Assert.Equal(edgesByTarget["target-1"].Name, projectedTarget1.Provenance.Metadata["projectionEdge"]);

            shadowTargetConnector.ReplaceValue(projection.Id, "target-1", "id", new IntegerValue(8));
            var projectionManifestHash = await ProjectionRunManifestStore.WriteAsync(project, projection,
                TestContext.Current.CancellationToken);
            var binding = new ProjectionVerificationBinding(projection.Id, projection.ConfigurationHash,
                projection.GraphHash, capture.Id, capture.Checkpoint.ManifestHash!, capture.Checkpoint.SourceFingerprint!,
                projection.Fingerprint!, projection.FingerprintVersion, projection.SourceArtifactCount,
                projection.TargetArtifactCount, "succeeded", projectionManifestHash, projection.JournalPath,
                projection.ConnectorVersions);
            var targetRuntimes = targetsByName.Values.Select(node => new VerificationTargetRuntime(node.Name,
                shadowTargetConnector, new TargetObservationContext(contextFactory.Create(loaded.Configuration, node),
                    projection.Id, SystemRole.ShadowTarget))).ToArray();
            var ruleSet = new VerificationRuleRegistry([new GenericVerificationRuleProvider()]).Resolve(
            [
                VerificationRule("source-accounting", "source-artifact-accounting"),
                VerificationRule("target-lineage", "target-lineage"),
                VerificationRule("target-presence", "target-presence"),
                VerificationRule("unexpected-targets", "unexpected-target"),
                AttributeRule("primary-name", "target-0", "display_name", sourceNode.SemanticType),
                AttributeRule("search-id", "target-1", "id", sourceNode.SemanticType),
                AttributeRule("search-label", "target-1", "search_label", sourceNode.SemanticType)
            ]);
            var verification = await new VerificationService(checkpointStore).VerifyAsync(loaded.Configuration,
                compiledGraph, binding, ruleSet, project, Path.Combine(root, "verification-work"), "fanout-test",
                targetRuntimes, TestContext.Current.CancellationToken);
            Assert.Equal(VerificationOutcome.Failed, verification.Run.Outcome);
            Assert.Equal(1, verification.Ledger.SourceCount);
            Assert.Equal(2, verification.Ledger.TargetCount);
            Assert.Equal(1, verification.Ledger.DispositionCount);
            Assert.Equal(2, verification.Ledger.LineageCount);

            var searchIdFinding = Assert.Single(verification.Findings, finding =>
                finding.RuleId.Value == "search-id" && finding.Code == "AttributeMismatch");
            Assert.Equal(EvidenceResult.Fail, searchIdFinding.Result);
            Assert.Contains(searchIdFinding.Inputs, input => input.ArtifactId == sourceRecord.Artifact.Id &&
                input.GraphNodeId == sourceNode.Id);
            Assert.Contains(searchIdFinding.Inputs, input => input.ArtifactId == projectedTarget1.Artifact.Id &&
                input.GraphNodeId == targetsByName["target-1"].Id);
            Assert.Equal(2, searchIdFinding.Inputs.Count(input => input.ArtifactId == projectedTarget1.Artifact.Id &&
                input.GraphNodeId == targetsByName["target-1"].Id));
            Assert.Contains(searchIdFinding.Inputs, input => input.MigrationEdgeId == edgesByTarget["target-1"].Id);
            Assert.Contains(searchIdFinding.Inputs, input => input.CheckpointId == capture.Id);
            Assert.Contains(searchIdFinding.Inputs, input => input.ProjectionRunId == projection.Id);
            Assert.DoesNotContain(searchIdFinding.Inputs, input => input.GraphNodeId == targetsByName["target-0"].Id);

            var evidenceRecord = Assert.Single(verification.EvidenceGraph.Records,
                record => record.RuleId == searchIdFinding.RuleId && record.Code == searchIdFinding.Code);
            Assert.Contains(evidenceRecord.Inputs, input => input.ArtifactId == sourceRecord.Artifact.Id &&
                input.GraphNodeId == sourceNode.Id);
            Assert.Equal(2, evidenceRecord.Inputs.Count(input => input.ArtifactId == projectedTarget1.Artifact.Id &&
                input.GraphNodeId == targetsByName["target-1"].Id));
            Assert.Contains(evidenceRecord.Inputs, input => input.MigrationEdgeId == edgesByTarget["target-1"].Id);
            Assert.Contains(evidenceRecord.Inputs, input => input.CheckpointId == capture.Id);
            Assert.Contains(evidenceRecord.Inputs, input => input.ProjectionRunId == projection.Id);
            Assert.DoesNotContain(evidenceRecord.Inputs, input => input.GraphNodeId == targetsByName["target-0"].Id);
            Assert.Equal(verification.Ledger.Fingerprint,
                verification.EvidenceGraph.VerificationContext!.VerificationLedgerFingerprint);
            var evidenceStore = new FileSystemEvidenceStore(Path.Combine(root, "evidence"));
            var evidenceReceipt = await evidenceStore.SaveAsync(verification.EvidenceGraph,
                TestContext.Current.CancellationToken);
            Assert.True(await evidenceStore.VerifyIntegrityAsync(verification.Run.Id,
                TestContext.Current.CancellationToken));
            Assert.Equal(verification.EvidenceGraph.Fingerprint, evidenceReceipt.Fingerprint);

            await using var ledger = await SqliteVerificationLedgerStore.OpenAsync(project, verification.Ledger,
                TestContext.Current.CancellationToken);
            var dispositions = new List<ArtifactDispositionRecord>();
            await foreach (var disposition in ledger.ReadDispositionsAsync(TestContext.Current.CancellationToken))
                dispositions.Add(disposition);
            var dispositionRecord = Assert.Single(dispositions);
            Assert.Equal(sourceRecord.Artifact.Id, dispositionRecord.Source.Id);
            Assert.Equal(sourceRecord.Artifact.SystemId, dispositionRecord.Source.SystemId);
            Assert.Equal(sourceRecord.Artifact.EndpointId, dispositionRecord.Source.EndpointId);
            Assert.Equal(sourceRecord.Artifact.ArtifactType, dispositionRecord.Source.ArtifactType);
            Assert.Equal($"opaque:{sourceRecord.Artifact.Id.Value}", dispositionRecord.Source.Identity);
            Assert.Equal(sourceNode.Id, dispositionRecord.SourceNodeId);
            Assert.Equal(ArtifactDisposition.Transformed, dispositionRecord.Disposition);
            Assert.Equal(2, dispositionRecord.Targets.Count);
            Assert.Equal(2, dispositionRecord.TargetNodeIds.Count);
            var targetDispositionPairs = dispositionRecord.Targets.Zip(dispositionRecord.TargetNodeIds)
                .ToDictionary(pair => pair.Second, pair => pair.First);
            var dispositionTarget0 = targetDispositionPairs[targetsByName["target-0"].Id];
            Assert.Equal(projectedTarget0.Artifact.Id, dispositionTarget0.Id);
            Assert.Equal(projectedTarget0.Artifact.SystemId, dispositionTarget0.SystemId);
            Assert.Equal(projectedTarget0.Artifact.EndpointId, dispositionTarget0.EndpointId);
            Assert.Equal(projectedTarget0.Artifact.ArtifactType, dispositionTarget0.ArtifactType);
            Assert.Equal($"opaque:{projectedTarget0.Artifact.Id.Value}", dispositionTarget0.Identity);
            var dispositionTarget1 = targetDispositionPairs[targetsByName["target-1"].Id];
            Assert.Equal(projectedTarget1.Artifact.Id, dispositionTarget1.Id);
            Assert.Equal(projectedTarget1.Artifact.SystemId, dispositionTarget1.SystemId);
            Assert.Equal(projectedTarget1.Artifact.EndpointId, dispositionTarget1.EndpointId);
            Assert.Equal(projectedTarget1.Artifact.ArtifactType, dispositionTarget1.ArtifactType);
            Assert.Equal($"opaque:{projectedTarget1.Artifact.Id.Value}", dispositionTarget1.Identity);

            var lineages = new List<LineageRecord>();
            await foreach (var lineage in ledger.ReadLineageAsync(TestContext.Current.CancellationToken))
                lineages.Add(lineage);
            Assert.Equal(2, lineages.Count);
            var lineageByTargetNode = lineages.ToDictionary(lineage => lineage.TargetNodeId!.Value);
            var target0Lineage = lineageByTargetNode[targetsByName["target-0"].Id];
            Assert.Equal(projectedTarget0.Artifact.Id, target0Lineage.Target.Id);
            Assert.Equal(sourceRecord.Artifact.Id, Assert.Single(target0Lineage.Sources).Id);
            Assert.Equal($"opaque:{projectedTarget0.Artifact.Id.Value}", target0Lineage.Target.Identity);
            Assert.Equal($"opaque:{sourceRecord.Artifact.Id.Value}", Assert.Single(target0Lineage.Sources).Identity);
            Assert.Equal(sourceNode.Id, Assert.Single(target0Lineage.SourceNodeIds));
            Assert.Equal(edgesByTarget["target-0"].Id, Assert.Single(target0Lineage.Path));
            Assert.Equal(LineageBasis.ExecutionObserved, target0Lineage.Basis);
            var target1Lineage = lineageByTargetNode[targetsByName["target-1"].Id];
            Assert.Equal(projectedTarget1.Artifact.Id, target1Lineage.Target.Id);
            Assert.Equal(sourceRecord.Artifact.Id, Assert.Single(target1Lineage.Sources).Id);
            Assert.Equal($"opaque:{projectedTarget1.Artifact.Id.Value}", target1Lineage.Target.Identity);
            Assert.Equal($"opaque:{sourceRecord.Artifact.Id.Value}", Assert.Single(target1Lineage.Sources).Identity);
            Assert.Equal(sourceNode.Id, Assert.Single(target1Lineage.SourceNodeIds));
            Assert.Equal(edgesByTarget["target-1"].Id, Assert.Single(target1Lineage.Path));
            Assert.Equal(LineageBasis.ExecutionObserved, target1Lineage.Basis);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private static VerificationRuleDefinition VerificationRule(string id, string type) =>
        new(new RuleId(id), type, "1", EvidenceSeverity.Error);

    private static VerificationRuleDefinition AttributeRule(string id, string targetNode, string field, string semanticType) =>
        new(new RuleId(id), "attribute-comparison", "1", EvidenceSeverity.Error,
        [
            new KeyValuePair<string, string>("targetNode", targetNode),
            new KeyValuePair<string, string>("attribute", field),
            new KeyValuePair<string, string>("semanticType", semanticType)
        ]);

    private sealed class FanoutEnvironmentProvider(IReadOnlyDictionary<string, string> values) : IEnvironmentVariableProvider
    {
        public string? GetValue(string name) => values.GetValueOrDefault(name);
    }

    private sealed class FanoutCheckpointConnector(RecordEnvelope sourceRecord) : ICheckpointSourceConnector
    {
        public ConnectorId Id { get; } = new("postgres");
        public string Version => "fanout-test-v1";
        public SourceConsistencyGuarantee CheckpointConsistency => SourceConsistencyGuarantee.Observed;
        public int CheckpointReadCount { get; private set; }
        public int LiveReadCount { get; private set; }

        public Task<SourceInspection> InspectAsync(ConnectorContext context, ArtifactSelector selector,
            CancellationToken cancellationToken) =>
            Task.FromResult(new SourceInspection(SourceInspectionStatus.Valid, identityFields: selector.IdentityFields));

        public IAsyncEnumerable<RecordEnvelope> ReadAsync(ConnectorContext context, ArtifactSelector selector,
            ReadOptions options, CancellationToken cancellationToken)
        {
            LiveReadCount++;
            return ReadRecordAsync(cancellationToken);
        }

        public IAsyncEnumerable<RecordEnvelope> ReadForCheckpointAsync(ConnectorContext context,
            ArtifactSelector selector, ReadOptions options, CancellationToken cancellationToken)
        {
            CheckpointReadCount++;
            return ReadRecordAsync(cancellationToken);
        }

        private async IAsyncEnumerable<RecordEnvelope> ReadRecordAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return sourceRecord;
        }
    }

    private sealed class FanoutShadowTargetConnector : IShadowTargetConnector
    {
        private readonly Dictionary<(RunId RunId, string NodeKey), List<RecordEnvelope>> _records = [];

        public ConnectorId Id { get; } = new("postgres");
        public string Version => "fanout-test-v1";

        public Task PrepareAsync(ShadowTargetContext context, ArtifactSelector selector,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _records.Add((context.RunId, context.ConnectorContext.NodeKey), []);
            return Task.CompletedTask;
        }

        public Task WriteAsync(ShadowWriteRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _records[(request.Context.RunId, request.NodeKey)].Add(request.Record);
            return Task.CompletedTask;
        }

        public Task CompleteAsync(ShadowTargetContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<RecordEnvelope> ReadAsync(ReadRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            var key = (request.Context.RunId, request.Context.ConnectorContext.NodeKey);
            foreach (var record in _records[key].OrderBy(record => record.Artifact.Identity, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return record;
            }
        }

        public System.Collections.ObjectModel.ReadOnlyCollection<RecordEnvelope> ReadRecords(RunId runId, string nodeKey) =>
            _records[(runId, nodeKey)].AsReadOnly();

        public void ReplaceValue(RunId runId, string nodeKey, string field, ValueNode value)
        {
            var records = _records[(runId, nodeKey)];
            Assert.Single(records);
            var record = records[0];
            records[0] = new RecordEnvelope(record.Artifact, record.SemanticType,
                record.Values.Select(pair => new KeyValuePair<string, ValueNode>(pair.Key,
                    pair.Key == field ? value : pair.Value)), record.Provenance, record.Relationships, record.Temporal);
        }
    }

    [Theory]
    [InlineData("oracle", "table")]
    [InlineData("db2", "table")]
    [InlineData("fixed-width", "fixed-width")]
    [InlineData("json", "json")]
    [InlineData("ndjson", "ndjson")]
    [InlineData("xml", "xml")]
    public async Task ScaffoldPreservesEnterpriseAndStructuredConnectorSelectors(string connector, string expectedKind)
    {
        var root = Path.Combine(Path.GetTempPath(), $"proofshift-scaffold-{Guid.NewGuid():N}");
        try
        {
            var sourceContext = new ConnectorContext("logical-source", "records", new ConnectorId(connector), "discovery",
                "Physical.Uninterpreted", new RuntimeConfiguration([]));
            var selectorProperties = connector switch
            {
                "oracle" => new Dictionary<string, string> { ["schema"] = "APP" },
                "db2" => new Dictionary<string, string> { ["schema"] = "APP" },
                "fixed-width" => new Dictionary<string, string>
                {
                    ["path"] = "records.txt", ["width"] = "4", ["fields.id.start"] = "1", ["fields.id.length"] = "4"
                },
                "json" or "ndjson" => new Dictionary<string, string> { ["path"] = "records.json" },
                _ => new Dictionary<string, string> { ["path"] = "records.xml", ["recordElement"] = "Member" }
            };
            var source = PhysicalDiscovery.Create(sourceContext, connector, "0.10.0",
                [new("APP", "RECORDS", "table", [new("id", "integer", false, 1)], [new("PK_RECORDS", true, ["id"])], [], SelectorProperties: selectorProperties)]);
            var targetContext = new ConnectorContext("logical-target", "records", new ConnectorId("postgres"), "discovery",
                "Physical.Uninterpreted", new RuntimeConfiguration([]));
            var target = PhysicalDiscovery.Create(targetContext, "postgres", "0.1.0",
                [new("public", "RECORDS", "table", [new("id", "integer", false, 1)], [new("records_pkey", true, ["id"])], [])]);
            var sourcePath = Path.Combine(root, "source");
            var targetPath = Path.Combine(root, "target");
            await PhysicalDiscoveryStore.SaveAsync(sourcePath, source, TestContext.Current.CancellationToken);
            await PhysicalDiscoveryStore.SaveAsync(targetPath, target, TestContext.Current.CancellationToken);
            var generated = Path.Combine(root, "project");
            var scaffold = await RunAsync("scaffold", "--source-discovery", sourcePath, "--target-discovery", targetPath, "--output", generated);
            Assert.True(scaffold.ExitCode == 0, scaffold.Output + scaffold.Error);
            var graph = await File.ReadAllTextAsync(Path.Combine(generated, "migration", "graph.yaml"), TestContext.Current.CancellationToken);
            var yaml = new YamlStream();
            yaml.Load(new StringReader(graph));
            var nodes = (YamlMappingNode)((YamlMappingNode)yaml.Documents[0].RootNode)["nodes"];
            var sourceNode = (YamlMappingNode)nodes.Children.Values.First();
            var selector = (YamlMappingNode)((YamlMappingNode)sourceNode)["selector"];
            Assert.Equal(expectedKind, ((YamlScalarNode)selector["kind"]).Value);
            var properties = (YamlMappingNode)selector["properties"];
            if (connector is "oracle" or "db2") Assert.Equal("APP.RECORDS", ((YamlScalarNode)properties["name"]).Value);
            else Assert.Contains(properties.Children, pair => pair.Key.ToString() == "path");
            var system = await File.ReadAllTextAsync(Path.Combine(generated, "systems", "source.yaml"), TestContext.Current.CancellationToken);
            var systemYaml = new YamlStream();
            systemYaml.Load(new StringReader(system));
            var systemNode = (YamlMappingNode)systemYaml.Documents[0].RootNode;
            var storage = (YamlMappingNode)systemNode["storage"];
            var recordsEndpoint = (YamlMappingNode)storage["records"];
            Assert.Equal(connector, ((YamlScalarNode)recordsEndpoint["connector"]).Value);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private static async Task<Guid> CreatePackageFixtureRunAsync(string root, string? disposition,
        EvidenceResult evidenceResult, bool detailedFindings = false, string evidenceValue = "fixture-result",
        string exceptionReason = "sensitive note", IReadOnlyList<string>? discoveryFingerprints = null,
        bool replayedCheckpoint = true)
    {
        var workspace = await EvaluationWorkspaceManifestStore.ReadAsync(root)
            ?? throw new InvalidDataException("Test workspace manifest is missing.");
        var planResult = await WorkspaceEvaluationService.PlanAsync(root, TestContext.Current.CancellationToken);
        var plan = planResult.Plan ?? throw new InvalidDataException("Test workspace has no effective plan.");
        var runId = Guid.NewGuid();
        var verificationRunId = new ProofShift.Domain.RunId(Guid.NewGuid());
        var checkpointId = Guid.NewGuid();
        var runDirectory = Path.Combine(root, workspace.Paths.Runs, runId.ToString("D"));
        var projectionId = new ProofShift.Domain.RunId(Guid.NewGuid());
        var journalPath = $".proofshift/projections/{projectionId.Value:N}/journal.ndjson";
        var absoluteJournalPath = Path.Combine(runDirectory, journalPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(absoluteJournalPath)!);
        await File.WriteAllTextAsync(absoluteJournalPath,
            "{\"sources\":[\"source-artifact-1\"],\"targets\":[\"target-artifact-a\",\"target-artifact-b\"],\"edge\":\"fan-out\"}\n",
            TestContext.Current.CancellationToken);
        var projectionRun = new ProofShift.Projection.ProjectionRun(projectionId,
            ProofShift.Projection.ProjectionStatus.Succeeded, "fixture-project", new string('b', 64),
            plan.GraphHash, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 1, 2, 0, null,
            new string('f', 64), [], [], journalPath, checkpointId.ToString("N"), new string('d', 64),
            new string('c', 64));
        var projectionManifestHash = await ProofShift.Projection.ProjectionRunManifestStore.WriteAsync(
            runDirectory, projectionRun, TestContext.Current.CancellationToken);

        var recoveryId = Guid.NewGuid();
        var recoveryDirectory = Path.Combine(runDirectory, "recovery", recoveryId.ToString("N"));
        Directory.CreateDirectory(recoveryDirectory);
        var recoveryFiles = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in new[]
                 {
                     "assessment.json", "plan.json", "rehearsal.json",
                     "recovery-evidence.json", "qualification.json"
                 })
        {
            var bytes = Encoding.UTF8.GetBytes($"{{\"artifact\":\"{name}\"}}");
            await File.WriteAllBytesAsync(Path.Combine(recoveryDirectory, name), bytes,
                TestContext.Current.CancellationToken);
            recoveryFiles.Add(name, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        }
        var recoveryDraft = new TestRecoveryManifest("proofshift-recovery-store-v1",
            recoveryId.ToString("D"), recoveryFiles, null);
        var recoveryOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };
        var recoveryHash = Convert.ToHexString(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(recoveryDraft, recoveryOptions))).ToLowerInvariant();
        var recoveryManifest = JsonSerializer.SerializeToUtf8Bytes(
            recoveryDraft with { IntegrityHash = recoveryHash }, recoveryOptions);
        await File.WriteAllBytesAsync(Path.Combine(recoveryDirectory, "manifest.json"), recoveryManifest,
            TestContext.Current.CancellationToken);

        var evidenceRecords = new List<ProofShift.Domain.EvidenceRecord>();
        if (detailedFindings && evidenceResult == EvidenceResult.Fail)
        {
            var budgetAttributesId = new ProofShift.Domain.EvidenceId(Guid.NewGuid());
            var budgetAttributesNode = new ProofShift.Domain.MigrationNodeId(
                Guid.Parse("10000000-0000-0000-0000-000000000001"));
            var budgetAttributesArtifact = new ProofShift.Domain.ArtifactId("target-budget-attributes");
            evidenceRecords.Add(new ProofShift.Domain.EvidenceRecord(
                budgetAttributesId, verificationRunId, ProofShift.Domain.EvidenceType.Comparison,
                new ProofShift.Domain.RuleId("budget-attributes"), "1", EvidenceResult.Fail,
                [new ProofShift.Domain.EvidenceReference(artifactId: budgetAttributesArtifact,
                    graphNodeId: budgetAttributesNode)],
                "Sensitive finding text and value must not appear in the summary.",
                DateTimeOffset.UnixEpoch, actual: new ProofShift.Domain.EvidenceValue(
                    new ProofShift.Domain.StringValue(evidenceValue)), code: "BudgetAttributesMismatch"));
            foreach (var (rule, code, artifact) in new[]
                     {
                         ("budget-total", "BudgetTotalMismatch", "target-budget-total"),
                         ("proofshift.verification.materialized-state", "MaterializedTargetChanged", "source-artifact-1")
                     })
            {
                evidenceRecords.Add(new ProofShift.Domain.EvidenceRecord(
                    new ProofShift.Domain.EvidenceId(Guid.NewGuid()), verificationRunId,
                    ProofShift.Domain.EvidenceType.Comparison, new ProofShift.Domain.RuleId(rule), "1",
                    EvidenceResult.Fail,
                    [new ProofShift.Domain.EvidenceReference(artifactId: new ProofShift.Domain.ArtifactId(artifact),
                        graphNodeId: new ProofShift.Domain.MigrationNodeId(
                            Guid.Parse(rule == "budget-total"
                                ? "10000000-0000-0000-0000-000000000002"
                                : "10000000-0000-0000-0000-000000000003")))],
                    "Package fixture defect.", DateTimeOffset.UnixEpoch,
                    actual: new ProofShift.Domain.EvidenceValue(new ProofShift.Domain.StringValue("mismatch")),
                    code: code));
            }
            evidenceRecords.Add(new ProofShift.Domain.EvidenceRecord(
                new ProofShift.Domain.EvidenceId(Guid.NewGuid()), verificationRunId,
                ProofShift.Domain.EvidenceType.Decision,
                new ProofShift.Domain.RuleId("proofshift.verification.expected-exception-annotation"), "1",
                EvidenceResult.Warning,
                [
                    new ProofShift.Domain.EvidenceReference(evidenceId: budgetAttributesId),
                    new ProofShift.Domain.EvidenceReference(artifactId: budgetAttributesArtifact,
                        graphNodeId: budgetAttributesNode),
                    new ProofShift.Domain.EvidenceReference(ruleId: new ProofShift.Domain.RuleId("budget-attributes"))
                ],
                $"Expected exception 'budget-attributes-expected' annotates the finding; original failure remains active. Owner: test. Reason: {exceptionReason}.",
                DateTimeOffset.UnixEpoch, code: "ExpectedExceptionAnnotated",
                severity: ProofShift.Domain.EvidenceSeverity.Info));
        }
        else
        {
            evidenceRecords.Add(new ProofShift.Domain.EvidenceRecord(
                new ProofShift.Domain.EvidenceId(Guid.NewGuid()),
                verificationRunId,
                ProofShift.Domain.EvidenceType.Comparison,
                new ProofShift.Domain.RuleId(evidenceResult == EvidenceResult.Fail
                    ? "proofshift.verification.materialized-state" : "fixture.rule"),
                "1",
                evidenceResult,
                [new ProofShift.Domain.EvidenceReference(
                    artifactId: new ProofShift.Domain.ArtifactId("source-artifact-1"),
                    graphNodeId: new ProofShift.Domain.MigrationNodeId(
                        Guid.Parse("10000000-0000-0000-0000-000000000003")))],
                "Package fixture finding preserves source-to-target evidence provenance.",
                DateTimeOffset.UnixEpoch,
                actual: new ProofShift.Domain.EvidenceValue(new ProofShift.Domain.StringValue(evidenceValue)),
                code: evidenceResult == EvidenceResult.Fail ? "MaterializedTargetChanged" : null));
        }
        var evidenceGraph = new EvidenceGraph(verificationRunId, evidenceRecords);
        var evidenceStore = new FileSystemEvidenceStore(Path.Combine(runDirectory, "evidence"));
        await evidenceStore.SaveAsync(evidenceGraph, TestContext.Current.CancellationToken);
        var evidenceManifest = await evidenceStore.ReadManifestAsync(verificationRunId,
            TestContext.Current.CancellationToken);

        var runManifest = new WorkspaceRunManifest
        {
            WorkspaceId = workspace.WorkspaceId,
            RunId = runId,
            ProductVersion = "0.1.0-preview.1",
            SourceRevision = "test-revision",
            SourceTree = "test-tree",
            StartedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch,
            State = "Completed",
            PlanFingerprint = plan.Fingerprint,
            ConfigurationHash = plan.ConfigurationFingerprint,
            GraphHash = plan.GraphHash,
            DiscoveryFingerprints = discoveryFingerprints ?? [],
            CheckpointId = checkpointId.ToString("D"),
            CheckpointManifestHash = new string('c', 64),
            CheckpointSourceFingerprint = new string('d', 64),
            CheckpointStoreScope = "workspace",
            ReplayedCheckpoint = replayedCheckpoint,
            ProjectionRunId = projectionId.Value.ToString("D"),
            ProjectionManifestReference = $"runs/{runId:D}/.proofshift/projections/{projectionId.Value:N}/run.json",
            ProjectionManifestHash = projectionManifestHash,
            ProjectionFingerprint = new string('f', 64),
            VerificationRunId = verificationRunId.Value.ToString("D"),
            VerificationOutcome = evidenceResult == EvidenceResult.Pass ? "Passed" : "Failed",
            VerificationEvidenceFingerprint = evidenceGraph.Fingerprint,
            VerificationLedgerFingerprint = new string('e', 64),
            VerificationLedgerReference = $"runs/{runId:D}/verification-ledger/ledger.sqlite",
            ReconciliationRuleIds = detailedFindings ? ["budget-total"] : [],
            ReconciliationFailedRuleIds = detailedFindings && evidenceResult == EvidenceResult.Fail
                ? ["budget-total"] : [],
            EvidenceReference = $"runs/{runId:D}/evidence/{verificationRunId.Value:N}/manifest.json",
            EvidenceManifestIntegrityHash = evidenceManifest.IntegrityHash,
            RecoveryRunId = recoveryId.ToString("D"),
            RecoveryQualification = "Qualified",
            RecoveryArtifactReference = $"runs/{runId:D}/recovery/{recoveryId:N}/manifest.json",
            RecoveryArtifactIntegrityHash = recoveryHash,
            Disposition = disposition
        };
        await WorkspaceRunManifestStore.WriteAsync(runDirectory, runManifest, createNew: true,
            TestContext.Current.CancellationToken);
        return runId;
    }

    private sealed record TestRecoveryManifest(string Format, string DryRunId,
        SortedDictionary<string, string> Files, string? IntegrityHash);

    private static async Task<WorkspaceEvidencePackageValidation> ValidateChangedPackageAsync(string root,
        string name, IReadOnlyDictionary<string, byte[]> entries, string? symbolicLinkEntry = null)
    {
        var path = Path.Combine(root, "evidence", name);
        await WritePackageEntriesAsync(path, entries, symbolicLinkEntry);
        return await WorkspaceEvidencePackageService.ValidateAsync(path, TestContext.Current.CancellationToken);
    }

    private static async Task<Dictionary<string, byte[]>> ReadPackageEntriesAsync(string path)
    {
        var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        using var archive = ZipFile.OpenRead(path);
        foreach (var entry in archive.Entries)
        {
            await using var stream = entry.Open();
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, TestContext.Current.CancellationToken);
            entries.Add(entry.FullName, buffer.ToArray());
        }
        return entries;
    }

    private static async Task WritePackageEntriesAsync(string path, IReadOnlyDictionary<string, byte[]> entries,
        string? symbolicLinkEntry = null)
    {
        await using var file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false);
        foreach (var (name, bytes) in entries.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
            entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
            if (name == symbolicLinkEntry) entry.ExternalAttributes = unchecked((int)0xA0000000);
            await using var stream = entry.Open();
            await stream.WriteAsync(bytes, TestContext.Current.CancellationToken);
        }
    }

    private static string ReadEntryText(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    private static async Task<byte[]> ReadEntryAsync(ZipArchiveEntry entry)
    {
        await using var stream = entry.Open();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, TestContext.Current.CancellationToken);
        return buffer.ToArray();
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunAsync(params string[] args)
        => await RunProcessAsync(null, args);

    private static async Task<(int ExitCode, string Output, string Error)> RunWithEnvironmentAsync(
        string variable,
        string? value,
        params string[] args) =>
        await RunProcessAsync((variable, value), args);

    private static async Task<(int ExitCode, string Output, string Error)> RunProcessAsync(
        (string Variable, string? Value)? environmentOverride,
        string[] args)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (environmentOverride is { } environment)
        {
            if (environment.Value is null)
                start.Environment.Remove(environment.Variable);
            else
                start.Environment[environment.Variable] = environment.Value;
        }
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "ProofShift.Cli.dll"));
        foreach (var argument in args) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("CLI process did not start.");
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return (process.ExitCode, await output, await error);
    }

    private sealed class MissingEnvironmentVariableProvider : IEnvironmentVariableProvider
    {
        public string? GetValue(string name) => null;
    }
}