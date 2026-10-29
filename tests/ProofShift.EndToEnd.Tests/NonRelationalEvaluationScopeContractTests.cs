using ProofShift.Connectors.Abstractions;
using ProofShift.Connectors.AzureBlob;
using ProofShift.Connectors.Csv;
using ProofShift.Connectors.Files;
using ProofShift.Connectors.Parquet;
using ProofShift.Connectors.RemoteObjects;
using ProofShift.Connectors.S3;
using ProofShift.Connectors.Sftp;
using ProofShift.Connectors.StructuredFiles;
using ProofShift.Domain;
using Xunit;

namespace ProofShift.EndToEnd.Tests;

public sealed class NonRelationalEvaluationScopeContractTests
{
    [Fact]
    public async Task NonRelationalSourcesRejectEveryPredicateBeforeReading()
    {
        var root = Path.Combine(Path.GetTempPath(), $"proofshift-evaluation-scope-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "records.csv"), "id,amount,optional,name\n1,12,,alpha\n",
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(root, "records.json"), "[{\"id\":1,\"amount\":12,\"optional\":null,\"name\":\"alpha\"}]",
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(root, "records.ndjson"), "{\"id\":1,\"amount\":12,\"optional\":null,\"name\":\"alpha\"}\n",
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(root, "records.xml"), "<Root><Member id=\"1\"><amount>12</amount><optional/><name>alpha</name></Member></Root>",
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(root, "records.txt"), "1", TestContext.Current.CancellationToken);

            var connectors = new (ISourceConnector Connector, string Kind, KeyValuePair<string, string>[] Properties, string[] Identity)[]
            {
                (new CsvSourceConnector(), "csv", [new("path", "records.csv")], ["id"]),
                (new FilesystemSourceConnector(), "file-pattern", [new("pattern", "**/*")], ["relativePath"]),
                (new StructuredFileConnector("json"), "json", [new("path", "records.json")], ["id"]),
                (new StructuredFileConnector("ndjson"), "ndjson", [new("path", "records.ndjson")], ["id"]),
                (new StructuredFileConnector("xml"), "xml",
                    [new("path", "records.xml"), new("recordElement", "Member"), new("fields.id.path", "@id")], ["id"]),
                (new StructuredFileConnector("fixed-width"), "fixed-width",
                    [new("path", "records.txt"), new("width", "1"), new("fields.id.start", "1"),
                        new("fields.id.length", "1"), new("fields.id.type", "integer")], ["id"]),
                (new S3SourceConnector(), "object-pattern", [new("pattern", "**/*")], ["path"]),
                (new AzureBlobSourceConnector(), "object-pattern", [new("pattern", "**/*")], ["path"]),
                (new SftpSourceConnector(), "object-pattern", [new("pattern", "**/*")], ["path"]),
                (new ParquetSourceConnector(
                    [new LocalFileStoreFactory(), new S3StoreFactory(), new AzureBlobStoreFactory(), new SftpStoreFactory()]),
                    "parquet", [new("pattern", "**/*.parquet"), new("transport", "filesystem")], ["id"])
            };
            var predicates = new[]
            {
                new EvaluationPredicate("id", EvaluationPredicateOperator.Equals, ["1"]),
                new EvaluationPredicate("id", EvaluationPredicateOperator.Equals, ["999"]),
                new EvaluationPredicate("id", EvaluationPredicateOperator.NotEquals, ["999"]),
                new EvaluationPredicate("id", EvaluationPredicateOperator.In, ["1"]),
                new EvaluationPredicate("missing_field", EvaluationPredicateOperator.Equals, ["1"]),
                new EvaluationPredicate("optional", EvaluationPredicateOperator.IsNull),
                new EvaluationPredicate("optional", EvaluationPredicateOperator.IsNotNull),
                new EvaluationPredicate("effective_at", EvaluationPredicateOperator.DateTimeRange,
                    ["2025-01-01T00:00:00+00:00", "2026-01-01T00:00:00+00:00"]),
                new EvaluationPredicate("amount", EvaluationPredicateOperator.Equals, ["not-a-number"]),
                new EvaluationPredicate("amount", EvaluationPredicateOperator.Range, ["malformed", "10"]),
                new EvaluationPredicate("name", EvaluationPredicateOperator.Range, ["a", "z"])
            };
            var contextConfiguration = new RuntimeConfiguration(
                [new("root", RuntimeSetting.FromRuntimeValue(root))]);

            foreach (var (connector, kind, properties, identity) in connectors)
            {
                Assert.Empty(connector.Capabilities.EvaluationScopePredicates ?? []);
                var context = new ConnectorContext("synthetic-system", "synthetic-endpoint", connector.Id,
                    "source-node", "Generic.Record", contextConfiguration);
                foreach (var predicate in predicates)
                {
                    var selector = new ArtifactSelector(kind, properties, identity,
                        new EvaluationScope("non-relational-contract", EvaluationScopeType.BoundedEvaluation, [predicate]));
                    var emitted = 0;
                    var error = await Assert.ThrowsAsync<ConnectorReadException>(async () =>
                    {
                        await foreach (var _ in connector.ReadAsync(context, selector, new ReadOptions(),
                            TestContext.Current.CancellationToken))
                            emitted++;
                    });
                    Assert.Equal(ConnectorIssueCodes.EvaluationScopeUnsupported, error.Code);
                    Assert.Equal(0, emitted);
                }
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
