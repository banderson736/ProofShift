using System.Text.Json;

namespace ProofShift.Cli;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args is ["--version"] or ["version"])
        {
            Console.WriteLine($"ProofShift {VersionCommands.GetProductVersion()}");
            return 0;
        }
        if (args is ["version", "--json"])
        {
            Console.WriteLine(JsonSerializer.Serialize(new VersionInformation(
                VersionCommands.GetProductVersion(),
                VersionCommands.GetAssemblyMetadata("ProofShiftSourceRevision"),
                VersionCommands.GetAssemblyMetadata("ProofShiftSourceTree"),
                VersionCommands.GetAssemblyMetadata("ProofShiftTargetFramework")), CliOutput.JsonOptions));
            return 0;
        }
        if (args.Length > 0 && args[0] is "scaffold" or "mapping")
            return await MappingAuthoringCommands.RunAsync(args).ConfigureAwait(false);
        if (args.Length > 0 && args[0] is "explain" or "config")
            return await EffectiveAuthoringCommands.RunAsync(args).ConfigureAwait(false);
        if (args.Length > 0 && args[0] is "discover" or "discovery")
            return await DiscoveryCommands.RunAsync(args).ConfigureAwait(false);
        if (args.Length > 0 && args[0] == "init")
            return await ProjectInitCommand.RunAsync(args).ConfigureAwait(false);
        if (args.Length > 0 && args[0] is "packs" or "rules" or "capabilities" or "connectors")
            return RuleAuthoringCommands.Run(args);
        if (args.Length > 0 && string.Equals(args[0], "demo", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length >= 2 && string.Equals(args[1], "generate", StringComparison.OrdinalIgnoreCase))
                return await PensionDemoGenerator.GenerateAsync(args.Skip(2).ToArray()).ConfigureAwait(false);
            if (args.Length >= 2 && string.Equals(args[1], "benchmark", StringComparison.OrdinalIgnoreCase))
                return await PensionDemoGenerator.BenchmarkAsync(args.Skip(2).ToArray()).ConfigureAwait(false);
            return await DemoCommands.RunAsync(args.Skip(1).ToArray()).ConfigureAwait(false);
        }

        if (args.Length >= 2 && string.Equals(args[0], "report", StringComparison.OrdinalIgnoreCase) &&
            Guid.TryParse(args[1], out _) && args.Skip(2).All(argument => string.Equals(argument, "--json", StringComparison.OrdinalIgnoreCase)))
        {
            return await AssuranceCommands.ReadStandaloneAssuranceReportAsync(Directory.GetCurrentDirectory(), args[1],
                args.Skip(2).Any(argument => string.Equals(argument, "--json", StringComparison.OrdinalIgnoreCase))).ConfigureAwait(false);
        }

        if (args.Length >= 3 && string.Equals(args[0], "compare", StringComparison.OrdinalIgnoreCase) &&
            Guid.TryParse(args[1], out _) && Guid.TryParse(args[2], out _) &&
            args.Skip(3).All(argument => string.Equals(argument, "--json", StringComparison.OrdinalIgnoreCase)))
        {
            return await AssuranceCommands.CompareStandaloneAssuranceRunsAsync(Directory.GetCurrentDirectory(), args[1], args[2],
                args.Skip(3).Any(argument => string.Equals(argument, "--json", StringComparison.OrdinalIgnoreCase))).ConfigureAwait(false);
        }

        if (args.Length < 2)
        {
            WriteUsage();
            return 2;
        }

        var jsonOutput = false;
        var strict = false;
        string? checkpoint = null;
        string? projection = null;
        string? verificationRun = null;
        string? beforeDryRun = null;
        string? afterDryRun = null;
        for (var index = 2; index < args.Length; index++)
        {
            if (string.Equals(args[index], "--json", StringComparison.OrdinalIgnoreCase))
            {
                jsonOutput = true;
            }
            else if (args[index] == "--strict" && string.Equals(args[0], "validate", StringComparison.OrdinalIgnoreCase))
            {
                strict = true;
            }
            else if (string.Equals(args[index], "--checkpoint", StringComparison.OrdinalIgnoreCase) &&
                index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                checkpoint = args[++index];
            }
            else if (string.Equals(args[index], "--projection", StringComparison.OrdinalIgnoreCase) &&
                index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                projection = args[++index];
            }
            else if (string.Equals(args[index], "--run", StringComparison.OrdinalIgnoreCase) &&
                index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                verificationRun = args[++index];
            }
            else if (string.Equals(args[index], "--before", StringComparison.OrdinalIgnoreCase) &&
                index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                beforeDryRun = args[++index];
            }
            else if (string.Equals(args[index], "--after", StringComparison.OrdinalIgnoreCase) &&
                index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                afterDryRun = args[++index];
            }
            else
            {
                WriteUsage();
                return 2;
            }
        }

        if (string.Equals(args[0], "validate", StringComparison.OrdinalIgnoreCase))
        {
            return await ConfigurationCommands.ValidateConfigurationAsync(args[1], jsonOutput, strict).ConfigureAwait(false);
        }

        if (string.Equals(args[0], "plan", StringComparison.OrdinalIgnoreCase))
        {
            return await MigrationCommands.CompilePlanAsync(args[1], jsonOutput).ConfigureAwait(false);
        }

        if (string.Equals(args[0], "inspect", StringComparison.OrdinalIgnoreCase))
        {
            return await MigrationCommands.InspectSourcesAsync(args[1], jsonOutput).ConfigureAwait(false);
        }

        if (string.Equals(args[0], "project", StringComparison.OrdinalIgnoreCase))
        {
            return await MigrationCommands.ProjectAsync(args[1], jsonOutput, checkpoint).ConfigureAwait(false);
        }

        if (string.Equals(args[0], "snapshot", StringComparison.OrdinalIgnoreCase) && checkpoint is null)
        {
            return await MigrationCommands.SnapshotAsync(args[1], jsonOutput).ConfigureAwait(false);
        }

        if (string.Equals(args[0], "dry-run", StringComparison.OrdinalIgnoreCase) &&
            checkpoint is null && projection is null && verificationRun is null)
        {
            return await AssuranceCommands.DryRunAsync(args[1], jsonOutput).ConfigureAwait(false);
        }

        if (string.Equals(args[0], "verify", StringComparison.OrdinalIgnoreCase) && checkpoint is not null && projection is not null)
        {
            return await AssuranceCommands.VerifyAsync(args[1], checkpoint, projection, jsonOutput).ConfigureAwait(false);
        }

        if (string.Equals(args[0], "evidence", StringComparison.OrdinalIgnoreCase) && verificationRun is not null)
        {
            return await AssuranceCommands.ReadEvidenceAsync(args[1], verificationRun, jsonOutput).ConfigureAwait(false);
        }

        if (string.Equals(args[0], "recovery", StringComparison.OrdinalIgnoreCase) && verificationRun is not null)
        {
            return await AssuranceCommands.ReadRecoveryAsync(args[1], verificationRun, jsonOutput).ConfigureAwait(false);
        }

        if (string.Equals(args[0], "report", StringComparison.OrdinalIgnoreCase) && verificationRun is not null)
        {
            return await AssuranceCommands.ReadAssuranceReportAsync(args[1], verificationRun, jsonOutput).ConfigureAwait(false);
        }

        if (string.Equals(args[0], "compare", StringComparison.OrdinalIgnoreCase) &&
            beforeDryRun is not null && afterDryRun is not null)
        {
            return await AssuranceCommands.CompareAssuranceRunsAsync(args[1], beforeDryRun, afterDryRun, jsonOutput).ConfigureAwait(false);
        }

        WriteUsage();
        return 2;
    }

    private static void WriteUsage() =>
        Console.Error.WriteLine("Usage: proofshift validate <config> [--json] | proofshift plan <config> [--json] | proofshift inspect <config> [--json] | proofshift snapshot <config> [--json] | proofshift project <config> [--checkpoint <id-or-path>] [--json] | proofshift verify <config> --checkpoint <id> --projection <run-id> [--json] | proofshift evidence <config> --run <verification-run-id> [--json] | proofshift recovery <config> --run <dry-run-id> [--json] | proofshift report <config> --run <dry-run-id> [--json] | proofshift report <dry-run-id> [--json, reads current-directory stores] | proofshift compare <config> --before <dry-run-id> --after <dry-run-id> [--json] | proofshift compare <before-dry-run-id> <after-dry-run-id> [--json, reads current-directory stores] | proofshift demo list | describe <id> | preflight <id> | run <id> | reset <id> | demo generate <output-directory> [--scale fast|large] [--seed <integer>] | demo benchmark [--scale fast|large] [--seed <integer>] | proofshift dry-run <config> [--json]");

}
