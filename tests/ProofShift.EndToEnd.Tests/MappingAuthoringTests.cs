using System.Text.Json;
using ProofShift.Connectors.Abstractions;
using ProofShift.Domain;
using ProofShift.Engine;
using ProofShift.Configuration;
using ProofShift.Graph;
using ProofShift.Recovery;
using Xunit;

namespace ProofShift.EndToEnd.Tests;

public sealed class MappingAuthoringTests
{
    [Fact]
    public void NamedCodeMapsResolveExactlyAndGenericRecoveryRejectsNonInjectiveMaps()
    {
        ConfigurationScalarNode Text(string value) => new(ConfigurationScalarKind.Text, value);
        ConfigurationMappingNode Map(params (string Name, ConfigurationDocumentNode Value)[] properties) =>
            new(properties.Select(property => new KeyValuePair<string, ConfigurationDocumentNode>(property.Name, property.Value)));
        var root = Map(("codeMaps", Map(("status", Map(("values", Map(("A", Text("ACTIVE")), ("E", Text("ACTIVE")))), ("unmapped", Text("fail")))))),
            ("pipeline", new ConfigurationSequenceNode([Map(("type", Text("code-map")), ("map", Text("status")))])));
        var resolved = ReusableCodeMaps.Resolve(root);
        var step = Assert.IsType<ConfigurationMappingNode>(Assert.IsType<ConfigurationSequenceNode>(resolved.Values["pipeline"]).Values[0]);
        Assert.False(step.Values.ContainsKey("map"));
        var values = Assert.IsType<ConfigurationMappingNode>(step.Values["values"]);
        var parameters = values.Values.Select(pair => new KeyValuePair<string, string>(pair.Key, Assert.IsType<ConfigurationScalarNode>(pair.Value).Value!))
            .Append(new("unknown", "fail"));
        var edge = new MigrationEdge(new MigrationEdgeId(Guid.NewGuid()), "status-map", [new MigrationNodeId(Guid.NewGuid())],
            [new MigrationNodeId(Guid.NewGuid())], new MigrationOperation(MigrationOperationType.Transform,
                fields: [new TransformationFieldDefinition("status", "STATUS", [new TransformationStep(TransformationStepType.CodeMap, "1", parameters)])]),
            "1", new RecoveryDefinition(RecoveryMode.Reverse));
        Assert.False(TransformationLossAnalyzer.Analyze(edge).IsReversible);
        var missing = Map(("pipeline", new ConfigurationSequenceNode([Map(("type", Text("code-map")), ("map", Text("not-defined")))])));
        Assert.Throws<InvalidDataException>(() => ReusableCodeMaps.Resolve(missing));
        var noPolicy = Map(("codeMaps", Map(("status", Map(("values", Map(("A", Text("ACTIVE")))))))));
        Assert.Throws<InvalidDataException>(() => ReusableCodeMaps.Resolve(noPolicy));
    }

    [Fact]
    public void ScaffoldIsDeterministicAndSuggestsStructureWithoutApprovingAmbiguousOrUnmappedFields()
    {
        var source = Artifact("source", [new("dbo", "MEMBER", "table",
            [new("MEMBER_ID", "int", false, 1), new("Unmapped_Status", "text", true, 2)], [new("pk", true, ["MEMBER_ID"])], [])]);
        var target = Artifact("target", [new("public", "member", "table",
            [new("memberId", "integer", false, 1)], [new("pk", true, ["memberId"])], [])]);
        var first = MappingScaffold.Create(source, target);
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(MappingScaffold.Create(source, target)));
        var matched = first.Mappings.Single(mapping => mapping.SourceField == "MEMBER_ID");
        Assert.Equal("suggested", matched.ReviewStatus);
        Assert.Contains("normalized-name-match", matched.Reasons);
        Assert.Contains("source-primary-key", matched.Reasons);
        Assert.Equal("unmapped", first.Mappings.Single(mapping => mapping.SourceField == "Unmapped_Status").ReviewStatus);
        Assert.True(first.Mappings.All(mapping => mapping.Required));
        var ambiguousTarget = Artifact("target", target.Objects.Concat([target.Objects[0] with { Schema = "other" }]).ToArray());
        Assert.Equal("ambiguous", MappingScaffold.Create(source, ambiguousTarget).Mappings[0].ReviewStatus);
    }

    [Fact]
    public async Task CsvImportValidatesActualDiscoveryAndReportsDuplicateUnknownAndInvalidValuesByRow()
    {
        var source = Artifact("source", [new("dbo", "records", "table", [new("id", "int", false, 1)], [], [])]);
        var target = Artifact("target", [new("public", "records", "table", [new("id", "integer", false, 1)], [], [])]);
        const string header = "Source Entity,Source Field,Target Entity,Target Field,Transformation,Required,Notes,Review Status\n";
        using var validReader = new StringReader(header + "dbo.records,id,public.records,id,copy,true,Reviewed,confirmed\n");
        var valid = await new CsvMappingImporter().ImportAsync(validReader, source, target, TestContext.Current.CancellationToken);
        Assert.Empty(valid.Issues);
        Assert.Equal("confirmed", Assert.Single(valid.Mappings).ReviewStatus);
        using var invalidReader = new StringReader(header +
            "dbo.records,id,public.records,id,copy,true,,suggested\n" +
            "dbo.records,id,public.records,unknown,script,maybe,,approved\n");
        var invalid = await new CsvMappingImporter().ImportAsync(invalidReader, source, target, TestContext.Current.CancellationToken);
        Assert.Contains(invalid.Issues, issue => issue.Code == "PSIMPORT002" && issue.Row == 3);
        Assert.Contains(invalid.Issues, issue => issue.Code == "PSIMPORT004" && issue.Row == 3);
        Assert.Contains(invalid.Issues, issue => issue.Code == "PSIMPORT005");
        Assert.Contains(invalid.Issues, issue => issue.Code == "PSIMPORT006");
        Assert.Contains(invalid.Issues, issue => issue.Code == "PSIMPORT007");
    }

    private static PhysicalDiscoveryArtifact Artifact(string endpoint, IReadOnlyList<PhysicalObject> objects) =>
        PhysicalDiscovery.Create(new ConnectorContext("logical", endpoint, new ConnectorId("postgres"), "discovery", "Physical.Uninterpreted", new RuntimeConfiguration([])),
            "postgres", "0.1.0", objects, DateTimeOffset.UnixEpoch);
}