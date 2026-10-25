namespace ProofShift.DemoScenarios;

public static class DefaultDemoScenarios
{
    public static DemoScenarioRegistry CreateRegistry() =>
        new([
            new FhirDemoScenario(),
            new HcmDemoScenario(),
            new JusticeDemoScenario(),
            new PensionDemoScenario(),
            new UtilityDemoScenario()
        ]);
}
