using ProofShift.Connectors.Abstractions;
using ProofShift.Connectors.Files;
using ProofShift.Domain;
using Xunit;

namespace ProofShift.EndToEnd.Tests;

public sealed class ShadowTargetConnectorTests
{
    [Fact]
    public async Task FilesystemShadowStreamsBinaryIntoIsolatedContainedRunAndReadsItBack()
    {
        var sourceRoot = CreateTemporaryDirectory();
        var shadowRoot = CreateTemporaryDirectory();
        var content = Enumerable.Range(0, 1024 * 1024).Select(index => (byte)(index % 251)).ToArray();
        try
        {
            Directory.CreateDirectory(Path.Combine(sourceRoot, "members"));
            await File.WriteAllBytesAsync(Path.Combine(sourceRoot, "members", "statement.bin"), content, TestContext.Current.CancellationToken);
            var sourceContext = Context("files", "source-system", "source-files", "source-node", sourceRoot);
            var sourceSelector = new ArtifactSelector("file-pattern", [new KeyValuePair<string, string>("pattern", "members/**/*.bin")], ["relativePath"]);
            var sourceConnector = new FilesystemSourceConnector();
            var source = Assert.Single(await ReadAllAsync(sourceConnector.ReadAsync(sourceContext, sourceSelector,
                new ReadOptions(), TestContext.Current.CancellationToken)));
            var binary = Assert.IsType<BinaryReferenceValue>(source.Values["content"]);
            var resolver = Assert.IsAssignableFrom<ISourceBinaryContentResolver>(sourceConnector);
            var targetConnector = new FilesystemShadowTargetConnector();
            var targetSelector = new ArtifactSelector("file-pattern", [new KeyValuePair<string, string>("pathField", "relativePath")], ["relativePath"]);
            var firstContext = ShadowContext("target-system", "shadow-files", "target-documents", shadowRoot, Guid.NewGuid());
            var secondContext = ShadowContext("target-system", "shadow-files", "target-documents", shadowRoot, Guid.NewGuid());
            await targetConnector.PrepareAsync(firstContext, targetSelector, TestContext.Current.CancellationToken);
            await targetConnector.PrepareAsync(secondContext, targetSelector, TestContext.Current.CancellationToken);

            var relativePath = Assert.IsType<StringValue>(source.Values["relativePath"]).Value;
            var targetRecord = CreateTargetRecord(firstContext, relativePath, binary);
            var request = new ShadowWriteRequest(firstContext, targetSelector, targetRecord, "target-documents",
                (field, token) => resolver.OpenBinaryReadAsync(sourceContext, sourceSelector, source.Artifact,
                    Assert.IsType<BinaryReferenceValue>(source.Values[field]), token));
            await targetConnector.WriteAsync(request, TestContext.Current.CancellationToken);
            await targetConnector.CompleteAsync(firstContext, TestContext.Current.CancellationToken);

            var runRoot = Path.Combine(shadowRoot, firstContext.RunId.Value.ToString("N"));
            var projectedPath = Path.Combine(runRoot, "target-documents", "members", "statement.bin");
            Assert.Equal(content, await File.ReadAllBytesAsync(projectedPath, TestContext.Current.CancellationToken));
            Assert.False(File.Exists(Path.Combine(shadowRoot, secondContext.RunId.Value.ToString("N"), "target-documents", "members", "statement.bin")));
            var readBack = Assert.Single(await ReadAllAsync(targetConnector.ReadAsync(new ReadRequest(firstContext, targetSelector),
                TestContext.Current.CancellationToken)));
            var readBackBinary = Assert.IsType<BinaryReferenceValue>(readBack.Values["content"]);
            Assert.Equal(content.LongLength, readBackBinary.ContentLength);
            Assert.Equal(binary.Sha256, readBackBinary.Sha256);

            await Assert.ThrowsAsync<ProjectionConnectorException>(() => targetConnector.WriteAsync(request, TestContext.Current.CancellationToken));
            var duplicateWithDifferentPath = CreateTargetRecord(firstContext, "members/alternate.bin", binary, targetRecord.Artifact.Identity);
            await Assert.ThrowsAsync<ProjectionConnectorException>(() => targetConnector.WriteAsync(
                new ShadowWriteRequest(firstContext, targetSelector, duplicateWithDifferentPath, "target-documents", request.OpenBinaryReadAsync),
                TestContext.Current.CancellationToken));
            Assert.False(File.Exists(Path.Combine(runRoot, "target-documents", "members", "alternate.bin")));

            var badHashContext = ShadowContext("target-system", "shadow-files", "target-documents", shadowRoot, Guid.NewGuid());
            await targetConnector.PrepareAsync(badHashContext, targetSelector, TestContext.Current.CancellationToken);
            var incorrectBinary = new BinaryReferenceValue(binary.Reference, binary.ContentLength, new string('0', 64));
            var incorrectRecord = CreateTargetRecord(badHashContext, relativePath, incorrectBinary);
            await Assert.ThrowsAsync<ProjectionConnectorException>(() => targetConnector.WriteAsync(
                new ShadowWriteRequest(badHashContext, targetSelector, incorrectRecord, "target-documents", request.OpenBinaryReadAsync),
                TestContext.Current.CancellationToken));
            Assert.False(File.Exists(Path.Combine(shadowRoot, badHashContext.RunId.Value.ToString("N"), "target-documents", "members", "statement.bin")));

            var traversalRecord = CreateTargetRecord(firstContext, "../outside.bin", binary);
            await Assert.ThrowsAsync<ProjectionConnectorException>(() => targetConnector.WriteAsync(
                new ShadowWriteRequest(firstContext, targetSelector, traversalRecord, "target-documents",
                    request.OpenBinaryReadAsync), TestContext.Current.CancellationToken));
            Assert.False(File.Exists(Path.Combine(runRoot, "outside.bin")));
        }
        finally
        {
            Directory.Delete(sourceRoot, recursive: true);
            Directory.Delete(shadowRoot, recursive: true);
        }
    }

    [Fact]
    public async Task FilesystemShadowRejectsSymlinkEscapeWhenHostAllowsSymlinkCreation()
    {
        var shadowRoot = CreateTemporaryDirectory();
        var outside = CreateTemporaryDirectory();
        try
        {
            var targetConnector = new FilesystemShadowTargetConnector();
            var targetSelector = new ArtifactSelector("file-pattern", [new KeyValuePair<string, string>("pathField", "relativePath")], ["relativePath"]);
            var context = ShadowContext("target-system", "shadow-files", "target-documents", shadowRoot, Guid.NewGuid());
            await targetConnector.PrepareAsync(context, targetSelector, TestContext.Current.CancellationToken);
            var runRoot = Path.Combine(shadowRoot, context.RunId.Value.ToString("N"));
            try
            {
                Directory.CreateSymbolicLink(Path.Combine(runRoot, "target-documents"), outside);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
            {
                Assert.Skip("Host does not permit symlink creation for shadow-target containment coverage.");
            }

            var binary = new BinaryReferenceValue("synthetic-reference", 0, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData([])).ToLowerInvariant());
            var record = CreateTargetRecord(context, "escape.bin", binary);
            await Assert.ThrowsAsync<ProjectionConnectorException>(() => targetConnector.WriteAsync(
                new ShadowWriteRequest(context, targetSelector, record, "target-documents"), TestContext.Current.CancellationToken));
            Assert.Empty(Directory.GetFiles(outside));
        }
        finally
        {
            Directory.Delete(shadowRoot, recursive: true);
            Directory.Delete(outside, recursive: true);
        }
    }

    private static RecordEnvelope CreateTargetRecord(ShadowTargetContext context, string relativePath, BinaryReferenceValue binary,
        string? identityOverride = null)
    {
        var identityValue = $"string:{relativePath.Normalize(System.Text.NormalizationForm.FormC)}";
        var identity = identityOverride ?? $"11:relativePath={identityValue.Length}:{identityValue}";
        return new RecordEnvelope(
            new ArtifactReference(new ArtifactId(StableArtifactIdentity.CreateArtifactId(context.ConnectorContext.SystemKey,
                    context.ConnectorContext.EndpointKey, "file", identity)), new SystemId(context.ConnectorContext.SystemKey),
                new StorageEndpointId(context.ConnectorContext.EndpointKey), "file", identity),
            "Pension.Document",
            new Dictionary<string, ValueNode>
            {
                ["relativePath"] = new StringValue(relativePath),
                ["content"] = binary
            },
            new ProvenanceMetadata(new ConnectorId("files"), new StorageEndpointId(context.ConnectorContext.EndpointKey),
                relativePath, DateTimeOffset.UnixEpoch));
    }

    private static ShadowTargetContext ShadowContext(string system, string endpoint, string node, string root, Guid runId) =>
        new(Context("files", system, endpoint, node, root), new RunId(runId), SystemRole.ShadowTarget);

    private static ConnectorContext Context(string connector, string system, string endpoint, string node, string root) =>
        new(system, endpoint, new ConnectorId(connector), node, "Pension.Document", new RuntimeConfiguration(
            [new KeyValuePair<string, RuntimeSetting>("root", RuntimeSetting.FromRuntimeValue(root))]));

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"proofshift-shadow-files-{Guid.NewGuid():N}");
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
}