using System.Diagnostics;
using System.Text.Json;
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

    private static async Task<(int ExitCode, string Output, string Error)> RunAsync(params string[] args)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "ProofShift.Cli.dll"));
        foreach (var argument in args) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("CLI process did not start.");
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return (process.ExitCode, await output, await error);
    }
}