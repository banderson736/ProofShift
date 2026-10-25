using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;
using System.Data.Common;
using ProofShift.Configuration;
using ProofShift.Connectors.Abstractions;
using ProofShift.Connectors.Csv;
using ProofShift.Connectors.Files;
using ProofShift.Connectors.Postgres;
using ProofShift.Connectors.SqlServer;
using ProofShift.Engine;
using ProofShift.Evidence;
using ProofShift.Graph;
using ProofShift.Packs.Pension;
using ProofShift.Projection;
using ProofShift.Recovery;
using ProofShift.Snapshots;
using ProofShift.Domain;
using ProofShift.Packs.Abstractions;
using ProofShift.Reporting;
using ProofShift.Verification;

namespace ProofShift.Cli;

internal static class ConfigurationCommands
{
    internal static async Task<int> ValidateConfigurationAsync(string path, bool jsonOutput, bool strict = false)
    {
        ConfigurationLoadResult result;
        try
        {
            result = await new ConfigurationLoader(requireEnvironmentValues: false).LoadAsync(path).ConfigureAwait(false);
            if (result.Configuration is { } candidate && !candidate.Root.UsesExplicitPackList &&
                !(candidate.ReferencedFiles.FirstOrDefault(file => file.Kind == ConfigurationFileKind.VerificationRules)?.Document is
                    ConfigurationMappingNode ruleDocument && ruleDocument.Values.TryGetValue("version", out var ruleVersion) &&
                    ruleVersion is ConfigurationScalarNode { Value: "2" }))
                result = await new ConfigurationLoader().LoadAsync(path).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return 130;
        }
        catch
        {
            Console.Error.WriteLine("Configuration validation could not be completed.");
            return 1;
        }

        if (result.Configuration is { } configuration)
        {
            try
            {
                var definitions = VerificationRuleConfigurationLoader.Load(configuration);
                if (strict || configuration.Root.UsesExplicitPackList || definitions.Any(definition => definition.UsesStructuredOptions))
                {
                    var registry = CliComposition.RulesFor(configuration, definitions);
                    _ = registry.Resolve(definitions);
                    var graph = MigrationGraphCompiler.Compile(configuration);
                    if (graph.Graph is { } compiled)
                    {
                        if (strict)
                            result = new ConfigurationLoadResult(configuration, result.Issues.Concat(
                                ConnectorConfigurationValidator.Validate(configuration, compiled, CliComposition.Connectors())));
                        result = new ConfigurationLoadResult(configuration, result.Issues.Concat(
                            RuleReferenceValidator.Validate(definitions, registry, compiled)));
                        _ = RecoveryPolicyConfigurationLoader.Load(configuration);
                        if (!strict)
                            result = new ConfigurationLoadResult(configuration, result.Issues.Concat(
                                StrictAuthoringValidation.UnapprovedMappings(configuration).Select(issue => new ConfigurationValidationIssue(
                                    issue.Code, ValidationSeverity.Warning, issue.Message, issue.File, issue.Path, issue.Line, issue.Column))));
                        if (strict)
                        {
                            var reverseIssues = compiled.Edges.Where(edge => edge.Recovery is { Mode: RecoveryMode.Reverse } &&
                                    !TransformationLossAnalyzer.Analyze(edge).IsReversible)
                                .Select(edge => new ConfigurationValidationIssue("PSAUTHOR003", ValidationSeverity.Error,
                                    $"Mapping '{edge.Name}' declares Reverse for a transformation without a proven inverse.",
                                    configuration.Root.MigrationGraphFile, $"edges.{edge.Name}.recovery"));
                            result = new ConfigurationLoadResult(configuration, result.Issues.Concat(reverseIssues)
                                .Concat(StrictAuthoringValidation.UnapprovedMappings(configuration)));
                        }
                    }
                    else
                        result = new ConfigurationLoadResult(configuration, result.Issues.Concat(graph.Issues.Select(issue =>
                            new ConfigurationValidationIssue(issue.Code, ValidationSeverity.Error, issue.Message, issue.File, issue.Path))));
                }
            }
            catch (VerificationRuleException exception)
            {
                result = new ConfigurationLoadResult(configuration, result.Issues.Append(new ConfigurationValidationIssue(
                    exception.Code, ValidationSeverity.Error, exception.Message,
                    exception.Location?.File ?? configuration.ReferencedFiles.FirstOrDefault(file => file.Kind == ConfigurationFileKind.VerificationRules)?.RelativePath,
                    exception.Location?.Path, exception.Location?.Line, exception.Location?.Column)));
            }
                    catch (RecoveryException exception)
                    {
                    result = new ConfigurationLoadResult(configuration, result.Issues.Append(new ConfigurationValidationIssue(
                        exception.Code, ValidationSeverity.Error, exception.Message, configuration.Root.RecoveryPolicyFile, "recovery")));
                    }
        }

        if (jsonOutput)
        {
            WriteJson(result);
        }
        else
        {
            WriteHumanReadable(result);
        }

        return result.IsValid ? 0 : 1;
    }


    private static void WriteJson(ConfigurationLoadResult result)
    {
        var issues = result.Issues.Select(issue => new ValidationIssueOutput(
            issue.Code,
            issue.Severity.ToString().ToLowerInvariant(),
            issue.File,
            issue.Path,
            issue.Message,
            issue.Line,
            issue.Column));
        var output = new ValidationOutput(
            result.IsValid,
            result.ConfigurationVersion,
            result.Configuration?.CanonicalizationVersion,
            result.Configuration?.ConfigurationHash,
            issues);
        Console.WriteLine(JsonSerializer.Serialize(output, CliOutput.JsonOptions));
    }


    private static void WriteHumanReadable(ConfigurationLoadResult result)
    {
        Console.WriteLine("ProofShift Configuration Validation");
        Console.WriteLine();

        if (result.Configuration is { } configuration)
        {
            Console.WriteLine($"Project: {configuration.Root.Project?.Name}");
            Console.WriteLine();
            Console.WriteLine($"Configuration Version: {configuration.Root.Version}");
            Console.WriteLine($"Canonicalization Version: {configuration.CanonicalizationVersion}");
            Console.WriteLine();
            Console.WriteLine($"Systems: {configuration.Systems.Count}");
            Console.WriteLine($"Storage Endpoints: {configuration.Systems.Sum(system => system.StorageEndpoints.Count)}");
            Console.WriteLine($"Migration Graph: {configuration.Root.MigrationGraphFile}");
            Console.WriteLine($"Verification Rules: {configuration.Root.VerificationRulesFile}");
            Console.WriteLine($"Recovery Policy: {configuration.Root.RecoveryPolicyFile}");
            Console.WriteLine();
            Console.WriteLine($"Configuration Hash: {configuration.ConfigurationHash}");
            Console.WriteLine();
        }
        else if (result.ConfigurationVersion is not null)
        {
            Console.WriteLine($"Configuration Version: {result.ConfigurationVersion}");
            Console.WriteLine();
        }

        foreach (var issue in result.Issues)
        {
            var severity = issue.Severity.ToString().ToUpperInvariant();
            Console.WriteLine($"{severity} {issue.Code}");
            if (issue.File is not null)
            {
                Console.WriteLine(issue.Line is { } line ? $"{issue.File}:{line}:{issue.Column ?? 1}" : issue.File);
            }

            if (issue.Path is not null)
            {
                Console.WriteLine($"Path: {issue.Path}");
            }

            Console.WriteLine(issue.Message);
            Console.WriteLine();
        }

        Console.WriteLine("RESULT:");
        Console.WriteLine(result.IsValid ? "VALID" : "INVALID");
    }


}
