namespace ProofShift.EndToEnd.Tests;

internal static class DemoScenarioTestPaths
{
    public static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null;
             directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "ProofShift.sln")))
                return directory.FullName;
        throw new DirectoryNotFoundException("ProofShift repository root could not be located from the test output directory.");
    }
}
