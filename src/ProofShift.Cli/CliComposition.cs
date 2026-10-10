using ProofShift.Packs.Abstractions;
using ProofShift.Packs.Pension;
using ProofShift.Verification;
using ProofShift.Configuration;
using ProofShift.Engine;
using ProofShift.Connectors.SqlServer;
using ProofShift.Connectors.Postgres;
using ProofShift.Connectors.Csv;
using ProofShift.Connectors.Files;
using ProofShift.Connectors.Oracle;
using ProofShift.Connectors.Db2;
using ProofShift.Connectors.StructuredFiles;
using ProofShift.Connectors.RemoteObjects;
using ProofShift.Connectors.S3;
using ProofShift.Connectors.AzureBlob;
using ProofShift.Connectors.Sftp;
using ProofShift.Connectors.Parquet;

namespace ProofShift.Cli;

internal static class CliComposition
{
    internal static ConnectorCatalog Connectors() => new(
        [new SqlServerSourceConnector(), new PostgresSourceConnector(), new CsvSourceConnector(), new FilesystemSourceConnector(),
            new OracleSourceConnector(), new Db2SourceConnector(), new StructuredFileConnector("fixed-width"), new StructuredFileConnector("json"),
            new StructuredFileConnector("ndjson"), new StructuredFileConnector("xml"),
            new S3SourceConnector(), new AzureBlobSourceConnector(), new SftpSourceConnector(),
            new ParquetSourceConnector([new LocalFileStoreFactory(), new S3StoreFactory(), new AzureBlobStoreFactory(), new SftpStoreFactory()])],
        [new PostgresShadowTargetConnector(), new FilesystemShadowTargetConnector()]);

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