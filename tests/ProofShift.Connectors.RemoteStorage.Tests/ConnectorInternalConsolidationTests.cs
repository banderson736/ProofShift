using System.Xml.Linq;
using Xunit;

namespace ProofShift.Connectors.RemoteStorage.Tests;

public sealed class ConnectorInternalConsolidationTests
{
    [Fact]
    public void ConnectorInternalOwnershipAndSourceBoundariesRemainNarrow()
    {
        var root = FindRepositoryRoot();
        var sourceDirectory = Path.Combine(root, "src");
        var connectorProjects = Directory.GetFiles(sourceDirectory, "ProofShift.Connectors.*.csproj",
            SearchOption.AllDirectories);
        foreach (var projectPath in connectorProjects)
        {
            var project = XDocument.Load(projectPath);
            var links = project.Descendants().Where(element => element.Name.LocalName == "Compile")
                .Select(element => (string?)element.Attribute("Include"))
                .Where(include => include is not null && include.Replace('\\', '/').Split('/').Contains(".."))
                .ToArray();
            Assert.True(links.Length == 0,
                $"{Path.GetFileName(projectPath)} has cross-project production compile links: {string.Join(", ", links)}");
        }

        var allowedConsumers = new HashSet<string>(StringComparer.Ordinal)
        {
            "ProofShift.Connectors.Csv",
            "ProofShift.Connectors.Parquet",
            "ProofShift.Connectors.StructuredFiles"
        };
        foreach (var projectPath in Directory.GetFiles(sourceDirectory, "*.csproj", SearchOption.AllDirectories))
        {
            var projectName = Path.GetFileNameWithoutExtension(projectPath);
            var references = GetProjectReferences(projectPath);
            if (allowedConsumers.Contains(projectName))
                Assert.Contains("ProofShift.Connectors.Internal", references);
            else
                Assert.DoesNotContain("ProofShift.Connectors.Internal", references);

            if (projectName is "ProofShift.Domain" or "ProofShift.Graph" or "ProofShift.Engine" or
                "ProofShift.Verification" or "ProofShift.Recovery" ||
                projectName.StartsWith("ProofShift.Packs.", StringComparison.Ordinal))
                Assert.DoesNotContain("ProofShift.Connectors.Internal", references);
        }

        var internalProjectPath = Path.Combine(sourceDirectory, "ProofShift.Connectors.Internal",
            "ProofShift.Connectors.Internal.csproj");
        Assert.Equal(["ProofShift.Connectors.Abstractions"], GetProjectReferences(internalProjectPath));
        var internalProject = XDocument.Load(internalProjectPath);
        var packages = internalProject.Descendants().Where(element => element.Name.LocalName == "PackageReference")
            .Select(element => (string?)element.Attribute("Include")).ToArray();
        Assert.Equal(["Microsoft.Data.Sqlite"], packages);

        var friends = internalProject.Descendants().Where(element => element.Name.LocalName == "InternalsVisibleTo")
            .Select(element => (string?)element.Attribute("Include"))
            .Where(include => include is not null)
            .Select(include => include!)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal(allowedConsumers, friends);
    }

    private static string[] GetProjectReferences(string projectPath)
    {
        var project = XDocument.Load(projectPath);
        return project.Descendants().Where(element => element.Name.LocalName == "ProjectReference")
            .Select(element => (string?)element.Attribute("Include"))
            .Where(include => include is not null)
            .Select(include => Path.GetFileNameWithoutExtension(include!.Replace('\\', Path.DirectorySeparatorChar)))
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "ProofShift.sln")))
                return directory.FullName;

        throw new DirectoryNotFoundException("Could not locate the ProofShift repository root.");
    }
}
