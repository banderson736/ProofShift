using ProofShift.Configuration;
using ProofShift.Domain;
using Xunit;

namespace ProofShift.Configuration.Tests;

public sealed class ConfigurationLoaderTests
{
    private const string RuntimeSecret = "synthetic-runtime-secret-2026";

    [Fact]
    public async Task ValidFixtureLoadsMultipleSystemsAndCreatesSafeDomainDefinitions()
    {
        var result = await LoadFixtureAsync("valid-project");

        Assert.True(result.IsValid, FormatIssues(result));
        var configuration = Assert.IsType<LoadedProjectConfiguration>(result.Configuration);
        Assert.Equal(1, configuration.Root.Version);
        Assert.Equal("synthetic-pension", configuration.Root.Project!.Id);
        Assert.Equal(2, configuration.Systems.Count);
        Assert.Equal(4, configuration.Systems.Sum(system => system.StorageEndpoints.Count));
        Assert.Equal(5, configuration.ReferencedFiles.Count);
        Assert.Equal("proofshift-config-canonical-v1", configuration.CanonicalizationVersion);
        Assert.Equal(64, configuration.ConfigurationHash.Length);
        Assert.All(configuration.ConfigurationHash, character => Assert.Contains(character, "0123456789abcdef"));

        var source = Assert.Single(configuration.SystemConfigurations, system => system.Id == "legacy-pension");
        var database = Assert.Single(source.StorageEndpoints, endpoint => endpoint.Id == "member-database");
        var secretSetting = Assert.IsType<SecretConfigurationSetting>(database.Settings["connection"]);
        Assert.Equal("PROOFSHIFT_TEST_SOURCE_DB", secretSetting.Reference.Name);
        Assert.Equal("secret:PROOFSHIFT_TEST_SOURCE_DB",
            configuration.Systems.Single(system => system.Id.Value == "legacy-pension")
                .StorageEndpoints.Single(endpoint => endpoint.Id.Value == "member-database")
                .Configuration["connection"]);
    }

    [Fact]
    public async Task CanonicalHashIgnoresCommentsWhitespaceAndMappingOrder()
    {
        var original = await LoadFixtureAsync("valid-project");
        var repeated = await LoadFixtureAsync("valid-project");
        var reordered = await LoadTemporaryAsync(directory =>
        {
            File.WriteAllText(Path.Combine(directory, "proofshift.yaml"), """
                # reordered, equivalent root configuration
                recovery: { policy: recovery/policy.yaml }
                verification: { rules: rules/pension.yaml }
                migration: { graph: migration/graph.yaml }
                systems:
                  target: { file: systems/target.yaml }
                  source: { file: systems/source.yaml }
                pack: { version: '0.1', id: proofshift.example }
                project: { name: Synthetic Pension Modernization, id: synthetic-pension }
                proofshift: 1
                """);
            File.WriteAllText(Path.Combine(directory, "systems", "source.yaml"), """
                # map order and formatting do not affect the fingerprint
                storage:
                  member-documents:
                    root: { env: PROOFSHIFT_TEST_SOURCE_FILES }
                    connector: files
                  member-database:
                    connection: { secret: PROOFSHIFT_TEST_SOURCE_DB }
                    connector: sqlserver
                role: source
                name: Synthetic Legacy System
                id: legacy-pension
                """);
        });

        Assert.True(original.IsValid, FormatIssues(original));
        Assert.True(repeated.IsValid, FormatIssues(repeated));
        Assert.True(reordered.IsValid, FormatIssues(reordered));
        Assert.Equal(original.Configuration!.ConfigurationHash, repeated.Configuration!.ConfigurationHash);
        Assert.Equal(original.Configuration.Systems, repeated.Configuration.Systems);
        Assert.Equal(original.Configuration!.ConfigurationHash, reordered.Configuration!.ConfigurationHash);
    }

    [Fact]
    public async Task ReferencedSemanticChangesChangeTheHash()
    {
        var original = await LoadFixtureAsync("valid-project");
        var originalHash = Assert.IsType<LoadedProjectConfiguration>(original.Configuration).ConfigurationHash;
        var changedValues = new[]
        {
            await LoadTemporaryAsync(directory => Replace(directory, "systems/source.yaml", "sqlserver", "another-connector")),
            await LoadTemporaryAsync(directory => Replace(directory, "systems/source.yaml", "member-database", "changed-database")),
            await LoadTemporaryAsync(directory => Replace(directory, "rules/pension.yaml", "critical", "high")),
            await LoadTemporaryAsync(directory => Replace(directory, "recovery/policy.yaml", "default: false", "default: true")),
            await LoadTemporaryAsync(directory =>
            {
                File.Copy(Path.Combine(directory, "systems", "source.yaml"), Path.Combine(directory, "systems", "source-alt.yaml"));
                Replace(directory, "proofshift.yaml", "systems/source.yaml", "systems/source-alt.yaml");
            })
        };

        Assert.All(changedValues, result =>
        {
            Assert.True(result.IsValid, FormatIssues(result));
            Assert.NotEqual(originalHash, result.Configuration!.ConfigurationHash);
        });
    }

    [Fact]
    public async Task InvalidReferencesVersionAndYamlReturnStableSanitizedIssues()
    {
        var fixturePath = FixturePath("invalid-project");
        var first = await new ConfigurationLoader(CreateEnvironment()).LoadAsync(fixturePath, TestContext.Current.CancellationToken);
        var second = await new ConfigurationLoader(CreateEnvironment()).LoadAsync(fixturePath, TestContext.Current.CancellationToken);

        Assert.False(first.IsValid);
        Assert.Contains(first.Issues, issue => issue.Code == ConfigurationIssueCodes.UnsupportedVersion);
        Assert.Contains(first.Issues, issue => issue.Code == ConfigurationIssueCodes.InvalidRelativePath);
        Assert.Contains(first.Issues, issue => issue.Code == ConfigurationIssueCodes.MissingReferencedFile);
        Assert.Equal(first.Issues, second.Issues);

        var malformed = await LoadTemporaryAsync(directory =>
            File.WriteAllText(Path.Combine(directory, "proofshift.yaml"), "proofshift: [synthetic-invalid-secret"));
        Assert.False(malformed.IsValid);
        Assert.Contains(malformed.Issues, issue => issue.Code == ConfigurationIssueCodes.InvalidYaml);
        Assert.DoesNotContain("synthetic-invalid-secret", FormatIssues(malformed), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DuplicateSystemIdsInvalidRolesAndMissingRequiredFieldsAreReportedTogether()
    {
        var result = await LoadTemporaryAsync(directory =>
        {
            Replace(directory, "systems/target.yaml", "modern-pension", "legacy-pension");
            Replace(directory, "systems/target.yaml", "shadow-target", "unknown-role");
            Replace(directory, "systems/source.yaml", "name: Synthetic Legacy System", "name: ");
        });

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Code == ConfigurationIssueCodes.DuplicateSystemId);
        Assert.Contains(result.Issues, issue => issue.Code == ConfigurationIssueCodes.InvalidSystemRole);
        Assert.Contains(result.Issues, issue => issue.Code == ConfigurationIssueCodes.MissingRequiredProperty);
    }

    [Fact]
    public async Task MissingRootVersionIsReportedAtItsConfigurationPath()
    {
        var result = await LoadTemporaryAsync(directory =>
            Replace(directory, "proofshift.yaml", "proofshift: 1", "unknown-root-field: value"));

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue =>
            issue.Code == ConfigurationIssueCodes.MissingRequiredProperty &&
            issue.File == "proofshift.yaml" && issue.Path == "proofshift");
    }

    [Fact]
    public async Task InlineSecretTextIsNeverIncludedInConfigurationOrDiagnostics()
    {
        const string inlineSecret = "never-show-this-inline-secret";
        var result = await LoadTemporaryAsync(directory =>
            Replace(directory, "systems/source.yaml", "connector: sqlserver",
                $"connector: sqlserver{Environment.NewLine}    password: {inlineSecret}"));
        var diagnostics = FormatIssues(result);

        Assert.False(result.IsValid);
        Assert.Null(result.Configuration);
        Assert.Contains(result.Issues, issue => issue.Code == ConfigurationIssueCodes.InlineSecretNotAllowed);
        Assert.DoesNotContain(inlineSecret, diagnostics, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DuplicateStorageEndpointIdsUseTheDedicatedDiagnostic()
    {
        var result = await LoadTemporaryAsync(directory => File.WriteAllText(
            Path.Combine(directory, "systems", "source.yaml"), """
            id: legacy-pension
            name: Legacy
            role: source
            storage:
              member-database:
                connector: sqlserver
                connection: { env: PROOFSHIFT_TEST_SOURCE_DB }
              member-database:
                connector: files
                root: { env: PROOFSHIFT_TEST_SOURCE_FILES }
            """));

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Code == ConfigurationIssueCodes.DuplicateStorageEndpointId);
    }

    [Fact]
    public async Task MissingEnvironmentVariableIsReportedWithoutValueOrSecretMaterial()
    {
        var result = await new ConfigurationLoader(new DictionaryEnvironmentVariableProvider(new Dictionary<string, string>()))
            .LoadAsync(FixturePath("valid-project"), TestContext.Current.CancellationToken);
        var diagnostics = FormatIssues(result);

        Assert.False(result.IsValid);
        Assert.Equal(4, result.Issues.Count(issue => issue.Code == ConfigurationIssueCodes.MissingEnvironmentVariable));
        Assert.DoesNotContain(RuntimeSecret, diagnostics, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive-runtime-value", diagnostics, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RuntimeSecretsResolveSeparatelyAndNeverEnterLoadedConfigurationOrHash()
    {
        var environment = CreateEnvironment();
        var result = await new ConfigurationLoader(environment)
            .LoadAsync(FixturePath("valid-project"), TestContext.Current.CancellationToken);
        var configuration = Assert.IsType<LoadedProjectConfiguration>(result.Configuration);
        var runtimeValue = new EnvironmentVariableResolver(environment)
            .Resolve(new EnvironmentVariableReference("PROOFSHIFT_TEST_SOURCE_DB"));
        var exposedModel = string.Join('\n', configuration.CanonicalConfiguration, configuration, result.Issues);

        Assert.Equal(RuntimeSecret, runtimeValue.Use(value => value));
        Assert.Equal("[REDACTED]", runtimeValue.ToString());
        Assert.DoesNotContain(RuntimeSecret, configuration.CanonicalConfiguration, StringComparison.Ordinal);
        Assert.DoesNotContain(RuntimeSecret, configuration.ConfigurationHash, StringComparison.Ordinal);
        Assert.DoesNotContain(RuntimeSecret, exposedModel, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadedConfigurationDoesNotExposeMutableCollectionInternals()
    {
        var result = await LoadFixtureAsync("valid-project");
        var configuration = Assert.IsType<LoadedProjectConfiguration>(result.Configuration);
        var system = configuration.Systems.Single(item => item.Id.Value == "legacy-pension");
        var endpoint = system.StorageEndpoints.Single(item => item.Id.Value == "member-database");
        var storedHash = configuration.ConfigurationHash;

        Assert.IsType<DomainList<SystemDefinition>>(configuration.Systems);
        Assert.IsType<DomainList<StorageEndpointDefinition>>(system.StorageEndpoints);
        Assert.IsType<DomainDictionary<string>>(endpoint.Configuration);
        Assert.Equal(storedHash, configuration.ConfigurationHash);
        Assert.Equal("secret:PROOFSHIFT_TEST_SOURCE_DB", endpoint.Configuration["connection"]);
    }

    private static Task<ConfigurationLoadResult> LoadFixtureAsync(string fixtureName) =>
        new ConfigurationLoader(CreateEnvironment())
            .LoadAsync(FixturePath(fixtureName), TestContext.Current.CancellationToken);

    private static async Task<ConfigurationLoadResult> LoadTemporaryAsync(Action<string> update)
    {
        var directory = CopyValidFixture();
        try
        {
            update(directory);
            return await new ConfigurationLoader(CreateEnvironment())
                .LoadAsync(Path.Combine(directory, "proofshift.yaml"), TestContext.Current.CancellationToken);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CopyValidFixture()
    {
        var sourceDirectory = Path.GetDirectoryName(FixturePath("valid-project"))!;
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"proofshift-config-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        foreach (var sourcePath in Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, sourcePath);
            var destinationPath = Path.Combine(temporaryDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(sourcePath, destinationPath);
        }

        return temporaryDirectory;
    }

    private static string FixturePath(string fixtureName) =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", fixtureName, "proofshift.yaml");

    private static void Replace(string directory, string relativePath, string oldValue, string newValue)
    {
        var fullPath = Path.Combine(directory, relativePath.Replace('/', Path.DirectorySeparatorChar));
        var content = File.ReadAllText(fullPath);
        Assert.Contains(oldValue, content, StringComparison.Ordinal);
        File.WriteAllText(fullPath, content.Replace(oldValue, newValue, StringComparison.Ordinal));
    }

    private static DictionaryEnvironmentVariableProvider CreateEnvironment() => new(new Dictionary<string, string>
    {
        ["PROOFSHIFT_TEST_SOURCE_DB"] = RuntimeSecret,
        ["PROOFSHIFT_TEST_SOURCE_FILES"] = "synthetic-source-files",
        ["PROOFSHIFT_TEST_TARGET_DB"] = "synthetic-target-database",
        ["PROOFSHIFT_TEST_TARGET_FILES"] = "synthetic-target-files"
    });

    private static string FormatIssues(ConfigurationLoadResult result) =>
        string.Join(Environment.NewLine, result.Issues.Select(issue => $"{issue.Code} {issue.File} {issue.Path}: {issue.Message}"));

    private sealed class DictionaryEnvironmentVariableProvider(IReadOnlyDictionary<string, string> values)
        : IEnvironmentVariableProvider
    {
        public string? GetValue(string name) => values.GetValueOrDefault(name);
    }
}
