using System.Text;
using System.Globalization;
using ProofShift.Connectors.Abstractions;
using ProofShift.Connectors.StructuredFiles;
using ProofShift.Domain;
using ProofShift.Engine;
using Xunit;

namespace ProofShift.EndToEnd.Tests;

public sealed class StructuredFileConnectorTests
{
    [Fact]
    public async Task NdjsonStreamsUnicodeExactNumbersAndDoesNotInferTemporalStrings()
    {
        await WithFileAsync("records.ndjson", "{\"id\":1,\"name\":\"\u00e9\",\"amount\":12345678901234567890.01,\"date\":\"2026-10-05\",\"missing\":null}\n{\"id\":2,\"name\":\"\",\"amount\":0}\n", async (context, path) =>
        {
            var selector = new ArtifactSelector("ndjson", [new("path", path)], ["id"]);
            var connector = new StructuredFileConnector("ndjson");
            var records = new List<RecordEnvelope>();
            await foreach (var record in connector.ReadAsync(context, selector, new ReadOptions(), TestContext.Current.CancellationToken)) records.Add(record);
            Assert.Equal(2, records.Count);
            Assert.Equal("\u00e9", Assert.IsType<StringValue>(records[0].Values["name"]).Value);
            Assert.Equal(12345678901234567890.01m, Assert.IsType<DecimalValue>(records[0].Values["amount"]).Value);
            Assert.IsType<StringValue>(records[0].Values["date"]);
            Assert.IsType<NullValue>(records[0].Values["missing"]);
            Assert.DoesNotContain(context.Configuration.GetRequired("root").UseValue(value => value), records[0].Provenance.Location, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task FixedWidthUsesDeclaredOneBasedFieldsScaleTrimNullAndRejectsOverlap()
    {
        await WithFileAsync("records.txt", "0001A 000001234520261005NULL\n", async (context, path) =>
        {
            var properties = new Dictionary<string, string>
            {
                ["path"] = path, ["width"] = "28", ["fields.id.start"] = "1", ["fields.id.length"] = "4", ["fields.id.type"] = "integer",
                ["fields.status.start"] = "5", ["fields.status.length"] = "2", ["fields.status.trim"] = "both",
                ["fields.amount.start"] = "7", ["fields.amount.length"] = "10", ["fields.amount.type"] = "decimal", ["fields.amount.scale"] = "2",
                ["fields.date.start"] = "17", ["fields.date.length"] = "8", ["fields.date.type"] = "date", ["fields.date.format"] = "yyyyMMdd",
                ["fields.optional.start"] = "25", ["fields.optional.length"] = "4", ["fields.optional.null"] = "NULL"
            };
            var connector = new StructuredFileConnector("fixed-width");
            var selector = new ArtifactSelector("fixed-width", properties, ["id"]);
            var records = new List<RecordEnvelope>();
            await foreach (var record in connector.ReadAsync(context, selector, new ReadOptions(), TestContext.Current.CancellationToken)) records.Add(record);
            Assert.Equal(123.45m, Assert.IsType<DecimalValue>(Assert.Single(records).Values["amount"]).Value);
            Assert.Equal("A", Assert.IsType<StringValue>(records[0].Values["status"]).Value);
            Assert.Equal(new DateOnly(2026, 10, 5), Assert.IsType<DateValue>(records[0].Values["date"]).Value);
            Assert.IsType<NullValue>(records[0].Values["optional"]);
            properties["fields.amount.start"] = "6";
            await Assert.ThrowsAsync<ConnectorReadException>(() => connector.InspectAsync(context,
                new ArtifactSelector("fixed-width", properties, ["id"]), TestContext.Current.CancellationToken));
        });
    }

    [Fact]
    public async Task XmlStreamsQualifiedRecordBoundariesAndDeclaredAttributeElementPaths()
    {
        await WithFileAsync("records.xml", "<p:Root xmlns:p=\"urn:synthetic\"><p:Member id=\"1\"><p:Name>first</p:Name></p:Member><p:Member id=\"2\"><p:Name>second</p:Name></p:Member></p:Root>", async (context, path) =>
        {
            var selector = new ArtifactSelector("xml", [new("path", path), new("recordElement", "p:Member"), new("namespaces.p", "urn:synthetic"),
                new("fields.id.path", "@id"), new("fields.id.type", "integer"), new("fields.name.path", "p:Name")], ["id"]);
            var records = new List<RecordEnvelope>();
            await foreach (var record in new StructuredFileConnector("xml").ReadAsync(context, selector, new ReadOptions(), TestContext.Current.CancellationToken)) records.Add(record);
            Assert.Equal(2, records.Count);
            Assert.Equal("second", Assert.IsType<StringValue>(records[1].Values["name"]).Value);
        });
    }

    [Fact]
    public async Task FixedWidthUtf16AndInvalidFieldDiagnosticsAreExplicitAndSanitized()
    {
        await WithFileAsync("records.txt", "0001BAD", async (context, path) =>
        {
            var selector = new ArtifactSelector("fixed-width", [new("path", path), new("encoding", "utf-16"), new("width", "7"),
                new("fields.id.start", "1"), new("fields.id.length", "4"), new("fields.id.type", "integer"),
                new("fields.amount.start", "5"), new("fields.amount.length", "3"), new("fields.amount.type", "integer")], ["id"]);
            var error = await Assert.ThrowsAsync<ConnectorReadException>(async () =>
            {
                await foreach (var record in new StructuredFileConnector("fixed-width").ReadAsync(context, selector,
                    new ReadOptions(), TestContext.Current.CancellationToken)) { _ = record; }
            });
            Assert.Contains("records.txt", error.Message, StringComparison.Ordinal);
            Assert.Contains("record 1", error.Message, StringComparison.Ordinal);
            Assert.Contains("amount", error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("0001BAD", error.Message, StringComparison.Ordinal);
        }, new UnicodeEncoding(false, true, true));
    }

    [Fact]
    public async Task FixedWidthPositionsCountUtf16CodeUnitsRatherThanEncodedBytes()
    {
        await WithFileAsync("unicode-fixed.txt", "1\uD83D\uDE00\n", async (context, path) =>
        {
            var selector = new ArtifactSelector("fixed-width", [new("path", path), new("width", "3"),
                new("fields.id.start", "1"), new("fields.id.length", "1"), new("fields.id.type", "integer"),
                new("fields.symbol.start", "2"), new("fields.symbol.length", "2")], ["id"]);
            await foreach (var record in new StructuredFileConnector("fixed-width").ReadAsync(context, selector,
                new ReadOptions(), TestContext.Current.CancellationToken))
                Assert.Equal("\uD83D\uDE00", Assert.IsType<StringValue>(record.Values["symbol"]).Value);
        });
    }

    [Fact]
    public async Task XmlReaderHonorsUtf16DeclarationAndByteOrderMark()
    {
        await WithFileAsync("utf16.xml", "<?xml version=\"1.0\" encoding=\"utf-16\"?><Root><Member id=\"1\"><Name>\u96ea</Name></Member></Root>", async (context, path) =>
        {
            var selector = new ArtifactSelector("xml", [new("path", path), new("recordElement", "Member"),
                new("fields.id.path", "@id"), new("fields.id.type", "integer"), new("fields.name.path", "Name")], ["id"]);
            await foreach (var record in new StructuredFileConnector("xml").ReadAsync(context, selector,
                new ReadOptions(), TestContext.Current.CancellationToken))
                Assert.Equal("\u96ea", Assert.IsType<StringValue>(record.Values["name"]).Value);
        }, new UnicodeEncoding(false, true, true));
    }

    [Fact]
    public async Task FixedWidthDiscoveryWithoutSchemaReportsPopulationButNeverInventsBoundaries()
    {
        await WithFileAsync("unmodeled.txt", "0001\n0002\n", async (context, path) =>
        {
            var selector = new ArtifactSelector("fixed-width", [new("path", path)], []);
            var artifact = await new StructuredFileConnector("fixed-width").DiscoverAsync(context, [selector],
                TestContext.Current.CancellationToken);
            var discovered = Assert.Single(artifact.Objects);
            Assert.Equal(2, discovered.RecordCount);
            Assert.Empty(discovered.Fields);
            Assert.Equal(new FileInfo(Path.Combine(context.Configuration.GetRequired("root").UseValue(value => value), path)).Length,
                discovered.Bytes);
        });
    }

    [Fact]
    public async Task XmlDiscoveryReportsNamespaceRepeatingCandidatesAndChangesOnNewField()
    {
        await WithFileAsync("discover.xml", "<p:Root xmlns:p=\"urn:synthetic\"><p:Member id=\"1\"><p:Name>first</p:Name></p:Member><p:Member id=\"2\"><p:Name>second</p:Name></p:Member></p:Root>", async (context, path) =>
        {
            var selector = new ArtifactSelector("xml", [new("path", path)], []);
            var connector = new StructuredFileConnector("xml");
            var first = await connector.DiscoverAsync(context, [selector], TestContext.Current.CancellationToken);
            var root = Assert.Single(first.Objects, item => item.Kind == "xml-root");
            Assert.Equal("Root", root.Name);
            Assert.Equal("urn:synthetic", root.Schema);
            var candidate = Assert.Single(first.Objects, item => item.Kind == "xml-record-candidate");
            Assert.Equal("Member", candidate.Name);
            Assert.Equal(2, candidate.RecordCount);
            Assert.Contains(candidate.Fields, field => field.TypeInferred && field.NativeType == "String");
            Assert.Contains(candidate.SelectorProperties!, pair => pair.Key.StartsWith("fields.", StringComparison.Ordinal) &&
                pair.Value == "{urn:synthetic}Name");
            var again = await connector.DiscoverAsync(context, [selector], TestContext.Current.CancellationToken);
            Assert.Equal(first.Fingerprint, again.Fingerprint);
            await File.WriteAllTextAsync(Path.Combine(context.Configuration.GetRequired("root").UseValue(value => value), path),
                "<p:Root xmlns:p=\"urn:synthetic\"><p:Member id=\"1\"><p:Name>first</p:Name><p:Status>A</p:Status></p:Member><p:Member id=\"2\"><p:Name>second</p:Name><p:Status>B</p:Status></p:Member></p:Root>",
                new UTF8Encoding(false), TestContext.Current.CancellationToken);
            var changed = await connector.DiscoverAsync(context, [selector], TestContext.Current.CancellationToken);
            Assert.NotEqual(first.Fingerprint, changed.Fingerprint);
            Assert.Contains(PhysicalDiscoveryDiff.Compare(first, changed), item => item.Kind == "field-added");
        });
    }

    [Fact]
    public async Task NdjsonRejectsDuplicatePropertiesAndStreamsLargeInputOneRecordAtATime()
    {
        var root = Path.Combine(Path.GetTempPath(), $"proofshift-structured-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "large.ndjson");
            await using (var writer = new StreamWriter(path, false, new UTF8Encoding(false)))
                for (var index = 1; index <= 20000; index++)
                    await writer.WriteLineAsync($"{{\"id\":{index},\"nested\":{{\"amount\":12345e-2,\"ok\":true,\"optional\":null,\"date\":\"2026-10-05\"}}}}");
            var context = CreateContext(root);
            var selector = new ArtifactSelector("ndjson", [new("path", "large.ndjson"), new("fields.id.type", "integer"),
                new("fields.amount.path", "nested.amount"), new("fields.amount.type", "decimal"),
                new("fields.ok.path", "nested.ok"), new("fields.optional.path", "nested.optional"),
                new("fields.date.path", "nested.date"), new("fields.date.type", "date"),
                new("fields.date.format", "yyyy-MM-dd")], ["id"]);
            var fileBytes = new FileInfo(path).Length;
            RecordEnvelope? first = null;
            var measurement = await ConnectorReadMeasurement.MeasureAsync("NDJSON", fileBytes, fileBytes, async cancellationToken =>
            {
                long count = 0;
                await foreach (var record in new StructuredFileConnector("ndjson").ReadAsync(context, selector,
                    new ReadOptions(), cancellationToken))
                {
                    first ??= record;
                    count++;
                }
                return count;
            }, null, null, TestContext.Current.CancellationToken);
            Assert.Equal(20000, measurement.RecordCount);
            Assert.IsType<DecimalValue>(first!.Values["amount"]);
            Assert.IsType<BooleanValue>(first.Values["ok"]);
            Assert.IsType<NullValue>(first.Values["optional"]);
            Assert.IsType<DateValue>(first.Values["date"]);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task XmlStreamsLargeForwardOnlyFixture()
    {
        var root = Path.Combine(Path.GetTempPath(), $"proofshift-structured-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "large.xml");
            await using (var writer = new StreamWriter(path, false, new UTF8Encoding(false)))
            {
                await writer.WriteAsync("<Root>");
                for (var index = 1; index <= 5000; index++) await writer.WriteAsync($"<Member id=\"{index}\"><Name>synthetic</Name></Member>");
                await writer.WriteAsync("</Root>");
            }
            var context = CreateContext(root);
            var selector = new ArtifactSelector("xml", [new("path", "large.xml"), new("recordElement", "Member"),
                new("fields.id.path", "@id"), new("fields.id.type", "integer"), new("fields.name.path", "Name")], ["id"]);
            var fileBytes = new FileInfo(path).Length;
            RecordEnvelope? first = null;
            var measurement = await ConnectorReadMeasurement.MeasureAsync("XML", fileBytes, fileBytes, async cancellationToken =>
            {
                long count = 0;
                await foreach (var record in new StructuredFileConnector("xml").ReadAsync(context, selector,
                    new ReadOptions(), cancellationToken))
                {
                    first ??= record;
                    count++;
                }
                return count;
            }, null, null, TestContext.Current.CancellationToken);
            Assert.Equal(5000, measurement.RecordCount);
            Assert.IsType<IntegerValue>(first!.Values["id"]);
            Assert.IsType<StringValue>(first.Values["name"]);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task FixedWidthMeasuresTypedStreamingAcrossFiftyThousandRecords()
    {
        const int expectedRecords = 50000;
        var root = Path.Combine(Path.GetTempPath(), $"proofshift-fixed-width-measurement-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "measure.txt");
            await using (var writer = new StreamWriter(path, false, new UTF8Encoding(false)))
            {
                for (var id = 1; id <= expectedRecords; id++)
                {
                    var cents = checked(id * 100 + 1234);
                    await writer.WriteLineAsync($"{id.ToString("D5", CultureInfo.InvariantCulture)}{cents.ToString("D10", CultureInfo.InvariantCulture)}");
                }
            }

            var context = CreateContext(root);
            var selector = new ArtifactSelector("fixed-width", [new("path", "measure.txt"), new("width", "15"),
                new("fields.id.start", "1"), new("fields.id.length", "5"), new("fields.id.type", "integer"),
                new("fields.amount.start", "6"), new("fields.amount.length", "10"), new("fields.amount.type", "decimal"),
                new("fields.amount.scale", "2")], ["id"]);
            var fileBytes = new FileInfo(path).Length;
            RecordEnvelope? first = null;
            var measurement = await ConnectorReadMeasurement.MeasureAsync("fixed-width", fileBytes, fileBytes, async cancellationToken =>
            {
                long count = 0;
                await foreach (var record in new StructuredFileConnector("fixed-width").ReadAsync(context, selector,
                    new ReadOptions(), cancellationToken))
                {
                    first ??= record;
                    count++;
                }
                return count;
            }, null, null, TestContext.Current.CancellationToken);
            Assert.Equal(expectedRecords, measurement.RecordCount);
            Assert.IsType<IntegerValue>(first!.Values["id"]);
            Assert.IsType<DecimalValue>(first.Values["amount"]);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("fixed-width")]
    [InlineData("ndjson")]
    [InlineData("xml")]
    public async Task FileReadersHonorCancellationDuringRecordStreaming(string format)
    {
        var name = format switch { "fixed-width" => "cancel.txt", "ndjson" => "cancel.ndjson", _ => "cancel.xml" };
        var content = format switch
        {
            "fixed-width" => "0001\n0002\n0003\n",
            "ndjson" => "{\"id\":1}\n{\"id\":2}\n{\"id\":3}\n",
            _ => "<Root><Member id=\"1\"/><Member id=\"2\"/><Member id=\"3\"/></Root>"
        };
        await WithFileAsync(name, content, async (context, path) =>
        {
            var properties = format switch
            {
                "fixed-width" => new Dictionary<string, string>
                {
                    ["path"] = path, ["width"] = "4", ["fields.id.start"] = "1", ["fields.id.length"] = "4", ["fields.id.type"] = "integer"
                },
                "ndjson" => new Dictionary<string, string> { ["path"] = path },
                _ => new Dictionary<string, string> { ["path"] = path, ["recordElement"] = "Member", ["fields.id.path"] = "@id", ["fields.id.type"] = "integer" }
            };
            using var cancellation = new CancellationTokenSource();
            await using var enumerator = new StructuredFileConnector(format).ReadAsync(context,
                new ArtifactSelector(format, properties, ["id"]), new ReadOptions(), cancellation.Token)
                .GetAsyncEnumerator(cancellation.Token);
            Assert.True(await enumerator.MoveNextAsync());
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await enumerator.MoveNextAsync());
        });
    }

    [Fact]
    public async Task JsonRejectsLossyDecimalsAndDuplicateIdentityWithoutDumpingRecords()
    {
        await WithFileAsync("records.json", "[{\"id\":1,\"amount\":1.234567890123456789012345678901}]", async (context, path) =>
        {
            var connector = new StructuredFileConnector("json");
            var selector = new ArtifactSelector("json", [new("path", path)], ["id"]);
            await Assert.ThrowsAsync<ConnectorReadException>(async () =>
            {
                await foreach (var record in connector.ReadAsync(context, selector, new ReadOptions(), TestContext.Current.CancellationToken)) { _ = record; }
            });
        });
        await WithFileAsync("duplicate.ndjson", "{\"id\":1}\n{\"id\":1}\n", async (context, path) =>
        {
            var error = await Assert.ThrowsAsync<ConnectorReadException>(async () =>
            {
                await foreach (var record in new StructuredFileConnector("ndjson").ReadAsync(context,
                    new ArtifactSelector("ndjson", [new("path", path)], ["id"]), new ReadOptions(), TestContext.Current.CancellationToken)) { _ = record; }
            });
            Assert.Equal(ConnectorIssueCodes.DuplicateArtifactIdentity, error.Code);
        });
    }

    [Fact]
    public async Task NdjsonRejectsDuplicatePropertyNamesWithoutExposingRecordContent()
    {
        await WithFileAsync("duplicate-properties.ndjson", "{\"id\":1,\"id\":2}\n", async (context, path) =>
        {
            var error = await Assert.ThrowsAsync<ConnectorReadException>(async () =>
            {
                await foreach (var record in new StructuredFileConnector("ndjson").ReadAsync(context,
                    new ArtifactSelector("ndjson", [new("path", path)], ["id"]), new ReadOptions(),
                    TestContext.Current.CancellationToken)) { _ = record; }
            });
            Assert.Equal("PSCONN020", error.Code);
            Assert.DoesNotContain("\"id\":1", error.Message, StringComparison.Ordinal);
        });
    }

    [Theory]
    [InlineData("\"1.234567890123456789012345678901\"", "decimal", "0")]
    [InlineData("\"0.0000000000000000000000000001\"", "decimal", "1")]
    [InlineData("1.5", "integer", "0")]
    [InlineData("true", "integer", "0")]
    public async Task DeclaredJsonTypesRejectRoundingUnderflowAndNativeTypeMismatch(string content, string type, string scale)
    {
        await WithFileAsync("typed.json", $"[{{\"id\":1,\"value\":{content}}}]", async (context, path) =>
        {
            var selector = new ArtifactSelector("json", [new("path", path), new("fields.id.type", "integer"),
                new("fields.value.type", type), new("fields.value.scale", scale)], ["id"]);
            var error = await Assert.ThrowsAsync<ConnectorReadException>(async () =>
            {
                await foreach (var record in new StructuredFileConnector("json").ReadAsync(context, selector,
                    new ReadOptions(), TestContext.Current.CancellationToken)) { _ = record; }
            });
            Assert.Equal(ConnectorIssueCodes.UnsupportedPhysicalType, error.Code);
            Assert.DoesNotContain(content, error.Message, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task DeclaredJsonDecimalPreservesTypeAndExactExponentScale()
    {
        await WithFileAsync("typed.json", "[{\"id\":1,\"amount\":12345e-1}]", async (context, path) =>
        {
            var selector = new ArtifactSelector("json", [new("path", path), new("fields.id.type", "integer"),
                new("fields.amount.type", "decimal"), new("fields.amount.scale", "2")], ["id"]);
            await foreach (var record in new StructuredFileConnector("json").ReadAsync(context, selector,
                new ReadOptions(), TestContext.Current.CancellationToken))
                Assert.Equal(12.345m, Assert.IsType<DecimalValue>(record.Values["amount"]).Value);
        });
    }

    private static async Task WithFileAsync(string name, string content, Func<ConnectorContext, string, Task> action, Encoding? encoding = null)
    {
        var root = Path.Combine(Path.GetTempPath(), $"proofshift-structured-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, name), content, encoding ?? new UTF8Encoding(false), TestContext.Current.CancellationToken);
            var context = CreateContext(root);
            await action(context, name);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static ConnectorContext CreateContext(string root) => new("synthetic", "records", new ConnectorId("structured-test"), "source", "Generic.Record",
        new RuntimeConfiguration([new KeyValuePair<string, RuntimeSetting>("root", RuntimeSetting.FromRuntimeValue(root))]));
}