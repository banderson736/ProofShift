using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ProofShift.Connectors.Abstractions;
using ProofShift.Projection;
using ProofShift.Recovery;
using ProofShift.Snapshots;
using ProofShift.Verification;

namespace ProofShift.Cli;

internal static class WorkspaceCommands
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
    private static readonly JsonSerializerOptions JsonLineOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length < 2)
        {
            WriteUsage();
            return 2;
        }

        return args[1] switch
        {
            "init" => await InitializeAsync(args).ConfigureAwait(false),
            "validate" => await ValidateAsync(args).ConfigureAwait(false),
            "preflight" => await PreflightAsync(args).ConfigureAwait(false),
            "plan" => await PlanAsync(args).ConfigureAwait(false),
            "review-plan" => await ReviewPlanAsync(args).ConfigureAwait(false),
            "run" => await RunEvaluationAsync(args).ConfigureAwait(false),
            "evidence" => await EvidenceAsync(args).ConfigureAwait(false),
            "baseline" => await BaselineAsync(args).ConfigureAwait(false),
            "summary" => await SummaryAsync(args).ConfigureAwait(false),
            "compare" => await CompareAsync(args).ConfigureAwait(false),
            "cleanup" => await CleanupAsync(args).ConfigureAwait(false),
            "runs" => await RunsAsync(args).ConfigureAwait(false),
            _ => WriteUsageAndReturn()
        };
    }

    private static async Task<int> BaselineAsync(string[] args)
    {
        if (args.Length < 4)
        {
            WriteUsage();
            return 2;
        }

        var operation = args[2];
        var minimumLength = operation is "promote" or "show" ? 5 : 4;
        var maximumLength = minimumLength + 1;
        if (args.Length < minimumLength || args.Length > maximumLength ||
            (args.Length == maximumLength && args[^1] != "--json"))
        {
            WriteUsage();
            return 2;
        }

        try
        {
            switch (operation)
            {
                case "promote":
                {
                    var promotion = await WorkspaceBaselineService.PromoteAsync(args[3], args[4],
                        CancellationToken.None).ConfigureAwait(false);
                    WriteBaseline(promotion.Baseline, args.Length == maximumLength,
                        promotion.Created ? "Baseline promoted" : "Baseline already active",
                        promotion.Created ? "Promoted" : "AlreadyActive");
                    return 0;
                }
                case "list":
                {
                    var baselines = await WorkspaceBaselineService.ListAsync(args[3], CancellationToken.None)
                        .ConfigureAwait(false);
                    Guid? activeBaselineId = baselines.Count == 0
                        ? null
                        : baselines[baselines.Count - 1].BaselineId;
                    if (args.Length == maximumLength)
                    {
                        var history = new JsonArray();
                        foreach (var baseline in baselines)
                        {
                            var item = JsonSerializer.SerializeToNode(baseline, JsonOptions)?.AsObject()
                                ?? throw new InvalidDataException("Baseline history record could not be serialized.");
                            item["isActive"] = baseline.BaselineId == activeBaselineId;
                            history.Add(item);
                        }
                        Console.WriteLine(history.ToJsonString(JsonOptions));
                    }
                    else
                    {
                        Console.WriteLine("ProofShift Evaluation Baseline History");
                        if (baselines.Count == 0)
                            Console.WriteLine("No baselines have been promoted.");
                        foreach (var baseline in baselines)
                            Console.WriteLine($"{baseline.BaselineId:D} run={baseline.RunId:D} disposition={baseline.Disposition} " +
                                $"promoted={baseline.PromotedAt:O} previous={baseline.PreviousActiveBaselineId?.ToString("D") ?? "none"} " +
                                $"comparison={baseline.PromotionComparisonOutcomeClassification ?? "not recorded"} " +
                                $"status={(baseline.BaselineId == activeBaselineId ? "active" : "historical")}");
                    }
                    return 0;
                }
                case "show":
                {
                    var baseline = await WorkspaceBaselineService.ShowAsync(args[3], args[4],
                        CancellationToken.None).ConfigureAwait(false);
                    WriteBaseline(baseline, args.Length == maximumLength, "Baseline");
                    return 0;
                }
                case "active":
                {
                    var baseline = await WorkspaceBaselineService.ActiveAsync(args[3], CancellationToken.None)
                        .ConfigureAwait(false);
                    if (args.Length == maximumLength)
                        Console.WriteLine(JsonSerializer.Serialize(baseline, JsonOptions));
                    else if (baseline is null)
                        Console.WriteLine("No active baseline is selected.");
                    else
                        WriteBaseline(baseline, jsonOutput: false, "Active baseline");
                    return 0;
                }
                default:
                    WriteUsage();
                    return 2;
            }
        }
        catch (WorkspaceBaselineAlreadyPromotedException exception)
        {
            Console.Error.WriteLine($"PSWORKBASE098: {exception.Message}");
            return 1;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            ArgumentException or InvalidOperationException or InvalidDataException or JsonException)
        {
            Console.Error.WriteLine($"PSWORKBASE099: Baseline operation failed ({exception.GetType().Name}).");
            return 1;
        }
    }

    private static void WriteBaseline(WorkspaceBaselineRecord baseline, bool jsonOutput, string heading,
        string? promotionStatus = null)
    {
        if (jsonOutput)
        {
            var value = JsonSerializer.SerializeToNode(baseline, JsonOptions)?.AsObject()
                ?? throw new InvalidDataException("Baseline record could not be serialized.");
            if (promotionStatus is not null)
                value["promotionStatus"] = promotionStatus;
            Console.WriteLine(value.ToJsonString(JsonOptions));
            return;
        }
        Console.WriteLine($"ProofShift {heading}");
        Console.WriteLine($"Baseline: {baseline.BaselineId:D}");
        Console.WriteLine($"Run: {baseline.RunId:D}");
        Console.WriteLine($"Disposition: {baseline.Disposition}");
        Console.WriteLine($"Promoted: {baseline.PromotedAt:O}");
        Console.WriteLine($"Plan fingerprint: {baseline.EffectivePlanFingerprint}");
        Console.WriteLine($"Evidence package identity: {baseline.EvidencePackageIdentity}");
        Console.WriteLine($"Previous active baseline: {baseline.PreviousActiveBaselineId?.ToString("D") ?? "none"}");
        if (baseline.PromotionComparisonBaselineId is { } comparisonBaseline)
            Console.WriteLine($"Outcome before promotion: {baseline.PromotionComparisonOutcomeClassification} vs {comparisonBaseline:D}");
        Console.WriteLine("Promotion records the run outcome; it does not change or endorse that disposition.");
    }

    private static async Task<int> RunsAsync(string[] args)
    {
        if (args.Length is not (3 or 4) ||
            args.Length == 4 && !string.Equals(args[3], "--json", StringComparison.OrdinalIgnoreCase))
        {
            WriteUsage();
            return 2;
        }

        try
        {
            var runs = await WorkspaceRunHistoryService.ListAsync(args[2], CancellationToken.None)
                .ConfigureAwait(false);
            if (args.Length == 4)
            {
                Console.WriteLine(JsonSerializer.Serialize(runs, JsonOptions));
                return 0;
            }

            Console.WriteLine("ProofShift Workspace Run History");
            if (runs.Count == 0)
                Console.WriteLine("No workspace runs were found.");
            foreach (var run in runs)
            {
                Console.WriteLine($"{run.DirectoryName} trust={run.ValidationStatus} state={run.State ?? "unknown"} " +
                    $"disposition={run.Disposition ?? "not established"} baseline={run.BaselineId?.ToString("D") ?? "none"} " +
                    $"replay={(run.ReplayedCheckpoint is null ? "unknown" : run.ReplayedCheckpoint.Value ? "replay" : "fresh")} " +
                    $"outcome={run.OutcomeClassification ?? run.ComparisonStatus} package={run.PackageStatus}");
                if (run.ValidationIssue is not null)
                    Console.WriteLine($"  Issue: {run.ValidationIssue}");
            }
            return 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            ArgumentException or InvalidOperationException or InvalidDataException or JsonException)
        {
            Console.Error.WriteLine($"PSWORKHISTORY099: Run history could not be listed ({exception.GetType().Name}).");
            return 1;
        }
    }

    private static async Task<int> ReviewPlanAsync(string[] args)
    {
        var baselineReference = "active";
        var jsonOutput = false;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 3; index < args.Length; index++)
        {
            if (args[index] == "--json" && seen.Add(args[index]))
                jsonOutput = true;
            else if (args[index] == "--baseline" && index + 1 < args.Length &&
                     !args[index + 1].StartsWith("--", StringComparison.Ordinal) && seen.Add(args[index]))
                baselineReference = args[++index];
            else
            {
                WriteUsage();
                return 2;
            }
        }
        if (args.Length < 3 || baselineReference != "active" && baselineReference != "none" &&
            (!Guid.TryParse(baselineReference, out var baselineId) || baselineId == Guid.Empty))
        {
            WriteUsage();
            return 2;
        }

        try
        {
            var result = await WorkspacePlanReviewService.ReviewAsync(args[2], baselineReference,
                CancellationToken.None).ConfigureAwait(false);
            if (jsonOutput)
                Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
            else
            {
                Console.WriteLine("ProofShift Exact Plan Review");
                Console.WriteLine($"Review: {result.Review.ReviewId:D}");
                Console.WriteLine($"Baseline: {result.Review.BaselineId:D}");
                Console.WriteLine($"Baseline plan: {result.Review.BaselinePlanFingerprint}");
                Console.WriteLine($"Current plan: {result.Review.CurrentPlanFingerprint}");
                Console.WriteLine($"Comparison: {result.Drift.Status}");
                Console.WriteLine($"Decision: {result.Review.Decision}");
                foreach (var change in result.Drift.Changes)
                    Console.WriteLine($"{change.Classification}: {change.Category}/{change.Kind} {change.Subject} - {change.Detail}");
                Console.WriteLine("This record acknowledges review of these exact evaluation semantics only; it is not production or migration approval.");
            }
            return 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or
            InvalidOperationException or InvalidDataException or System.Text.Json.JsonException)
        {
            Console.Error.WriteLine($"PSWORKREV099: Plan review could not be recorded ({exception.GetType().Name}); no configuration values were included.");
            return 1;
        }
    }

    private static async Task<int> SummaryAsync(string[] args)
    {
        if (args.Length < 4)
        {
            WriteUsage();
            return 2;
        }
        string? outputPath = null;
        var jsonOutput = false;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 4; index < args.Length; index++)
        {
            if (args[index] == "--output" && index + 1 < args.Length &&
                !args[index + 1].StartsWith("--", StringComparison.Ordinal) && seen.Add(args[index]))
                outputPath = args[++index];
            else if (args[index] == "--json" && seen.Add(args[index]))
                jsonOutput = true;
            else
            {
                WriteUsage();
                return 2;
            }
        }

        try
        {
            var result = await WorkspaceEvaluationSummaryService.CreateAsync(args[2], args[3], outputPath,
                CancellationToken.None).ConfigureAwait(false);
            if (jsonOutput) Console.WriteLine(JsonSerializer.Serialize(result, JsonLineOptions));
            else
            {
                Console.WriteLine("ProofShift Evaluation Summary");
                Console.WriteLine($"Run: {result.RunId:D}");
                Console.WriteLine($"Execution state: {result.State}");
                Console.WriteLine($"Disposition: {result.Disposition ?? "not established"}");
                Console.WriteLine($"Failed findings: {result.FailedFindingCount}");
                Console.WriteLine($"Report identity: {result.ReportIdentity}");
                if (result.BaselineId is { } baselineId)
                {
                    Console.WriteLine($"Baseline: {baselineId:D} ({result.BaselineDisposition ?? "disposition unavailable"})");
                    Console.WriteLine($"Pre-run semantic drift: {result.PreRunSemanticDriftStatus ?? "unavailable"}");
                    Console.WriteLine($"Review: {result.ReviewDecision ?? "NotRequired"}");
                    Console.WriteLine($"Post-run outcome: {result.OutcomeClassification ?? "NotEstablished"}");
                    Console.WriteLine($"New/resolved findings: {result.NewFindings.Count}/{result.ResolvedFindings.Count}");
                    Console.WriteLine($"Reconciliation failures added/resolved: {result.ReconciliationFailuresAdded.Count}/{result.ReconciliationFailuresResolved.Count}");
                    Console.WriteLine($"Materialized-state finding added/resolved: {result.MaterializedStateFindingAdded}/{result.MaterializedStateFindingResolved}");
                }
                Console.WriteLine($"Summary: {result.SummaryPath}");
            }
            return 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or
            InvalidOperationException or InvalidDataException or System.Text.Json.JsonException)
        {
            Console.Error.WriteLine($"PSWORKSUM099: Evaluation summary generation failed ({exception.GetType().Name}).");
            return 1;
        }
    }

    private static async Task<int> CompareAsync(string[] args)
    {
        if (args.Length is not (5 or 6) || (args.Length == 6 && args[5] != "--json"))
        {
            WriteUsage();
            return 2;
        }
        try
        {
            var comparison = await WorkspaceRunComparisonService.CompareAsync(args[2], args[3], args[4],
                CancellationToken.None).ConfigureAwait(false);
            if (args.Length == 6)
            {
                Console.WriteLine(JsonSerializer.Serialize(comparison, JsonOptions));
            }
            else
            {
                Console.WriteLine("ProofShift Workspace Run Comparison");
                Console.WriteLine($"Before: {comparison.BeforeRunId:D} ({comparison.BeforeDisposition ?? "no disposition"})");
                Console.WriteLine($"After:  {comparison.AfterRunId:D} ({comparison.AfterDisposition ?? "no disposition"})");
                Console.WriteLine($"Disposition changed: {(comparison.DispositionChanged ? "yes" : "no")}");
                Console.WriteLine($"Semantically equivalent: {(comparison.SemanticallyEquivalent ? "yes" : "no")}");
                Console.WriteLine($"Execution mode changed: {comparison.ExecutionModeChanged}; configuration/plan changed: {comparison.ConfigurationChanged || comparison.PlanChanged}");
                Console.WriteLine($"Checkpoint identity/source changed: {comparison.CheckpointIdentityChanged}/{comparison.SourcePopulationChanged}; projection changed: {comparison.ProjectionChanged}");
                Console.WriteLine($"Materialized-state finding added/resolved: {comparison.MaterializedStateFindingAdded}/{comparison.MaterializedStateFindingResolved}");
                foreach (var finding in comparison.Findings)
                    Console.WriteLine($"{finding.Category}: {finding.RuleId} ({finding.BeforeCode ?? "none"} -> {finding.AfterCode ?? "none"}), target {finding.TargetIdentity}, count {finding.Count}");
                foreach (var delta in comparison.ReconciliationFailuresAdded)
                    Console.WriteLine($"Reconciliation failure added: {delta}");
                foreach (var delta in comparison.ReconciliationFailuresResolved)
                    Console.WriteLine($"Reconciliation failure resolved: {delta}");
                foreach (var exception in comparison.ExpectedExceptions)
                    Console.WriteLine($"Expected exception {exception.Category}: {exception.AnnotationIdentity} (annotation does not waive findings)");
                Console.WriteLine("Matching dispositions do not establish run equivalence.");
            }
            return 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or
            InvalidOperationException or InvalidDataException or System.Text.Json.JsonException)
        {
            Console.Error.WriteLine($"PSWORKCMP099: Workspace run comparison failed ({exception.GetType().Name}).");
            return 1;
        }
    }

    private static async Task<int> CleanupAsync(string[] args)
    {
        if (args.Length < 4)
        {
            WriteUsage();
            return 2;
        }
        var execute = false;
        var planRequested = false;
        var jsonOutput = false;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 4; index < args.Length; index++)
        {
            if (args[index] == "--execute" && seen.Add(args[index]))
                execute = true;
            else if (args[index] == "--plan" && seen.Add(args[index]))
                planRequested = true;
            else if (args[index] == "--json" && seen.Add(args[index]))
                jsonOutput = true;
            else
            {
                WriteUsage();
                return 2;
            }
        }
        if (execute && planRequested)
        {
            WriteUsage();
            return 2;
        }
        try
        {
            var result = await WorkspaceCleanupService.RunAsync(args[2], args[3], execute,
                CancellationToken.None).ConfigureAwait(false);
            if (jsonOutput) Console.WriteLine(JsonSerializer.Serialize(result, JsonLineOptions));
            else
            {
                Console.WriteLine("ProofShift Workspace Cleanup");
                Console.WriteLine($"Run: {result.RunId:D}");
                Console.WriteLine($"Mode: {(result.Executed ? "executed" : "preview only")}");
                foreach (var resource in result.Resources)
                    Console.WriteLine($"{resource.Status}: {resource.Type} {resource.Path} - {resource.Action}; {resource.Reason}");
                Console.WriteLine("Preserved:");
                foreach (var item in result.Preserved) Console.WriteLine($"  {item}");
                Console.WriteLine("Configured target systems and customer source data are never cleanup targets.");
            }
            return result.SafeToExecute ? 0 : 1;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or
            InvalidOperationException or InvalidDataException or System.Text.Json.JsonException)
        {
            Console.Error.WriteLine($"PSWORKCLN099: Workspace cleanup was refused ({exception.GetType().Name}).");
            return 1;
        }
    }

    private static async Task<int> EvidenceAsync(string[] args)
    {
        if (args.Length < 3)
        {
            WriteUsage();
            return 2;
        }

        if (args[2] == "compare-packages")
        {
            if (args.Length is not (5 or 6) || (args.Length == 6 && args[5] != "--json"))
            {
                WriteUsage();
                return 2;
            }

            try
            {
                var result = await WorkspacePackageComparisonService.CompareAsync(args[3], args[4],
                    CancellationToken.None).ConfigureAwait(false);
                if (args.Length == 6)
                {
                    Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
                }
                else
                {
                    Console.WriteLine("ProofShift Offline Evidence Package Comparison");
                    Console.WriteLine($"Package integrity: VALID (both packages)");
                    Console.WriteLine($"Comparison: {result.ComparisonMode}");
                    Console.WriteLine($"Baseline run/package: {result.BaselineRunId:D} / {result.BaselinePackageIdentity}");
                    Console.WriteLine($"Candidate run/package: {result.CandidateRunId:D} / {result.CandidatePackageIdentity}");
                    Console.WriteLine($"Governance binding: {(result.BaselineGovernanceBindingValid == true ? "VALID" : "not asserted (ad hoc comparison)")}");
                    Console.WriteLine($"Disposition: {result.BaselineDisposition ?? "not established"} -> {result.CandidateDisposition ?? "not established"}");
                    Console.WriteLine($"Pre-run semantic drift: {result.SemanticDriftStatus ?? "not recorded"}; review: {result.ReviewDecision ?? "not applicable"}");
                    Console.WriteLine($"Post-run outcome: {result.OutcomeClassification ?? "not classified (ad hoc comparison)"}");
                    Console.WriteLine($"Replay ancestry changed: {result.ReplayAncestryChanged}");
                    foreach (var finding in result.DetailedComparison.Findings.Where(item => item.Category != "Persisting"))
                        Console.WriteLine($"Finding {finding.Category}: {finding.RuleId}; target {finding.TargetIdentity}; count {finding.Count}");
                    foreach (var annotation in result.DetailedComparison.ExpectedExceptions.Where(item => item.Category != "Persisting"))
                        Console.WriteLine($"Expected-exception annotation {annotation.Category}: count {annotation.Count}; annotations never waive findings.");
                    foreach (var rule in result.DetailedComparison.ReconciliationFailuresAdded)
                        Console.WriteLine($"Reconciliation failure added: {rule}");
                    foreach (var rule in result.DetailedComparison.ReconciliationFailuresResolved)
                        Console.WriteLine($"Reconciliation failure resolved: {rule}");
                    Console.WriteLine($"Materialized-state finding added/resolved: {result.MaterializedStateFindingAdded}/{result.MaterializedStateFindingResolved}");
                    Console.WriteLine("Offline comparison uses embedded package data only; it does not connect to a workspace or customer system.");
                }
                return 0;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                InvalidDataException or ArgumentException or InvalidOperationException or
                System.Text.Json.JsonException or FormatException or KeyNotFoundException)
            {
                Console.Error.WriteLine($"PSWORKPKGCMP099: Offline package comparison failed closed ({exception.GetType().Name}).");
                return 1;
            }
        }

        if (args[2] == "package")
        {
            if (args.Length < 5)
            {
                WriteUsage();
                return 2;
            }

            string? outputPath = null;
            var diagnostic = false;
            var jsonOutput = false;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 5; index < args.Length; index++)
            {
                if (args[index] == "--output" && index + 1 < args.Length &&
                    !args[index + 1].StartsWith("--", StringComparison.Ordinal) && seen.Add(args[index]))
                {
                    outputPath = args[++index];
                }
                else if (args[index] == "--diagnostic" && seen.Add(args[index]))
                {
                    diagnostic = true;
                }
                else if (args[index] == "--json" && seen.Add(args[index]))
                {
                    jsonOutput = true;
                }
                else
                {
                    WriteUsage();
                    return 2;
                }
            }

            try
            {
                var result = await WorkspaceEvidencePackageService.CreateAsync(args[3], args[4], outputPath,
                    diagnostic, CancellationToken.None).ConfigureAwait(false);
                if (jsonOutput)
                {
                    Console.WriteLine(JsonSerializer.Serialize(new
                    {
                        status = "CREATED",
                        runId = result.RunId,
                        packagePath = result.PackagePath,
                        packageIdentity = result.PackageIdentity,
                        archiveSha256 = result.ArchiveSha256,
                        packageType = result.PackageType,
                        executionState = result.ExecutionState,
                        disposition = result.Disposition,
                        embeddedArtifactCount = result.EmbeddedArtifactCount,
                        externalReferenceCount = result.ExternalReferenceCount,
                        omittedArtifactCount = result.OmittedArtifactCount,
                        validationCommand = $"proofshift workspace evidence validate-package \"{result.PackagePath}\""
                    }, JsonLineOptions));
                }
                else
                {
                    Console.WriteLine("ProofShift Evidence Package");
                    Console.WriteLine($"Run: {result.RunId:D}");
                    Console.WriteLine($"Package: {result.PackagePath}");
                    Console.WriteLine($"Logical package identity: {result.PackageIdentity}");
                    Console.WriteLine($"Archive SHA-256: {result.ArchiveSha256}");
                    Console.WriteLine($"Package type: {result.PackageType}; execution state: {result.ExecutionState}");
                    Console.WriteLine($"Disposition: {result.Disposition ?? "not established"}");
                    Console.WriteLine($"Artifacts: {result.EmbeddedArtifactCount} embedded, {result.ExternalReferenceCount} external references, {result.OmittedArtifactCount} intentionally omitted");
                    Console.WriteLine($"Validate offline: proofshift workspace evidence validate-package \"{result.PackagePath}\"");
                    Console.WriteLine("Evidence may contain customer-sensitive values; review package contents before sharing.");
                }
                return 0;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or
                InvalidOperationException or InvalidDataException or ProjectionManifestException or KeyNotFoundException or
                FormatException or System.Text.Json.JsonException)
            {
                var diagnosticCode = exception.Data[WorkspaceEvidencePackageService.DiagnosticCodeDataKey] as string
                    ?? "PSWORKPKG099";
                Console.Error.WriteLine($"{diagnosticCode}: Evidence package creation failed ({exception.GetType().Name}); no configuration values were included.");
                return 1;
            }
        }

        if (args[2] == "validate-package")
        {
            if (args.Length is not (4 or 5) || (args.Length == 5 && args[4] != "--json"))
            {
                WriteUsage();
                return 2;
            }

            var result = await WorkspaceEvidencePackageService.ValidateAsync(args[3], CancellationToken.None)
                .ConfigureAwait(false);
            if (args.Length == 5)
            {
                Console.WriteLine(JsonSerializer.Serialize(result, JsonLineOptions));
            }
            else
            {
                Console.WriteLine("ProofShift Evidence Package Validation");
                Console.WriteLine($"Package integrity: {(result.Valid ? "VALID" : "INVALID")}");
                Console.WriteLine($"Run: {result.RunId?.ToString("D") ?? "unavailable"}");
                Console.WriteLine($"Package identity: {result.PackageIdentity ?? "unavailable"}");
                Console.WriteLine($"Package type: {result.PackageType ?? "unavailable"}");
                Console.WriteLine($"Execution state: {result.ExecutionState ?? "unavailable"}");
                Console.WriteLine($"Evaluation disposition: {result.Disposition ?? "not established"}");
                Console.WriteLine($"Artifacts: {result.EmbeddedArtifactCount} embedded, {result.ExternalReferenceCount} external references, {result.OmittedArtifactCount} intentionally omitted");
                foreach (var failure in result.Failures) Console.WriteLine($"FAIL: {failure}");
                Console.WriteLine("Validation is offline and does not rerun evaluation or check external reference availability.");
            }
            return result.Valid ? 0 : 1;
        }

        WriteUsage();
        return 2;
    }

    private static async Task<int> InitializeAsync(string[] args)
    {
        if (args.Length is not (3 or 5) ||
            args.Length == 5 && !string.Equals(args[3], "--template", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(args[3], "--from", StringComparison.OrdinalIgnoreCase))
        {
            WriteUsage();
            return 2;
        }

        var sourceWorkspace = args.Length == 5 &&
            string.Equals(args[3], "--from", StringComparison.OrdinalIgnoreCase) ? args[4] : null;
        var template = args.Length == 5 &&
            string.Equals(args[3], "--template", StringComparison.OrdinalIgnoreCase) ? args[4] : "generic";
        if (sourceWorkspace is null && template is not ("generic" or "pension"))
        {
            Console.Error.WriteLine("PSWORK015: Template must be generic or pension.");
            return 1;
        }

        try
        {
            var directory = Path.GetFullPath(args[2]);
            if (sourceWorkspace is not null)
            {
                var workspaceId = await WorkspaceDerivationService.InitializeFromAsync(sourceWorkspace, directory,
                    CancellationToken.None).ConfigureAwait(false);
                Console.WriteLine($"Initialized derived evaluation workspace: {directory}");
                Console.WriteLine($"Source workspace: {Path.GetFullPath(sourceWorkspace)}");
                Console.WriteLine($"Workspace ID: {workspaceId:D}");
                Console.WriteLine("Configuration references were reused; run history, baselines, reviews, Evidence, discovery, and customer data were not copied.");
                return 0;
            }

            var projectResult = await ProjectInitCommand.InitializeAsync(directory, template, announce: false).ConfigureAwait(false);
            if (projectResult != 0) return projectResult;

            var manifest = new EvaluationWorkspaceManifest
            {
                FormatVersion = EvaluationWorkspaceManifest.CurrentFormatVersion,
                WorkspaceId = Guid.NewGuid(),
                ProjectConfiguration = "proofshift.yaml",
                Paths = EvaluationWorkspacePaths.Default
            };
            foreach (var entry in manifest.Paths.Entries)
                Directory.CreateDirectory(Path.Combine(directory, entry.Path.Replace('/', Path.DirectorySeparatorChar)));
            await EvaluationWorkspaceManifestStore.WriteNewAsync(directory, manifest).ConfigureAwait(false);

            var validation = await EvaluationWorkspaceManifestStore.ValidateAsync(directory).ConfigureAwait(false);
            if (!validation.Valid)
            {
                WriteValidation(validation, jsonOutput: false);
                Console.Error.WriteLine("PSWORK016: Initialized workspace did not pass its structural validation.");
                return 1;
            }

            Console.WriteLine($"Initialized {template} evaluation workspace: {directory}");
            Console.WriteLine($"Customer data: {manifest.Paths.CustomerData}/");
            Console.WriteLine($"Discovery artifacts: {manifest.Paths.Discovery}/");
            Console.WriteLine($"Generated drafts (review required): {manifest.Paths.Generated}/");
            Console.WriteLine($"Run records: {manifest.Paths.Runs}/");
            Console.WriteLine($"Evidence packages: {manifest.Paths.Evidence}/");
            Console.WriteLine($"Temporary data: {manifest.Paths.Temporary}/");
            Console.WriteLine($"Workspace ID: {manifest.WorkspaceId:D}");
            return 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or
            InvalidOperationException or InvalidDataException or System.Text.Json.JsonException)
        {
            Console.Error.WriteLine($"PSWORK017: Workspace initialization failed ({exception.GetType().Name}); inspect the destination before retrying.");
            return 1;
        }
    }

    private static async Task<int> ValidateAsync(string[] args)
    {
        if (args.Length is not (3 or 4) ||
            (args.Length == 4 && !string.Equals(args[3], "--json", StringComparison.OrdinalIgnoreCase)))
        {
            WriteUsage();
            return 2;
        }

        EvaluationWorkspaceValidationResult result;
        try
        {
            result = await EvaluationWorkspaceManifestStore.ValidateAsync(args[2]).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            result = new(false, null, null, null,
                [new EvaluationWorkspaceIssue("PSWORK018", $"Workspace validation could not complete ({exception.GetType().Name}).")]);
        }

        WriteValidation(result, args.Length == 4);
        return result.Valid ? 0 : 1;
    }

    private static async Task<int> PreflightAsync(string[] args)
    {
        if (args.Length < 3 || args.Skip(3).Any(argument => argument is not ("--json" or "--connect")) ||
            args.Skip(3).Distinct(StringComparer.Ordinal).Count() != args.Length - 3)
        {
            WriteUsage();
            return 2;
        }

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            var result = await WorkspaceEvaluationService.PreflightAsync(args[2],
                args.Contains("--connect", StringComparer.Ordinal), cancellation.Token).ConfigureAwait(false);
            WritePreflight(result, args.Contains("--json", StringComparer.Ordinal));
            return result.Status == "READY" ? 0 : 1;
        }
        catch (OperationCanceledException)
        {
            return 130;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or
            InvalidOperationException or VerificationRuleException or RecoveryException or ConnectorReadException or
            System.Data.Common.DbException or System.Net.Sockets.SocketException or TimeoutException or System.Text.Json.JsonException)
        {
            Console.Error.WriteLine($"PSPREF099: Preflight could not complete ({exception.GetType().Name}); no configuration values were included.");
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static async Task<int> PlanAsync(string[] args)
    {
        if (args.Length < 3)
        {
            WriteUsage();
            return 2;
        }

        var jsonOutput = false;
        var baselineReference = "active";
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 3; index < args.Length; index++)
        {
            if (args[index] == "--json" && seen.Add(args[index]))
                jsonOutput = true;
            else if (args[index] == "--baseline" && index + 1 < args.Length &&
                     !args[index + 1].StartsWith("--", StringComparison.Ordinal) && seen.Add(args[index]))
                baselineReference = args[++index];
            else
            {
                WriteUsage();
                return 2;
            }
        }
        if (baselineReference != "active" && baselineReference != "none" &&
            (!Guid.TryParse(baselineReference, out var selectedBaseline) || selectedBaseline == Guid.Empty))
        {
            WriteUsage();
            return 2;
        }

        try
        {
            var result = await WorkspaceEvaluationService.PlanAsync(args[2], CancellationToken.None).ConfigureAwait(false);
            var drift = await WorkspaceSemanticDriftService.CompareAsync(args[2], result, baselineReference,
                CancellationToken.None).ConfigureAwait(false);
            result = result with { SemanticDrift = drift };
            if (jsonOutput)
                Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
            else
                WritePlan(result);
            return result.Status == "READY" &&
                drift.Status != WorkspaceSemanticDriftStatus.Blocking ? 0 : 1;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or
            InvalidDataException or InvalidOperationException or VerificationRuleException or RecoveryException or
            System.Text.Json.JsonException)
        {
            var failureLocation = new StackTrace(exception, false).GetFrames()?
                .Select(frame => frame.GetMethod())
                .FirstOrDefault(method => method?.DeclaringType?.Namespace?.StartsWith(
                    "ProofShift.", StringComparison.Ordinal) == true);
            var location = failureLocation is null
                ? "unknown"
                : $"{failureLocation.DeclaringType?.Name}.{failureLocation.Name}";
            Console.Error.WriteLine($"PSPLAN099: Workspace plan could not be generated ({exception.GetType().Name} at {location}); no configuration values were included.");
            return 1;
        }
    }

    private static async Task<int> RunEvaluationAsync(string[] args)
    {
        string? checkpoint = null;
        string? observation = null;
        string? review = null;
        var baseline = "active";
        var jsonOutput = false;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 3; index < args.Length; index++)
        {
            var option = args[index];
            if (option == "--json")
            {
                if (!seen.Add(option))
                {
                    WriteUsage();
                    return 2;
                }
                jsonOutput = true;
            }
            else if (option is ("--checkpoint" or "--observation" or "--review" or "--baseline") &&
                index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                var value = args[++index];
                if (string.IsNullOrWhiteSpace(value) || !seen.Add(option))
                {
                    WriteUsage();
                    return 2;
                }

                if (option == "--checkpoint") checkpoint = value;
                else if (option == "--observation") observation = value;
                else if (option == "--review") review = value;
                else baseline = value;
            }
            else
            {
                WriteUsage();
                return 2;
            }
        }

        if (args.Length < 3)
        {
            WriteUsage();
            return 2;
        }
        if (baseline != "active" && baseline != "none" &&
            (!Guid.TryParse(baseline, out var selectedBaseline) || selectedBaseline == Guid.Empty))
        {
            WriteUsage();
            return 2;
        }

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            var result = await WorkspaceRunService.RunAsync(args[2], checkpoint, observation,
                cancellation.Token, item =>
                {
                    if (jsonOutput)
                        Console.WriteLine(JsonSerializer.Serialize(new WorkspaceRunProgressEvent("progress", item), JsonLineOptions));
                    else
                        Console.Error.WriteLine($"[{item.Stage}] {item.Status}");
                }, baseline, review).ConfigureAwait(false);
            if (jsonOutput)
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    type = "result",
                    runPath = result.RunDirectory,
                    state = result.Manifest?.State ?? "Blocked",
                    run = result.Manifest,
                    baselineComparison = result.BaselineComparison,
                    readinessChecks = result.ReadinessChecks
                }, JsonLineOptions));
            }
            else
            {
                WriteRunResult(result);
            }

            if (result.Manifest?.State == "Interrupted") return 130;
            if (result.Manifest?.State != "Completed") return 1;
            return result.Manifest.Disposition == "QUALIFIED" ||
                result.Manifest.Disposition is null &&
                result.Manifest.VerificationOutcome is
                    (nameof(VerificationOutcome.Passed) or nameof(VerificationOutcome.PassedWithWarnings))
                ? 0 : 1;
        }
        catch (OperationCanceledException)
        {
            return 130;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or
            InvalidOperationException or InvalidDataException or VerificationRuleException or RecoveryException or
            ConnectorConfigurationException or ConnectorReadException or ConnectorResolutionException or
            SnapshotStoreException or ProjectionExecutionException or ProjectionManifestException or
            System.Data.Common.DbException or System.Net.Sockets.SocketException or TimeoutException or
            System.Text.Json.JsonException)
        {
            Console.Error.WriteLine($"PSWORKRUN099: Workspace execution could not be completed ({exception.GetType().Name}); no configuration values were included.");
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static void WriteRunResult(WorkspaceRunResult result)
    {
        Console.WriteLine("ProofShift Guided Workspace Evaluation");
        if (result.RunDirectory is not null) Console.WriteLine($"Run: {result.RunDirectory}");
        if (result.Manifest is null)
        {
            Console.WriteLine("State: BLOCKED (workspace or effective plan is invalid)");
            WritePlanChecks(result.ReadinessChecks);
            return;
        }

        Console.WriteLine($"State: {result.Manifest.State.ToUpperInvariant()}");
        Console.WriteLine($"Plan fingerprint: {result.Manifest.PlanFingerprint ?? "unavailable"}");
        Console.WriteLine($"Checkpoint: {result.Manifest.CheckpointId ?? "not captured"}");
        if (result.Manifest.VerificationRunId is not null)
            Console.WriteLine($"Verification: {result.Manifest.VerificationOutcome} ({result.Manifest.VerificationRunId})");
        if (result.Manifest.RecoveryQualification is not null)
            Console.WriteLine($"Recovery qualification: {result.Manifest.RecoveryQualification}");
        Console.WriteLine($"Disposition: {result.Manifest.Disposition ?? "not claimed"}");
        if (result.Manifest.BaselineId is { } baselineId)
        {
            Console.WriteLine($"Baseline: {baselineId:D} ({result.Manifest.BaselineRunId:D})");
            Console.WriteLine($"Pre-run semantic drift: {result.Manifest.PreRunSemanticDriftStatus} ({result.Manifest.PreRunSemanticDriftIdentityFingerprint})");
            Console.WriteLine($"Review: {result.Manifest.ReviewDecision ?? "NotRequired"}{(result.Manifest.ReviewRecordId is { } reviewId ? $" ({reviewId:D})" : string.Empty)}");
            Console.WriteLine($"Post-run outcome: {result.BaselineComparison?.OutcomeClassification ?? "NotEstablished"}");
            if (result.BaselineComparison is { } comparison)
            {
                foreach (var finding in comparison.DetailedComparison.Findings.Where(item => item.Category != "Persisting"))
                    Console.WriteLine($"Finding {finding.Category}: {finding.RuleId}, target {finding.TargetIdentity}, count {finding.Count}");
                foreach (var rule in comparison.DetailedComparison.ReconciliationFailuresAdded)
                    Console.WriteLine($"Reconciliation failure added: {rule}");
                foreach (var rule in comparison.DetailedComparison.ReconciliationFailuresResolved)
                    Console.WriteLine($"Reconciliation failure resolved: {rule}");
            }
        }
        if (result.Manifest.FailureCode is not null) Console.WriteLine($"Failure: {result.Manifest.FailureCode}");
    }

    private static void WritePreflight(WorkspacePreflightResult result, bool jsonOutput)
    {
        if (jsonOutput)
        {
            Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
            return;
        }

        Console.WriteLine("ProofShift Evaluation Workspace Preflight");
        Console.WriteLine($"Workspace: {result.WorkspaceId?.ToString("D") ?? "unavailable"}");
        Console.WriteLine($"Result: {result.Status} (no known blockers; review NOT_CHECKED items)");
        Console.WriteLine($"Live connectivity attempted: {(result.ConnectivityAttempted ? "yes" : "no")}");
        foreach (var check in result.Checks)
        {
            Console.WriteLine($"{check.Status} {check.Id} [{check.Category}]{(check.Component is null ? string.Empty : $" ({check.Component})")}{(check.Capability is null ? string.Empty : $" capability={check.Capability}")}: {check.Message}");
            if (check.Remediation is not null) Console.WriteLine($"  Next: {check.Remediation}");
        }
        Console.WriteLine("Preflight does not capture checkpoints, project data, run Verification, or produce a business disposition.");
    }

    private static void WritePlan(WorkspacePlanResult result)
    {
        Console.WriteLine("ProofShift Effective Workspace Evaluation Plan");
        Console.WriteLine($"Readiness: {result.Status}");
        if (result.Plan is null)
        {
            Console.WriteLine("No plan is available because the workspace or project configuration is invalid.");
            WriteSemanticDrift(result.SemanticDrift);
            WritePlanChecks(result.Preflight.Checks);
            return;
        }

        Console.WriteLine($"Workspace: {result.Plan.WorkspaceId:D}");
        Console.WriteLine($"Project: {result.Plan.ProjectName} ({result.Plan.ProjectId})");
        Console.WriteLine($"Configuration: {result.Plan.ConfigurationFile} (format {result.Plan.ConfigurationVersion})");
        Console.WriteLine($"Graph: {result.Plan.GraphHash} (canonicalization {result.Plan.GraphCanonicalizationVersion})");
        Console.WriteLine($"Verification rule-set fingerprint: {result.Plan.VerificationRuleSetFingerprint}");
        Console.WriteLine($"Expected-exception fingerprint: {result.Plan.ExpectedExceptionsFingerprint}");
        WriteSemanticDrift(result.SemanticDrift);
        Console.WriteLine("Packs:");
        if (result.Plan.Packs.Count == 0)
            Console.WriteLine("  (none explicitly configured)");
        foreach (var pack in result.Plan.Packs)
            Console.WriteLine($"  {pack.Id} {pack.Version}");

        Console.WriteLine("Systems and endpoints:");
        foreach (var system in result.Plan.Systems)
            Console.WriteLine($"  {system.System}/{system.Endpoint}: role={system.Role}, connector={system.Connector} {system.ConnectorVersion}, capabilities=[{string.Join(", ", system.Capabilities)}]");

        Console.WriteLine("Graph nodes:");
        foreach (var node in result.Plan.Nodes)
        {
            Console.WriteLine($"  {node.Key}: {node.Type} {node.System}/{node.Endpoint}, semantic={node.SemanticType}, selector={node.SelectorKind}, identity=[{string.Join(", ", node.IdentityFields)}]");
            if (node.ScopeId is not null)
            {
                Console.WriteLine($"    Scope: {node.ScopeId} ({node.ScopeType}), fingerprint={node.ScopeFingerprint}");
                foreach (var predicate in node.ScopePredicates)
                    Console.WriteLine($"    Predicate: {predicate.Field} {predicate.Operator} [value bound by scope fingerprint]");
            }
        }

        Console.WriteLine("Graph edges and mappings:");
        foreach (var edge in result.Plan.Edges)
        {
            Console.WriteLine($"  {edge.Key}: {edge.Operation}; sources=[{string.Join(", ", edge.Sources)}], targets=[{string.Join(", ", edge.Targets)}], recovery={edge.RecoveryMode ?? "not configured"}");
            foreach (var field in edge.Fields)
                Console.WriteLine($"    {field.Source ?? "(constant)"} -> {field.Target}; transformations=[{string.Join(", ", field.Transformations)}]");
        }

        Console.WriteLine("Verification rules:");
        foreach (var rule in result.Plan.Rules)
            Console.WriteLine($"  {rule.Id}: {rule.Type} {rule.Version}, severity={rule.Severity}");
        Console.WriteLine($"Reconciliation rules: {(result.Plan.ReconciliationRules.Count == 0 ? "(none configured)" : string.Join(", ", result.Plan.ReconciliationRules))}");

        Console.WriteLine("Recovery:");
        Console.WriteLine($"  Require recovery for destructive operations: {result.Plan.RequireRecoveryForDestructiveOperations}");
        Console.WriteLine($"  Allow irreversible operations: {result.Plan.AllowIrreversibleOperations}");
        Console.WriteLine($"Checkpoint: {result.Plan.CheckpointOperation}");
        Console.WriteLine($"Checkpoint output: {result.Plan.CheckpointOutput}");
        Console.WriteLine($"Replay: {result.Plan.ReplayBehavior}");
        Console.WriteLine($"Projection: {result.Plan.ProjectionStrategy}");
        Console.WriteLine($"Run/evidence output: {result.Plan.RunOutput}/, {result.Plan.EvidenceOutput}/");
        Console.WriteLine($"Cleanup: {result.Plan.CleanupImplications}");
        Console.WriteLine($"Expected exceptions: {result.Plan.ExpectedExceptionSemantics}");
        Console.WriteLine("Intentionally omitted:");
        foreach (var item in result.Plan.IntentionallyOmittedCapabilities)
            Console.WriteLine($"  {item}");
        Console.WriteLine("Preflight checks:");
        WritePlanChecks(result.Plan.PreflightChecks);
        Console.WriteLine($"Plan fingerprint: {result.Plan.Fingerprint}");
        Console.WriteLine("This plan is read-only and does not execute the evaluation.");
    }

    private static void WriteSemanticDrift(WorkspaceSemanticDrift? drift)
    {
        Console.WriteLine("Baseline semantic comparison:");
        if (drift is null)
        {
            Console.WriteLine("  Not requested.");
            return;
        }
        Console.WriteLine($"  Status: {drift.Status}");
        if (drift.BaselineId is { } baselineId)
            Console.WriteLine($"  Baseline: {baselineId:D} (run {drift.BaselineRunId:D})");
        Console.WriteLine($"  Review required: {(drift.ReviewRequired ? "yes" : "no")}");
        foreach (var condition in drift.BlockingConditions)
            Console.WriteLine($"  BLOCKING: {condition}");
        foreach (var change in drift.Changes)
            Console.WriteLine($"  {change.Classification} {change.Category}/{change.Kind} {change.Subject}: {change.Detail}");
    }

    private static void WritePlanChecks(IEnumerable<WorkspacePreflightCheck> checks)
    {
        foreach (var check in checks)
        {
            Console.WriteLine($"  {check.Status} {check.Id} [{check.Category}]{(check.Component is null ? string.Empty : $" ({check.Component})")}{(check.Capability is null ? string.Empty : $" capability={check.Capability}")}: {check.Message}");
            if (check.Remediation is not null) Console.WriteLine($"    Next: {check.Remediation}");
        }
    }

    private static void WriteValidation(EvaluationWorkspaceValidationResult result, bool jsonOutput)
    {
        if (jsonOutput)
        {
            Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
            return;
        }

        Console.WriteLine("ProofShift Evaluation Workspace Validation");
        Console.WriteLine($"Status: {(result.Valid ? "VALID" : "INVALID")}");
        if (result.WorkspaceId is { } id) Console.WriteLine($"Workspace ID: {id:D}");
        if (result.ProjectConfiguration is { } configuration)
            Console.WriteLine($"Project configuration: {configuration}");
        foreach (var issue in result.Issues) Console.WriteLine($"{issue.Code}: {issue.Message}");
        Console.WriteLine("This command validates local workspace/configuration structure only; it does not determine readiness or connect to customer systems.");
    }

    private static int WriteUsageAndReturn()
    {
        WriteUsage();
        return 2;
    }

    private static void WriteUsage() =>
        Console.Error.WriteLine("Usage: proofshift workspace init <directory> [--template generic|pension|--from <workspace>] | proofshift workspace validate <directory> [--json] | proofshift workspace runs <workspace> [--json] | proofshift workspace preflight <directory> [--connect] [--json] | proofshift workspace plan <directory> [--baseline active|<baseline-id>|none] [--json] | proofshift workspace review-plan <directory> [--baseline active|<baseline-id>] [--json] | proofshift workspace run <directory> [--baseline active|<baseline-id>|none] [--review <review-id>] [--checkpoint <id-or-path>] [--observation <label>] [--json] | proofshift workspace evidence package <workspace> <run-id> [--output <zip>] [--diagnostic] [--json] | proofshift workspace evidence validate-package <zip> [--json] | proofshift workspace evidence compare-packages <baseline.zip> <candidate.zip> [--json] | proofshift workspace baseline promote <workspace> <run-id> [--json] | proofshift workspace baseline list <workspace> [--json] | proofshift workspace baseline show <workspace> <baseline-id> [--json] | proofshift workspace baseline active <workspace> [--json] | proofshift workspace summary <workspace> <run-id> [--output <path>] [--json] | proofshift workspace compare <workspace> <run-a> <run-b> [--json] | proofshift workspace cleanup <workspace> <run-id> [--plan|--execute] [--json]");
}
