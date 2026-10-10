using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace ProofShift.EndToEnd.Tests;

public sealed class RemoteConnectorAuthoringTests
{
    [Fact]
    public async Task CapabilitiesDescribeTransportsRangeSupportAndNoShadowWrite()
    {
        var result = await RunAsync("capabilities", "--json");
        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        var connectors = document.RootElement.GetProperty("connectors").EnumerateArray().ToDictionary(item => item.GetProperty("id").GetString()!);
        foreach (var id in new[] { "s3", "azure-blob", "sftp", "parquet" })
        {
            var connector = connectors[id];
            Assert.True(connector.GetProperty("discovery").GetBoolean());
            Assert.True(connector.GetProperty("checkpointCapture").GetBoolean());
            Assert.True(connector.GetProperty("offlineReplay").GetBoolean());
            Assert.True(connector.GetProperty("targetObservation").GetBoolean());
            Assert.True(connector.GetProperty("binaryStreaming").GetBoolean());
            Assert.True(connector.GetProperty("rangeRead").GetBoolean());
            Assert.False(connector.GetProperty("shadowWrite").GetBoolean());
        }

        Assert.Equal("s3", connectors["s3"].GetProperty("transport").GetString());
        Assert.True(connectors["s3"].GetProperty("versionPinning").GetBoolean());
        Assert.True(connectors["azure-blob"].GetProperty("versionPinning").GetBoolean());
        Assert.False(connectors["sftp"].GetProperty("versionPinning").GetBoolean());
        Assert.True(connectors["parquet"].GetProperty("structuredRead").GetBoolean());
        Assert.Equal("azure-blob|filesystem|s3|sftp", connectors["parquet"].GetProperty("transport").GetString());
        Assert.False(connectors["files"].GetProperty("rangeRead").GetBoolean());
    }

    [Fact]
    public async Task SchemasForTheSecretBearingEndpointsRequireSecretReferencesAndNeverEmbedValues()
    {
        foreach (var id in new[] { "s3", "azure-blob", "sftp" })
        {
            var result = await RunAsync("connectors", "schema", id);
            Assert.Equal(0, result.ExitCode);
            using var document = JsonDocument.Parse(result.Output);
            var endpoint = document.RootElement.GetProperty("properties").GetProperty("endpoint").GetProperty("properties");
            foreach (var secret in new[] { "accessKeyId", "secretAccessKey", "sessionToken", "accountKey", "sasToken", "password", "privateKey", "privateKeyPassphrase" })
                if (endpoint.TryGetProperty(secret, out var property)) Assert.True(property.TryGetProperty("oneOf", out _), $"{id}.{secret} must accept references");
        }

        var sftp = await RunAsync("connectors", "schema", "sftp");
        using var sftpSchema = JsonDocument.Parse(sftp.Output);
        var required = sftpSchema.RootElement.GetProperty("properties").GetProperty("endpoint").GetProperty("required").EnumerateArray().Select(item => item.GetString()).ToArray();
        Assert.Contains("hostKeyFingerprint", required);
    }

    [Fact]
    public async Task ScaffoldedRemoteProjectIsCredentialFreeAndStrictValidationFlagsInlineSecretsWithoutEchoingThem()
    {
        var root = Path.Combine(Path.GetTempPath(), $"proofshift-remote-authoring-{Guid.NewGuid():N}");
        try
        {
            Assert.Equal(0, (await RunAsync("init", root)).ExitCode);
            var systemPath = Path.Combine(root, "systems", "source.yaml");
            const string leaked = "AKIASYNTHETICLEAKEDKEY99";
            await File.WriteAllTextAsync(systemPath, string.Join('\n',
                "\"id\": \"source\"", "\"name\": \"Source\"", "\"role\": \"source\"", "\"storage\":", "  \"records\":",
                "    \"connector\": \"s3\"", "    \"bucket\": \"synthetic-bucket\"", "    \"region\": \"us-east-1\"", "    \"authentication\": \"static\"",
                $"    \"accessKeyId\": \"{leaked}\"", "    \"secretAccessKey\":", "      \"secret\": \"PS_SYNTHETIC_SECRET\"", "..."), TestContext.Current.CancellationToken);
            var validation = await RunAsync("validate", Path.Combine(root, "proofshift.yaml"), "--strict", "--json");
            Assert.Equal(1, validation.ExitCode);
            Assert.Contains("PSCFG012", validation.Output, StringComparison.Ordinal);
            Assert.DoesNotContain(leaked, validation.Output + validation.Error, StringComparison.Ordinal);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunAsync(params string[] args)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "ProofShift.Cli.dll"));
        foreach (var argument in args) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("CLI process did not start.");
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return (process.ExitCode, await output, await error);
    }
}
