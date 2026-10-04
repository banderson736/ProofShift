using System.Security.Cryptography;
using System.Text;
using ProofShift.Configuration;
using ProofShift.Connectors.Abstractions;
using ProofShift.Connectors.Csv;
using ProofShift.Connectors.Files;
using ProofShift.Domain;
using ProofShift.Engine;
using Xunit;

namespace ProofShift.EndToEnd.Tests;

public sealed class ConnectorRuntimeTests
{
    [Fact]
    public async Task FilesystemConnectorInspectsAndStreamsRelativeFileArtifactsWithSha256()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "members", "100"));
            var relativePath = "members/100/statement 2025.pdf";
            var content = Encoding.UTF8.GetBytes("synthetic document bytes\n");
            await File.WriteAllBytesAsync(Path.Combine(root, "members", "100", "statement 2025.pdf"), content,
                TestContext.Current.CancellationToken);
            var context = Context("files", [new KeyValuePair<string, string>("root", root)]);
            var selector = new ArtifactSelector("file-pattern",
                [new KeyValuePair<string, string>("pattern", "members/**/*.pdf")], ["relativePath"]);
            var connector = new FilesystemSourceConnector();

            var inspection = await connector.InspectAsync(context, selector, TestContext.Current.CancellationToken);
            var records = await ReadAllAsync(connector.ReadAsync(context, selector, new ReadOptions(), TestContext.Current.CancellationToken));

            Assert.Equal(SourceInspectionStatus.Valid, inspection.Status);
            Assert.Equal(1, inspection.Files);
            Assert.Equal("relativePath", Assert.Single(inspection.IdentityFields));
            var record = Assert.Single(records);
            Assert.Equal(relativePath, record.Artifact.Identity);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(),
                Assert.IsType<StringValue>(record.Values["sha256"]).Value);
            Assert.Equal(relativePath, record.Provenance.Location);
            Assert.Equal(content.LongLength, Assert.IsType<IntegerValue>(record.Values["size"]).Value);
            Assert.DoesNotContain(root, record.Artifact.Identity, StringComparison.Ordinal);
            var binary = Assert.IsType<BinaryReferenceValue>(record.Values["content"]);
            Assert.Equal(content.LongLength, binary.ContentLength);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(), binary.Sha256);
            var resolver = Assert.IsAssignableFrom<ISourceBinaryContentResolver>(connector);
            await using var sourceStream = await resolver.OpenBinaryReadAsync(
                Context("files", [new KeyValuePair<string, string>("root", root)]),
                selector,
                record.Artifact,
                binary,
                TestContext.Current.CancellationToken);
            using var copied = new MemoryStream();
            await sourceStream.CopyToAsync(copied, TestContext.Current.CancellationToken);
            Assert.Equal(content, copied.ToArray());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FilesystemConnectorRejectsTraversalAndMissingPatterns()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var context = Context("files", [new KeyValuePair<string, string>("root", root)]);
            var traversal = await new FilesystemSourceConnector().InspectAsync(
                context,
                new ArtifactSelector("file-pattern", [new KeyValuePair<string, string>("pattern", "../outside/**")]),
                TestContext.Current.CancellationToken);
            var missing = await new FilesystemSourceConnector().InspectAsync(
                context,
                new ArtifactSelector("file-pattern", [new KeyValuePair<string, string>("pattern", "nothing/**/*.pdf")]),
                TestContext.Current.CancellationToken);

            Assert.Contains(traversal.Issues, issue => issue.Code == ConnectorIssueCodes.InvalidPhysicalIdentifier);
            Assert.Contains(missing.Issues, issue => issue.Code == ConnectorIssueCodes.SourceObjectNotFound);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CsvConnectorStreamsQuotedUtf8FieldsAndCompositeIdentity()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var csvPath = Path.Combine(root, "supplemental.csv");
            await File.WriteAllTextAsync(csvPath,
                "MEMBER_ID;PAY_PERIOD;DISPLAY_NAME;EMPTY\n18291;2025-01;\"Member, Sample\";\n18291;2025-02;Zoë;\n",
                new UTF8Encoding(false),
                TestContext.Current.CancellationToken);
            var context = Context("csv", [new KeyValuePair<string, string>("root", root)]);
            var selector = new ArtifactSelector("csv",
                [
                    new KeyValuePair<string, string>("path", "supplemental.csv"),
                    new KeyValuePair<string, string>("delimiter", ";")
                ],
                ["MEMBER_ID", "PAY_PERIOD"]);
            var connector = new CsvSourceConnector();

            var inspection = await connector.InspectAsync(context, selector, TestContext.Current.CancellationToken);
            var records = await ReadAllAsync(connector.ReadAsync(context, selector, new ReadOptions(), TestContext.Current.CancellationToken));

            Assert.Equal(SourceInspectionStatus.Valid, inspection.Status);
            Assert.Null(inspection.EstimatedRecords);
            Assert.Equal(4, inspection.Columns.Count);
            Assert.Equal(2, records.Count);
            Assert.Equal("Member, Sample", Assert.IsType<StringValue>(records[0].Values["DISPLAY_NAME"]).Value);
            Assert.Equal(string.Empty, Assert.IsType<StringValue>(records[0].Values["EMPTY"]).Value);
            Assert.Equal("Zoë", Assert.IsType<StringValue>(records[1].Values["DISPLAY_NAME"]).Value);
            Assert.NotEqual(records[0].Artifact.Identity, records[1].Artifact.Identity);
            Assert.Equal("supplemental.csv", records[0].Provenance.Location);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CsvConnectorRejectsDuplicateCompositeIdentityAndMissingIdentityColumn()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(root, "duplicates.csv");
            await File.WriteAllTextAsync(path, "A,B,VALUE\n1,2,first\n1,2,second\n", TestContext.Current.CancellationToken);
            var context = Context("csv", [new KeyValuePair<string, string>("root", root)]);
            var selector = new ArtifactSelector("csv", [new KeyValuePair<string, string>("path", "duplicates.csv")], ["A", "B"]);
            var connector = new CsvSourceConnector();

            var duplicateInspection = await connector.InspectAsync(context, selector, TestContext.Current.CancellationToken);
            Assert.Equal(SourceInspectionStatus.Valid, duplicateInspection.Status);
            await Assert.ThrowsAsync<ConnectorReadException>(() => ReadAllAsync(
                connector.ReadAsync(context, selector, new ReadOptions(), TestContext.Current.CancellationToken)));

            var wrongIdentity = new ArtifactSelector("csv", [new KeyValuePair<string, string>("path", "duplicates.csv")], ["MISSING"]);
            var missingInspection = await connector.InspectAsync(context, wrongIdentity, TestContext.Current.CancellationToken);
            Assert.Contains(missingInspection.Issues, issue => issue.Code == ConnectorIssueCodes.IdentityFieldNotFound);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CsvConnectorRejectsTraversalAndSymlinkAncestorEscapes()
    {
        var root = CreateTemporaryDirectory();
        var outside = CreateTemporaryDirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(outside, "outside.csv"), "ID\n1\n", TestContext.Current.CancellationToken);
            var context = Context("csv", [new KeyValuePair<string, string>("root", root)]);
            var traversal = await new CsvSourceConnector().InspectAsync(
                context,
                new ArtifactSelector("csv", [new KeyValuePair<string, string>("path", "../outside/outside.csv")], ["ID"]),
                TestContext.Current.CancellationToken);
            Assert.Contains(traversal.Issues, issue => issue.Code == ConnectorIssueCodes.PathOutsideRoot);

            try
            {
                Directory.CreateSymbolicLink(Path.Combine(root, "linked"), outside);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
            {
                Assert.Skip("Host does not permit creation of a symlink for the containment regression test.");
            }

            var symlink = await new CsvSourceConnector().InspectAsync(
                context,
                new ArtifactSelector("csv", [new KeyValuePair<string, string>("path", "linked/outside.csv")], ["ID"]),
                TestContext.Current.CancellationToken);
            Assert.Contains(symlink.Issues, issue => issue.Code == ConnectorIssueCodes.PathOutsideRoot);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public async Task CsvReadSweepsAbandonedIdentityIndexFilesAndStoresNoRawIdentity()
    {
        var root = CreateTemporaryDirectory();
        var staleIndex = Path.Combine(Path.GetTempPath(), $"proofshift-identities-{Guid.NewGuid():N}.sqlite");
        var staleJournal = $"{staleIndex}-journal";
        const string rawIdentity = "synthetic-sensitive-identity-marker";
        try
        {
            var path = Path.Combine(root, "identities.csv");
            await File.WriteAllTextAsync(path, $"ID,VALUE\n{rawIdentity},row\n", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(staleIndex, "abandoned scratch data", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(staleJournal, "abandoned journal data", TestContext.Current.CancellationToken);
            File.SetLastWriteTimeUtc(staleIndex, DateTime.UtcNow.AddHours(-48));
            File.SetLastWriteTimeUtc(staleJournal, DateTime.UtcNow.AddHours(-48));
            var context = Context("csv", [new KeyValuePair<string, string>("root", root)]);
            var selector = new ArtifactSelector("csv", [new KeyValuePair<string, string>("path", "identities.csv")], ["ID"]);
            var existingScratchFiles = Directory.GetFiles(Path.GetTempPath(), "proofshift-identities-*.sqlite*")
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            string[] newScratchFiles = [];

            await using (var enumerator = new CsvSourceConnector()
                .ReadAsync(context, selector, new ReadOptions(), TestContext.Current.CancellationToken)
                .GetAsyncEnumerator(TestContext.Current.CancellationToken))
            {
                Assert.True(await enumerator.MoveNextAsync());
                newScratchFiles = Directory.GetFiles(Path.GetTempPath(), "proofshift-identities-*.sqlite*")
                    .Where(file => !existingScratchFiles.Contains(file)).ToArray();
                foreach (var scratchFile in newScratchFiles)
                {
                    await using var scratchStream = new FileStream(
                        scratchFile,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete,
                        4096,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    using var scratchContent = new MemoryStream();
                    await scratchStream.CopyToAsync(scratchContent, TestContext.Current.CancellationToken);
                    var bytes = scratchContent.ToArray();
                    Assert.DoesNotContain(rawIdentity, Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
                }

                Assert.Equal(rawIdentity, Assert.IsType<StringValue>(enumerator.Current.Values["ID"]).Value);
            }

            Assert.False(File.Exists(staleIndex));
            Assert.False(File.Exists(staleJournal));
            Assert.All(newScratchFiles, scratchFile => Assert.False(File.Exists(scratchFile)));
        }
        finally
        {
            try
            {
                File.Delete(staleIndex);
                File.Delete(staleJournal);
            }
            catch
            {
            }

            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RuntimeSettingsAndConnectorRegistryDoNotExposeSecretText()
    {
        const string syntheticSecret = "synthetic-password-do-not-print";
        var setting = RuntimeSetting.FromRuntimeValue(syntheticSecret, isSecret: true);
        var configuration = new RuntimeConfiguration([new KeyValuePair<string, RuntimeSetting>("password", setting)]);
        var registry = new ConnectorRegistry([new FilesystemSourceConnector(), new CsvSourceConnector()]);

        Assert.Equal("[REDACTED]", setting.ToString());
        Assert.Equal("[REDACTED]", configuration.ToString());
        Assert.DoesNotContain(syntheticSecret, string.Join('|', setting, configuration), StringComparison.Ordinal);
        Assert.Throws<ConnectorResolutionException>(() => registry.Resolve(new ConnectorId("unknown")));
    }

    [Fact]
    public void RuntimeContextResolvesSecretReferencesWithoutChangingHashedEndpointSettings()
    {
        const string secretName = "PROOFSHIFT_SYNTHETIC_CONNECTOR_SECRET";
        const string secretValue = "synthetic-connector-secret-only";
        var sourceEndpoint = Endpoint("member-store", "files", [new KeyValuePair<string, string>("root", $"secret:{secretName}")]);
        var sourceSystem = new SystemDefinition(new SystemId("source"), "Source", SystemRole.Source, [sourceEndpoint]);
        var loaded = LoadedConfiguration([sourceSystem], [EndpointDto("member-store", "files")]);
        var node = Node("source-node", sourceSystem.Id, sourceEndpoint.Id, MigrationNodeType.Source);
        var factory = new RuntimeConnectorContextFactory(new MapEnvironmentProvider(
            new Dictionary<string, string> { [secretName] = secretValue }));

        var context = factory.Create(loaded, node);
        var runtimeSetting = context.Configuration.GetRequired("root");

        Assert.True(runtimeSetting.IsSecret);
        Assert.Equal(secretValue, runtimeSetting.UseValue(value => value));
        Assert.Equal("[REDACTED]", runtimeSetting.ToString());
        Assert.DoesNotContain(secretValue, context.Configuration.ToString(), StringComparison.Ordinal);
        Assert.Equal($"secret:{secretName}", sourceEndpoint.Configuration["root"]);
    }

    [Fact]
    public async Task InspectionServiceResolvesAndInspectsOnlySourceNodes()
    {
        var sourceEndpoint = Endpoint("member-store", "synthetic");
        var targetEndpoint = Endpoint("target-store", "synthetic");
        var sourceSystem = new SystemDefinition(new SystemId("source"), "Source", SystemRole.Source, [sourceEndpoint]);
        var targetSystem = new SystemDefinition(new SystemId("target"), "Target", SystemRole.Target, [targetEndpoint]);
        var loaded = LoadedConfiguration(
            [sourceSystem, targetSystem],
            [EndpointDto("member-store", "synthetic"), EndpointDto("target-store", "synthetic")]);
        var source = Node("source-node", sourceSystem.Id, sourceEndpoint.Id, MigrationNodeType.Source);
        var target = Node("target-node", targetSystem.Id, targetEndpoint.Id, MigrationNodeType.Target);
        var graph = new MigrationGraph(new MigrationGraphId(Guid.NewGuid()), [source, target], [], "synthetic-graph-hash");
        var connector = new FakeSourceConnector("synthetic");
        var service = new SourceInspectionService(new ConnectorRegistry([connector]));

        var report = await service.InspectAsync(loaded, graph, TestContext.Current.CancellationToken);

        Assert.True(report.IsValid);
        Assert.Equal("Synthetic inspection project", report.ProjectName);
        Assert.Equal("source-node", Assert.Single(report.Sources).NodeKey);
        Assert.Equal(1, connector.InspectionCount);
    }

    [Fact]
    public void ConnectorRegistryRejectsDuplicateRegistrations()
    {
        Assert.Throws<ArgumentException>(() => new ConnectorRegistry(
            [new FakeSourceConnector("synthetic"), new FakeSourceConnector("synthetic")]));
    }

    [Fact]
    public void TemporalValueKindsPreserveInstantOffsetAndLocalWallClockSemantics()
    {
        var instant = new InstantValue(new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.FromHours(2)));
        var offset = new OffsetDateTimeValue(new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.FromHours(2)));
        var local = new LocalDateTimeValue(new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Unspecified));

        Assert.Equal(TimeSpan.Zero, instant.Value.Offset);
        Assert.Equal(TimeSpan.FromHours(2), offset.Value.Offset);
        Assert.Equal(DateTimeKind.Unspecified, local.Value.Kind);
        Assert.Throws<ArgumentException>(() => new LocalDateTimeValue(DateTime.SpecifyKind(local.Value, DateTimeKind.Local)));
    }

    private static ConnectorContext Context(string connector, IEnumerable<KeyValuePair<string, string>> settings) =>
        new("synthetic-system", "synthetic-endpoint", new ConnectorId(connector), "synthetic-node", "Generic.Record",
            new RuntimeConfiguration(settings.Select(pair => new KeyValuePair<string, RuntimeSetting>(
                pair.Key,
                RuntimeSetting.FromRuntimeValue(pair.Value)))));

    private static StorageEndpointDefinition Endpoint(
        string endpoint,
        string connector,
        IEnumerable<KeyValuePair<string, string>>? settings = null) =>
        new(new StorageEndpointId(endpoint), new ConnectorId(connector), settings);

    private static StorageEndpointConfigurationDto EndpointDto(string id, string connector) =>
        new(id, connector, []);

    private static MigrationNode Node(string name, SystemId system, StorageEndpointId endpoint, MigrationNodeType type) =>
        new(
            new MigrationNodeId(Guid.NewGuid()),
            name,
            type,
            "Generic.Record",
            system,
            endpoint,
            new ArtifactSelector("table", [new KeyValuePair<string, string>("name", "synthetic")], ["id"]));

    private static LoadedProjectConfiguration LoadedConfiguration(
        IEnumerable<SystemDefinition> systems,
        IEnumerable<StorageEndpointConfigurationDto> endpoints)
    {
        var systemDefinitions = systems.ToArray();
        var systemConfigurationDtos = systemDefinitions.Select(system => new SystemConfigurationDto(
            system.Id.Value,
            system.Name,
            system.Role.ToString(),
            endpoints.Where(endpoint => system.StorageEndpoints.Any(storage => storage.Id.Value == endpoint.Id))));
        var root = new RootConfigurationDto(
            1,
            new ProjectConfigurationDto("synthetic-runtime", "Synthetic inspection project"),
            null,
            [],
            null,
            null,
            null);
        return new LoadedProjectConfiguration(root, systemConfigurationDtos, systemDefinitions, [], "canonical", "hash");
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"proofshift-connector-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task<List<RecordEnvelope>> ReadAllAsync(IAsyncEnumerable<RecordEnvelope> records)
    {
        var result = new List<RecordEnvelope>();
        await foreach (var record in records.WithCancellation(TestContext.Current.CancellationToken))
        {
            result.Add(record);
        }

        return result;
    }

    private sealed class MapEnvironmentProvider(IReadOnlyDictionary<string, string> values) : IEnvironmentVariableProvider
    {
        public string? GetValue(string name) => values.GetValueOrDefault(name);
    }

    private sealed class FakeSourceConnector(string connectorId) : ISourceConnector
    {
        public ConnectorId Id { get; } = new(connectorId);
        public string Version => "test";
        public int InspectionCount { get; private set; }

        public Task<SourceInspection> InspectAsync(
            ConnectorContext context,
            ArtifactSelector selector,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InspectionCount++;
            return Task.FromResult(new SourceInspection(SourceInspectionStatus.Valid, physicalObject: "synthetic"));
        }

        public async IAsyncEnumerable<RecordEnvelope> ReadAsync(
            ConnectorContext context,
            ArtifactSelector selector,
            ReadOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            cancellationToken.ThrowIfCancellationRequested();
            yield break;
        }
    }
}
