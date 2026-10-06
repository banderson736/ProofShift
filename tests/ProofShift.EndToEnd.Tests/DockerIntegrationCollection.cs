using Xunit;

namespace ProofShift.EndToEnd.Tests;

[CollectionDefinition("DockerIntegration", DisableParallelization = true)]
public sealed class DockerIntegrationTestGroup
{
}

[CollectionDefinition("ProcessTempDirectory", DisableParallelization = true)]
public sealed class ProcessTempDirectoryTestGroup
{
}