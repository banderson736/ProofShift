using ProofShift.DemoScenarios;
using Xunit;

namespace ProofShift.EndToEnd.Tests;

public sealed class DemoScenarioContentRootTests
{
    private static readonly string[] PensionResources =
    [
        "scenarios/pension-modernization/ps09/database/sqlserver-source.sql",
        "scenarios/pension-modernization/ps010b/proofshift.yaml",
        "scenarios/pension-modernization/ps010b/proofshift-false-reverse.yaml"
    ];

    [Fact]
    public void PensionResourcesResolveFromPackageRootWhenRunnerBaseDirectoryIsDemo()
    {
        var packageRoot = Path.Combine(Path.GetTempPath(), $"proofshift-package-root-{Guid.NewGuid():N}");
        var runnerDirectory = Path.Combine(packageRoot, "demo");
        var workspace = Path.Combine(packageRoot, "workspace");
        Directory.CreateDirectory(runnerDirectory);
        Directory.CreateDirectory(workspace);
        try
        {
            foreach (var relativePath in PensionResources)
            {
                var packageResource = DemoScenarioContentRoot.ResolvePath(packageRoot, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(packageResource)!);
                File.WriteAllText(packageResource, relativePath);
            }

            var executionContext = new DemoScenarioExecutionContext(workspace,
                Path.Combine(workspace, "integrated-assurance", "demo-summary.json"),
                packageRoot, CancellationToken.None);
            Assert.Equal(packageRoot, executionContext.ContentRootDirectory);
            Assert.False(File.Exists(Path.Combine(runnerDirectory, "ProofShift.sln")));
            Assert.All(PensionResources, relativePath =>
            {
                var resolved = DemoScenarioContentRoot.ResolvePath(executionContext.ContentRootDirectory, relativePath);
                Assert.StartsWith(packageRoot + Path.DirectorySeparatorChar, resolved,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
                Assert.False(resolved.StartsWith(runnerDirectory + Path.DirectorySeparatorChar,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
                Assert.Equal(relativePath, File.ReadAllText(resolved));
            });

            Assert.Throws<ArgumentException>(() =>
                DemoScenarioContentRoot.ResolvePath(packageRoot, "../outside.sql"));
        }
        finally
        {
            if (Directory.Exists(packageRoot)) Directory.Delete(packageRoot, recursive: true);
        }
    }

    [Fact]
    public void PensionPackageInputsExistUnderTheGenericScenarioTree()
    {
        var repositoryRoot = DemoScenarioTestPaths.FindRepositoryRoot();
        foreach (var relativePath in PensionResources)
        {
            var fullPath = DemoScenarioContentRoot.ResolvePath(repositoryRoot, relativePath);
            Assert.True(File.Exists(fullPath), $"Package scenario input is missing: {relativePath}");
        }
    }
}
