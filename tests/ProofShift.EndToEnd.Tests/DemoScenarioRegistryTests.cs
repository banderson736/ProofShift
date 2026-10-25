using ProofShift.DemoScenarios;
using Xunit;

namespace ProofShift.EndToEnd.Tests;

public sealed class DemoScenarioRegistryTests
{
    [Fact]
    public void DefaultRegistryContainsAllFiveScenariosInStableOrder()
    {
        var registry = DefaultDemoScenarios.CreateRegistry();

        Assert.Equal(["fhir", "hcm", "justice", "pension", "utility"],
            registry.Scenarios.Select(scenario => scenario.Id));
    }

    [Fact]
    public void RegistryRejectsDuplicateIdsAndUnknownLookups()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new DemoScenarioRegistry([new StubScenario("same"), new StubScenario("same")]));

        Assert.Contains("Duplicate demo scenario ID", exception.Message, StringComparison.Ordinal);
        Assert.Throws<KeyNotFoundException>(() => DefaultDemoScenarios.CreateRegistry().GetRequired("missing"));
    }

    [Fact]
    public void ScenarioAssemblyHasNoTestFrameworkReferences()
    {
        var forbiddenPrefixes = new[]
        {
            "ProofShift.EndToEnd.Tests",
            "xunit",
            "Microsoft.NET.Test.Sdk",
            "Microsoft.Testing"
        };
        var references = typeof(IDemoScenario).Assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty);

        Assert.DoesNotContain(references, reference => forbiddenPrefixes.Any(prefix =>
            reference.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
    }

    private sealed class StubScenario(string id) : IDemoScenario
    {
        public string Id => id;
        public string DisplayName => id;

        public Task<DemoScenarioExecutionResult> ExecuteAsync(DemoScenarioExecutionContext context) =>
            Task.FromResult(DemoScenarioExecutionResult.Create(new { id }, id));
    }
}
