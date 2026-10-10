using System.Diagnostics;
using System.Text.Json;
using ProofShift.Connectors.Abstractions;
using ProofShift.Domain;
using ProofShift.Engine;
using YamlDotNet.RepresentationModel;
using Xunit;

namespace ProofShift.EndToEnd.Tests;

public sealed class AuthoringCliTests
{
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
            var strict = await RunAsync("validate", Path.Combine(generated, "proofshift.yaml"), "--strict", "--json");
            Assert.Equal(1, strict.ExitCode);
            Assert.Contains("PSAUTHOR001", strict.Output, StringComparison.Ordinal);
            var csv = Path.Combine(root, "reviewed.csv");
            await File.WriteAllTextAsync(csv, "Source Entity,Source Field,Target Entity,Target Field,Transformation,Required,Notes,Review Status\npublic.records,id,public.records,id,copy,true,Reviewed,confirmed\n", TestContext.Current.CancellationToken);
            var reviewed = Path.Combine(root, "confirmed");
            var import = await RunAsync("mapping", "import", "--csv", csv, "--source-discovery", sourcePath, "--target-discovery", targetPath, "--output", reviewed);
            Assert.True(import.ExitCode == 0, import.Error);
            var valid = await RunAsync("validate", Path.Combine(reviewed, "proofshift.yaml"), "--strict", "--json");
            Assert.True(valid.ExitCode == 0, valid.Output + valid.Error);
            Assert.Contains("\"valid\": true", valid.Output, StringComparison.Ordinal);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
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