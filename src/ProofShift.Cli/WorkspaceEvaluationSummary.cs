using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;
using ProofShift.Evidence;
using ProofShift.Snapshots;

namespace ProofShift.Cli;

internal sealed record WorkspaceEvaluationSummaryMetadata(
    string Format,
    Guid RunId,
    string RunManifestIdentityFingerprint,
    string RunManifestHash,
    string? PlanFingerprint,
    string? EvidenceFingerprint,
    string ProductVersion,
    string BodySha256,
    string ReportIdentity);

internal sealed record WorkspaceEvaluationSummaryResult(
    string SummaryPath,
    Guid RunId,
    string ReportIdentity,
    string State,
    string? Disposition,
    int FailedFindingCount);

internal sealed record WorkspaceEvaluationFinding(
    string RuleId,
    string? Code,
    string Severity,
    string TargetIdentity,
    string ValueFingerprint,
    string EvidenceId);

internal sealed record WorkspaceExpectedException(
    string AnnotationIdentity,
    string Result,
    string? RuleId,
    string? Code,
    string TargetIdentity,
    string? FindingEvidenceId,
    string AnnotationFingerprint);

internal sealed record WorkspaceEvaluationEvidence(
    IReadOnlyList<WorkspaceEvaluationFinding> Findings,
    IReadOnlyList<WorkspaceExpectedException> ExpectedExceptions,
    string? EvidenceFingerprint);

internal static class WorkspaceEvaluationSummaryService
{
    internal const string SummaryRole = "evaluation-summary";
    internal const string SummaryPath = "artifacts/summary.md";
    internal const string SummaryFormat = "proofshift-evaluation-summary-v1";
    private const string MetadataPrefix = "<!-- proofshift-evaluation-summary:";
    private static readonly JsonSerializerOptions MetadataJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private static readonly JsonElement EmptyArray = CreateEmptyArray();

    public static async Task<WorkspaceEvaluationSummaryResult> CreateAsync(string workspaceDirectory,
        string runReference, string? outputPath, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(workspaceDirectory);
        var validation = await EvaluationWorkspaceManifestStore.ValidateAsync(root).ConfigureAwait(false);
        if (!validation.Valid)
            throw new InvalidDataException("Workspace must pass offline structural validation before generating a summary.");
        var workspace = await EvaluationWorkspaceManifestStore.ReadAsync(root).ConfigureAwait(false)
            ?? throw new InvalidDataException("Workspace manifest is unavailable.");
        if (!Guid.TryParse(runReference, out var runId) || runId == Guid.Empty)
            throw new ArgumentException("Run reference must be a non-empty run GUID.", nameof(runReference));

        var runsRoot = ResolveContained(root, workspace.Paths.Runs);
        var runDirectory = ResolveContained(runsRoot, runId.ToString("D"));
        EnsureNoReparsePoints(root, runDirectory);
        if (!Directory.Exists(runDirectory))
            throw new FileNotFoundException("The selected workspace run does not exist.", runDirectory);
        var run = await WorkspaceRunManifestStore.ReadAsync(runDirectory, cancellationToken).ConfigureAwait(false);
        ValidateRunBinding(workspace.WorkspaceId, runId, run);

        var planResult = await WorkspaceEvaluationService.PlanAsync(root, cancellationToken).ConfigureAwait(false);
        var plan = planResult.Plan
            ?? throw new InvalidDataException("A safe effective plan could not be generated for this run.");
        if (run.PlanFingerprint != plan.Fingerprint || run.GraphHash != plan.GraphHash)
            throw new InvalidDataException("Current workspace plan does not match the run; refusing to generate a contradictory summary.");

        var content = await CreateContentAsync(runDirectory, run, plan, cancellationToken).ConfigureAwait(false);
        var destination = outputPath is null
            ? Path.Combine(runDirectory, "summary.md")
            : Path.GetFullPath(outputPath);
        if (!IsContained(runDirectory, destination) ||
            !string.Equals(Path.GetDirectoryName(destination), runDirectory, PathComparison) ||
            !string.Equals(Path.GetExtension(destination), ".md", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Summary output must be a Markdown file directly within the selected run directory.", nameof(outputPath));
        var destinationDirectory = Path.GetDirectoryName(destination)
            ?? throw new ArgumentException("Summary output must include a parent directory.", nameof(outputPath));
        Directory.CreateDirectory(destinationDirectory);
        EnsureNoReparsePoints(root, destination);
        if (File.Exists(destination))
        {
            if (!string.Equals(await File.ReadAllTextAsync(destination, cancellationToken).ConfigureAwait(false),
                    content, StringComparison.Ordinal))
                throw new InvalidDataException("Existing summary differs from authoritative run and Evidence data; refusing to overwrite it.");
        }
        else
        {
            await WriteAtomicAsync(destination, Encoding.UTF8.GetBytes(content), cancellationToken).ConfigureAwait(false);
        }
        var metadata = ReadMetadata(content);
        var evidence = await ReadEvidenceAsync(runDirectory, run, cancellationToken).ConfigureAwait(false);
        return new(destination, runId, metadata.ReportIdentity, run.State, run.Disposition, evidence.Findings.Count);
    }

    internal static async Task<string> CreateContentAsync(string runDirectory, WorkspaceRunManifest run,
        WorkspaceEvaluationPlan plan, CancellationToken cancellationToken)
    {
        if (run.PlanFingerprint != plan.Fingerprint || run.GraphHash != plan.GraphHash)
            throw new InvalidDataException("Effective plan identity does not match the authoritative run.");
        ValidateDisposition(run);
        var evidence = await ReadEvidenceAsync(runDirectory, run, cancellationToken).ConfigureAwait(false);
        var body = RenderBody(run, plan, evidence);
        var bodyHash = Hash(Encoding.UTF8.GetBytes(body));
        var provisional = new WorkspaceEvaluationSummaryMetadata(SummaryFormat, run.RunId,
            run.IdentityFingerprint, run.ManifestHash!, run.PlanFingerprint, evidence.EvidenceFingerprint,
            run.ProductVersion, bodyHash, string.Empty);
        var reportIdentity = Hash(JsonSerializer.SerializeToUtf8Bytes(
            provisional with { ReportIdentity = string.Empty }, MetadataJsonOptions));
        var metadata = provisional with { ReportIdentity = reportIdentity };
        return $"{MetadataPrefix}{JsonSerializer.Serialize(metadata, MetadataJsonOptions)} -->\n{body}";
    }

    internal static WorkspaceEvaluationSummaryMetadata ReadMetadata(string content)
    {
        var newline = content.IndexOf('\n');
        if (newline < 0 || !content[..newline].StartsWith(MetadataPrefix, StringComparison.Ordinal) ||
            !content[..newline].EndsWith(" -->", StringComparison.Ordinal))
            throw new InvalidDataException("Evaluation summary metadata header is missing or malformed.");
        var json = content[(MetadataPrefix.Length)..(content[..newline].Length - 4)];
        var metadata = JsonSerializer.Deserialize<WorkspaceEvaluationSummaryMetadata>(json, MetadataJsonOptions)
            ?? throw new InvalidDataException("Evaluation summary metadata is empty.");
        if (metadata.Format != SummaryFormat || metadata.RunId == Guid.Empty ||
            !IsSha256(metadata.RunManifestIdentityFingerprint) || !IsSha256(metadata.RunManifestHash) ||
            !IsSha256(metadata.ReportIdentity) || !IsSha256(metadata.BodySha256) ||
            string.IsNullOrWhiteSpace(metadata.ProductVersion))
            throw new InvalidDataException("Evaluation summary metadata identity is invalid.");
        var body = content[(newline + 1)..];
        if (Hash(Encoding.UTF8.GetBytes(body)) != metadata.BodySha256)
            throw new InvalidDataException("Evaluation summary content does not match its body identity.");
        var expectedIdentity = Hash(JsonSerializer.SerializeToUtf8Bytes(
            metadata with { ReportIdentity = string.Empty }, MetadataJsonOptions));
        if (metadata.ReportIdentity != expectedIdentity)
            throw new InvalidDataException("Evaluation summary report identity does not match its metadata.");
        return metadata;
    }

    internal static async Task<WorkspaceEvaluationEvidence> ReadEvidenceAsync(string runDirectory,
        WorkspaceRunManifest run, CancellationToken cancellationToken)
    {
        if (run.EvidenceReference is null)
        {
            if (run.State == "Completed")
                throw new InvalidDataException("A completed run has no authoritative Evidence reference.");
            return new([], [], null);
        }
        if (!Guid.TryParse(run.VerificationRunId, out var verificationId) || verificationId == Guid.Empty)
            throw new InvalidDataException("Evidence reference has no valid Verification run identity.");
        var evidenceRoot = Path.Combine(runDirectory, "evidence");
        var store = new FileSystemEvidenceStore(evidenceRoot);
        var verificationRunId = new ProofShift.Domain.RunId(verificationId);
        if (!await store.VerifyIntegrityAsync(verificationRunId, cancellationToken).ConfigureAwait(false))
            throw new InvalidDataException("Run Evidence failed its existing integrity validation.");
        var manifest = await store.ReadManifestAsync(verificationRunId, cancellationToken).ConfigureAwait(false);
        if (manifest.IntegrityHash != run.EvidenceManifestIntegrityHash ||
            manifest.Fingerprint != run.VerificationEvidenceFingerprint)
            throw new InvalidDataException("Evidence identity does not match the authoritative run manifest.");

        var findings = new List<WorkspaceEvaluationFinding>();
        var findingsByEvidenceId = new Dictionary<string, WorkspaceEvaluationFinding>(StringComparer.Ordinal);
        var pendingExceptions = new List<PendingExpectedException>();
        await foreach (var record in store.ReadRecordsAsync(verificationRunId, cancellationToken)
                           .WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            var ruleId = GetString(record, "ruleId");
            var code = GetNullableString(record, "code");
            var result = GetString(record, "result");
            var inputs = GetArray(record, "inputs");
            if (ruleId == "proofshift.verification.expected-exception-annotation")
            {
                var targetIdentity = ArtifactIdentity(inputs);
                var relatedFinding = inputs.EnumerateArray().Select(input => GetNullableString(input, "evidenceId"))
                    .FirstOrDefault(id => id is not null);
                var explanation = GetString(record, "explanation");
                var annotationRule = inputs.EnumerateArray().Select(input => GetNullableString(input, "ruleId"))
                    .FirstOrDefault(value => value is not null);
                pendingExceptions.Add(new PendingExpectedException(
                    ParseAnnotationIdentity(explanation) ?? GetString(record, "id"), result, annotationRule,
                    targetIdentity, relatedFinding, Hash(explanation)));
                continue;
            }
            if (result != "Fail") continue;
            var expected = GetRawValue(record, "expected");
            var actual = GetRawValue(record, "actual");
            var finding = new WorkspaceEvaluationFinding(ruleId, code, GetString(record, "severity"),
                ArtifactIdentity(inputs), Hash($"{expected}\n{actual}"), GetString(record, "id"));
            findings.Add(finding);
            findingsByEvidenceId.Add(finding.EvidenceId, finding);
        }

        var exceptions = pendingExceptions.Select(annotation =>
        {
            var linked = annotation.FindingEvidenceId is not null &&
                findingsByEvidenceId.TryGetValue(annotation.FindingEvidenceId, out var finding)
                ? finding
                : null;
            return new WorkspaceExpectedException(annotation.AnnotationIdentity, annotation.Result,
                linked?.RuleId ?? annotation.RuleId, linked?.Code, annotation.TargetIdentity,
                annotation.FindingEvidenceId, annotation.AnnotationFingerprint);
        }).ToArray();

        return new WorkspaceEvaluationEvidence(
            findings.OrderBy(item => item.RuleId, StringComparer.Ordinal)
                .ThenBy(item => item.Code, StringComparer.Ordinal)
                .ThenBy(item => item.TargetIdentity, StringComparer.Ordinal).ToArray(),
            exceptions.OrderBy(item => item.AnnotationIdentity, StringComparer.Ordinal)
                .ThenBy(item => item.TargetIdentity, StringComparer.Ordinal).ToArray(),
            manifest.Fingerprint);
    }

    private sealed record PendingExpectedException(
        string AnnotationIdentity,
        string Result,
        string? RuleId,
        string TargetIdentity,
        string? FindingEvidenceId,
        string AnnotationFingerprint);

    internal static string RenderBody(WorkspaceRunManifest run, WorkspaceEvaluationPlan plan,
        WorkspaceEvaluationEvidence evidence)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# ProofShift evaluation summary").AppendLine();
        builder.AppendLine("## Evaluation identity").AppendLine();
        builder.AppendLine(CultureInfo.InvariantCulture, $"- Project: {Escape(plan.ProjectName)} (`{Escape(plan.ProjectId)}`)");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- Workspace: `{run.WorkspaceId:D}`");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- Run: `{run.RunId:D}`");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- ProofShift version: `{Escape(run.ProductVersion)}`");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- Source revision/tree: `{Escape(run.SourceRevision ?? "unavailable")}` / `{Escape(run.SourceTree ?? "unavailable")}`");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- Execution mode: {(run.ReplayedCheckpoint == true ? "checkpoint replay" : run.ReplayedCheckpoint == false ? "fresh source capture" : "not established")}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- Effective plan fingerprint: `{run.PlanFingerprint ?? "unavailable"}`");
        builder.AppendLine();
        builder.AppendLine("## Scope and target model").AppendLine();
        var scopes = plan.Nodes.Where(node => node.Type == "Source" && node.ScopeId is not null)
            .OrderBy(node => node.Key, StringComparer.Ordinal).ToArray();
        if (scopes.Length == 0)
            builder.AppendLine("- Scope: no explicit evaluation-scope identifier is recorded in the effective plan.");
        foreach (var node in scopes)
            builder.AppendLine(CultureInfo.InvariantCulture, $"- `{Escape(node.Key)}`: `{Escape(node.ScopeId!)}` ({Escape(node.ScopeType ?? "unspecified")}); scope fingerprint `{node.ScopeFingerprint ?? "unavailable"}`.");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- Checkpoint: `{run.CheckpointId ?? "not captured"}`; source fingerprint `{run.CheckpointSourceFingerprint ?? "unavailable"}`.");
        var targets = plan.Systems.Where(system => system.Role is "ShadowTarget" or "Target")
            .OrderBy(system => system.System, StringComparer.Ordinal).ThenBy(system => system.Endpoint, StringComparer.Ordinal).ToArray();
        if (targets.Length == 0) builder.AppendLine("- Target model: no target endpoint is recorded.");
        foreach (var target in targets)
            builder.AppendLine(CultureInfo.InvariantCulture, $"- `{Escape(target.System)}/{Escape(target.Endpoint)}`: {Escape(target.Role)} via {Escape(target.Connector)}.");
        builder.AppendLine();

        builder.AppendLine("## Execution and disposition").AppendLine();
        builder.AppendLine(CultureInfo.InvariantCulture, $"- Execution state: **{Escape(run.State)}**.");
        if (run.State == "Completed" && run.Disposition is not null)
            builder.AppendLine(CultureInfo.InvariantCulture, $"- Final disposition: **{run.Disposition}**. This is an evaluation result, not a guarantee of production migration safety.");
        else
        {
            builder.AppendLine("- Final business disposition: **not established**.");
            var lastCompleted = run.Progress.LastOrDefault(item => item.Status is "completed" or "captured")?.Stage;
            builder.AppendLine(CultureInfo.InvariantCulture, $"- Last completed stage: {Escape(lastCompleted ?? "none recorded")}.");
            builder.AppendLine(CultureInfo.InvariantCulture, $"- Diagnostic code: `{Escape(run.FailureCode ?? "not recorded")}`.");
        }
        builder.AppendLine(CultureInfo.InvariantCulture, $"- Verification: **{Escape(run.VerificationOutcome ?? "not completed")}**.");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- Failed Verification findings: **{evidence.Findings.Count}**.");
        if (evidence.Findings.Count == 0)
            builder.AppendLine("- No unresolved failed Verification findings were recorded.");
        else
        {
            builder.AppendLine("- Failed findings (identifiers only; raw expected/actual values are omitted):");
            foreach (var finding in evidence.Findings)
                builder.AppendLine(CultureInfo.InvariantCulture, $"  - `{Escape(finding.RuleId)}` / `{Escape(finding.Code ?? "no-code")}` ({Escape(finding.Severity)}); target reference `{finding.TargetIdentity}`.");
        }
        var failedReconciliation = run.ReconciliationFailedRuleIds.ToHashSet(StringComparer.Ordinal);
        if (run.ReconciliationRuleIds.Count == 0)
            builder.AppendLine("- Reconciliation: no reconciliation rules were configured.");
        else
            builder.AppendLine(CultureInfo.InvariantCulture, $"- Reconciliation: {(failedReconciliation.Count == 0 ? "passed" : "failed")}; {run.ReconciliationRuleIds.Count - failedReconciliation.Count}/{run.ReconciliationRuleIds.Count} configured checks passed.");
        foreach (var failed in failedReconciliation.Order(StringComparer.Ordinal))
            builder.AppendLine(CultureInfo.InvariantCulture, $"  - Failed check: `{Escape(failed)}`.");

        var matchedExceptions = evidence.ExpectedExceptions.Where(item => item.Result == "Warning").ToArray();
        var unmatchedExceptions = evidence.ExpectedExceptions.Where(item => item.Code == "ExpectedExceptionNotMatched").ToArray();
        builder.AppendLine(CultureInfo.InvariantCulture, $"- Expected exceptions matched: {matchedExceptions.Length}; unmatched: {unmatchedExceptions.Length}.");
        builder.AppendLine("- Expected exceptions waive no findings; an annotated finding remains failed.");
        var materializedStateChanged = evidence.Findings.Any(item =>
            item.RuleId.Contains("materialized-state", StringComparison.OrdinalIgnoreCase) ||
            item.Code?.Contains("MaterializedTargetChanged", StringComparison.OrdinalIgnoreCase) == true);
        builder.AppendLine(CultureInfo.InvariantCulture, $"- Materialized-state/integrity change detected: {(materializedStateChanged ? "yes" : "no failed materialized-state finding recorded")}.");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- Recovery qualification: **{Escape(run.RecoveryQualification ?? "not assessed")}**.");
        if (run.EvidenceReference is not null)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"- Supporting Evidence fingerprint: `{run.VerificationEvidenceFingerprint ?? "unavailable"}`.");
            builder.AppendLine("- Supporting Evidence: the run-bound Evidence manifest and records identified by this fingerprint.");
        }
        else builder.AppendLine("- Supporting Evidence: no persisted Evidence reference is available.");
        builder.AppendLine();
        builder.AppendLine("## Limitations and next steps").AppendLine();
        builder.AppendLine("- Results cover only the recorded scope and checkpoint; they do not establish completeness outside that scope.");
        if (run.State == "Completed" && run.Disposition is null && run.RecoveryQualification is null)
            builder.AppendLine("- External Verification is read-only and does not qualify Recovery.");
        builder.AppendLine("- Expected-exception annotations are review context only and never waive failed findings.");
        builder.AppendLine("- This summary is a human-readable projection of the authoritative run and Evidence, not authoritative evidence itself.");
        builder.AppendLine("- Production migration and production rollback are not authorized by this evaluation.");
        builder.AppendLine("- Review the failed rules, reconciliation results, scope, Evidence records, and any materialized-state changes before drawing conclusions.");
        return builder.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static void ValidateRunBinding(Guid workspaceId, Guid runId, WorkspaceRunManifest run)
    {
        if (run.WorkspaceId != workspaceId || run.RunId != runId)
            throw new InvalidDataException("Run manifest identity does not match the selected workspace and run.");
        ValidateDisposition(run);
    }

    private static void ValidateDisposition(WorkspaceRunManifest run)
    {
        if (run.Disposition is not (null or "QUALIFIED" or "NOT QUALIFIED"))
            throw new InvalidDataException("Run contains an unsupported business disposition.");
        if (run.State != "Completed" && run.Disposition is not null)
            throw new InvalidDataException("An incomplete run cannot claim a business disposition.");
        if (run.State == "Completed" && run.Disposition is null &&
            run.VerificationOutcome is not ("ExternalVerification" or "Passed" or "Failed" or "PassedWithWarnings"))
            throw new InvalidDataException("Completed run disposition is inconsistent with its execution record.");
    }

    private static string ArtifactIdentity(JsonElement inputs)
    {
        var references = inputs.EnumerateArray()
            .Where(item => item.TryGetProperty("artifactId", out var artifact) && artifact.ValueKind == JsonValueKind.String)
            .Select(item => $"{GetNullableString(item, "graphNodeId") ?? "unscoped"}:{GetString(item, "artifactId")}")
            .Order(StringComparer.Ordinal).ToArray();
        if (references.Length == 0) return "none";
        return string.Join(",", references.Select(item => Hash(item)[..16]));
    }

    private static string? ParseAnnotationIdentity(string explanation)
    {
        const string prefix = "Expected exception '";
        var start = explanation.IndexOf(prefix, StringComparison.Ordinal);
        if (start < 0) return null;
        start += prefix.Length;
        var end = explanation.IndexOf('\'', start);
        return end <= start ? null : explanation[start..end];
    }

    private static JsonElement GetArray(JsonElement parent, string property) =>
        parent.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
            ? value : EmptyArray;

    private static JsonElement CreateEmptyArray()
    {
        using var document = JsonDocument.Parse("[]");
        return document.RootElement.Clone();
    }

    private static string GetString(JsonElement parent, string property) =>
        GetNullableString(parent, property) ?? throw new InvalidDataException($"Evidence record is missing '{property}'.");

    private static string? GetNullableString(JsonElement parent, string property) =>
        parent.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static string GetRawValue(JsonElement parent, string property) =>
        parent.TryGetProperty(property, out var value) ? value.GetRawText() : string.Empty;

    private static string Escape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("`", "\\`", StringComparison.Ordinal).Replace("\r", " ", StringComparison.Ordinal)
        .Replace("\n", " ", StringComparison.Ordinal);

    private static async Task WriteAtomicAsync(string destination, byte[] bytes, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(destination)!;
        var temporary = Path.Combine(directory, $".{Path.GetFileName(destination)}-{Guid.NewGuid():N}.partial");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string ResolveContained(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root);
        var full = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsContained(fullRoot, full)) throw new InvalidDataException("Workspace path is outside its configured root.");
        return full;
    }

    private static bool IsContained(string root, string path)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return !Path.IsPathRooted(relative) && relative != ".." &&
            !relative.StartsWith($"..{Path.DirectorySeparatorChar}", PathComparison);
    }

    private static void EnsureNoReparsePoints(string root, string candidate)
    {
        var fullRoot = Path.GetFullPath(root);
        var relative = Path.GetRelativePath(fullRoot, Path.GetFullPath(candidate));
        if (Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", PathComparison))
            throw new InvalidDataException("Workspace path is outside its configured root.");
        var current = fullRoot;
        if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Workspace path traverses a symbolic link or reparse point.");
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Workspace path traverses a symbolic link or reparse point.");
        }
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static string Hash(string value) => Hash(Encoding.UTF8.GetBytes(value));

    private static string Hash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
