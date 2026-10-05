using ProofShift.Packs.Abstractions;
using ProofShift.Packs.Pension;
using ProofShift.Verification;
using ProofShift.Configuration;

namespace ProofShift.Cli;

internal static class CliComposition
{
    internal static PackRegistry Packs() => new([new PensionPack()]);

    internal static IReadOnlyCollection<RuleDescriptor> InstalledRuleDescriptors() =>
        new GenericVerificationRuleProvider().RuleFactories.Select(factory => factory.Descriptor)
            .Concat(Packs().Installed.SelectMany(pack => pack.RuleFactories.Select(factory => factory.Descriptor)))
            .OrderBy(descriptor => descriptor.Type, StringComparer.Ordinal).ToArray();

    internal static string ProviderFor(string type) => Packs().Installed
        .FirstOrDefault(pack => pack.RuleFactories.Any(factory => factory.Type == type))?.Id ?? "proofshift.verification.generic";

    internal static VerificationRuleRegistry RulesFor(LoadedProjectConfiguration configuration,
        IReadOnlyCollection<VerificationRuleDefinition> definitions) => configuration.Root.UsesExplicitPackList || definitions.Any(definition => definition.UsesStructuredOptions)
            ? Packs().Resolve(configuration.Root.Packs)
            : new VerificationRuleRegistry(new IVerificationRuleProvider[] { new GenericVerificationRuleProvider() }
                .Concat(Packs().Installed));
}