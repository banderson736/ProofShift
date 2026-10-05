using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ProofShift.Domain;

namespace ProofShift.Recovery;

public sealed record RecoveryArtifactReceipt(DryRunId Id, string RelativeDirectory, string IntegrityHash);

public sealed record RecoveryArtifactSummary
{
    public DryRunId Id { get; }
    public RunId VerificationRunId { get; }
    public string ConfigurationHash { get; }
    public string GraphHash { get; }
    public string CheckpointManifestHash { get; }
    public string SourceFingerprint { get; }
    public string ProjectionFingerprint { get; }
    public string RuleSetFingerprint { get; }
    public DryRunQualificationStatus Status { get; }
    public string DryRunFingerprint { get; }
    public string PolicyFingerprint { get; }
    public string AssessmentFingerprint { get; }
    public string PlanFingerprint { get; }
    public string RehearsalFingerprint { get; }
    public string VerificationEvidenceFingerprint { get; }
    public string RecoveryEvidenceFingerprint { get; }
    public long ExecutedEdges { get; }
    public long ReverseEdges { get; }
    public long RestoreEdges { get; }
    public long CompensateEdges { get; }
    public long IrreversibleEdges { get; }
    public long FalseReversibleEdges { get; }
    public long AffectedArtifacts { get; }
    public long RecoverableArtifacts { get; }
    public long IrrecoverableArtifacts { get; }
    public long UnknownArtifacts { get; }
    public decimal? RecoverablePercentage { get; }
    public long FailedEdges { get; }
    public string RehearsalOutcome { get; }
    public IReadOnlyList<string> Reasons { get; }
    public IReadOnlyCollection<RecoverySemanticTypeCoverage> BySemanticType { get; }

    internal RecoveryArtifactSummary(DryRunId id, RunId verificationRunId, string configurationHash,
        string graphHash, string checkpointManifestHash, string sourceFingerprint, string projectionFingerprint,
        string ruleSetFingerprint, DryRunQualificationStatus status, string dryRunFingerprint,
        string policyFingerprint, string assessmentFingerprint, string planFingerprint, string rehearsalFingerprint,
        string verificationEvidenceFingerprint, string recoveryEvidenceFingerprint, long executedEdges,
        long reverseEdges, long restoreEdges, long compensateEdges, long irreversibleEdges, long falseReversibleEdges,
        long affectedArtifacts, long recoverableArtifacts, long irrecoverableArtifacts, long unknownArtifacts,
        decimal? recoverablePercentage, long failedEdges, string rehearsalOutcome,
        IReadOnlyList<string> reasons, IEnumerable<RecoverySemanticTypeCoverage> bySemanticType)
    {
        Id = id;
        VerificationRunId = verificationRunId;
        ConfigurationHash = configurationHash;
        GraphHash = graphHash;
        CheckpointManifestHash = checkpointManifestHash;
        SourceFingerprint = sourceFingerprint;
        ProjectionFingerprint = projectionFingerprint;
        RuleSetFingerprint = ruleSetFingerprint;
        Status = status;
        DryRunFingerprint = dryRunFingerprint;
        PolicyFingerprint = policyFingerprint;
        AssessmentFingerprint = assessmentFingerprint;
        PlanFingerprint = planFingerprint;
        RehearsalFingerprint = rehearsalFingerprint;
        VerificationEvidenceFingerprint = verificationEvidenceFingerprint;
        RecoveryEvidenceFingerprint = recoveryEvidenceFingerprint;
        ExecutedEdges = executedEdges;
        ReverseEdges = reverseEdges;
        RestoreEdges = restoreEdges;
        CompensateEdges = compensateEdges;
        IrreversibleEdges = irreversibleEdges;
        FalseReversibleEdges = falseReversibleEdges;
        AffectedArtifacts = affectedArtifacts;
        RecoverableArtifacts = recoverableArtifacts;
        IrrecoverableArtifacts = irrecoverableArtifacts;
        UnknownArtifacts = unknownArtifacts;
        RecoverablePercentage = recoverablePercentage;
        FailedEdges = failedEdges;
        RehearsalOutcome = rehearsalOutcome;
        Reasons = reasons;
        BySemanticType = bySemanticType.ToArray();
    }
}

public interface IRecoveryArtifactStore
{
    Task<RecoveryArtifactReceipt> SaveAsync(RecoveryRunResult result, CancellationToken cancellationToken);
    Task<bool> VerifyIntegrityAsync(DryRunId dryRunId, CancellationToken cancellationToken);
    Task<RecoveryArtifactSummary> ReadSummaryAsync(DryRunId dryRunId, CancellationToken cancellationToken);
}

public sealed class FileSystemRecoveryArtifactStore : IRecoveryArtifactStore
{
    public const string StoreFormatVersion = "proofshift-recovery-store-v1";
    private static readonly string[] ArtifactNames = ["assessment.json", "plan.json", "rehearsal.json", "recovery-evidence.json", "qualification.json"];
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private readonly string _root;

    public FileSystemRecoveryArtifactStore(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.GetFullPath(root);
    }

    public async Task<RecoveryArtifactReceipt> SaveAsync(RecoveryRunResult result, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        cancellationToken.ThrowIfCancellationRequested();
        var directory = GetDirectory(result.Id);
        Directory.CreateDirectory(directory);
        RestrictDirectory(directory);
        if (Directory.EnumerateFileSystemEntries(directory).Any())
            throw new InvalidOperationException("Recovery artifact directory already exists.");

        var artifacts = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["assessment.json"] = JsonSerializer.SerializeToUtf8Bytes(ToDocument(result.Assessment), JsonOptions),
            ["plan.json"] = JsonSerializer.SerializeToUtf8Bytes(ToDocument(result.Plan), JsonOptions),
            ["rehearsal.json"] = JsonSerializer.SerializeToUtf8Bytes(ToDocument(result.Rehearsal), JsonOptions),
            ["recovery-evidence.json"] = JsonSerializer.SerializeToUtf8Bytes(ToDocument(result.EvidenceGraph), JsonOptions),
            ["qualification.json"] = JsonSerializer.SerializeToUtf8Bytes(ToDocument(result.Qualification), JsonOptions)
        };

        try
        {
            var hashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var (name, bytes) in artifacts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await WriteAtomicAsync(Path.Combine(directory, name), bytes, cancellationToken).ConfigureAwait(false);
                hashes.Add(name, Hash(bytes));
            }
            var draft = new ManifestDocument(StoreFormatVersion, result.Id.Value.ToString("D", CultureInfo.InvariantCulture), hashes, null);
            var integrityHash = Hash(JsonSerializer.SerializeToUtf8Bytes(draft, JsonOptions));
            var manifest = JsonSerializer.SerializeToUtf8Bytes(draft with { IntegrityHash = integrityHash }, JsonOptions);
            await WriteAtomicAsync(Path.Combine(directory, "manifest.json"), manifest, cancellationToken).ConfigureAwait(false);
            return new RecoveryArtifactReceipt(result.Id, Path.GetRelativePath(_root, directory).Replace(Path.DirectorySeparatorChar, '/'), integrityHash);
        }
        catch
        {
            TryDeleteDirectory(directory);
            throw;
        }
    }

    public async Task<bool> VerifyIntegrityAsync(DryRunId dryRunId, CancellationToken cancellationToken)
    {
        var directory = GetDirectory(dryRunId);
        try
        {
            await using var stream = File.OpenRead(Path.Combine(directory, "manifest.json"));
            var document = await JsonSerializer.DeserializeAsync<ManifestDocument>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            if (document is null || document.Format != StoreFormatVersion || document.Files is null || document.IntegrityHash is null ||
                !Guid.TryParseExact(document.DryRunId, "D", out var parsedId) || parsedId != dryRunId.Value ||
                !ArtifactNames.Order(StringComparer.Ordinal).SequenceEqual(document.Files.Keys.Order(StringComparer.Ordinal), StringComparer.Ordinal))
                return false;
            var actualManifestHash = Hash(JsonSerializer.SerializeToUtf8Bytes(document with { IntegrityHash = null }, JsonOptions));
            if (!string.Equals(actualManifestHash, document.IntegrityHash, StringComparison.Ordinal)) return false;
            foreach (var name in ArtifactNames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = ResolveArtifactPath(directory, name);
                if (!File.Exists(path) || Hash(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false)) != document.Files[name])
                    return false;
            }
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    public async Task<RecoveryArtifactSummary> ReadSummaryAsync(DryRunId dryRunId, CancellationToken cancellationToken)
    {
        if (!await VerifyIntegrityAsync(dryRunId, cancellationToken).ConfigureAwait(false))
            throw new InvalidDataException("Recovery artifact integrity verification failed.");
        var directory = GetDirectory(dryRunId);
        var qualification = await ReadAsync<QualificationDocument>(directory, "qualification.json", cancellationToken).ConfigureAwait(false);
        var assessment = await ReadAsync<AssessmentDocument>(directory, "assessment.json", cancellationToken).ConfigureAwait(false);
        var rehearsal = await ReadAsync<RehearsalDocument>(directory, "rehearsal.json", cancellationToken).ConfigureAwait(false);
        return new RecoveryArtifactSummary(dryRunId, new RunId(Guid.ParseExact(assessment.VerificationRunId, "D")),
            assessment.ConfigurationHash, assessment.GraphHash, assessment.CheckpointManifestHash,
            assessment.SourceFingerprint, assessment.ProjectionFingerprint, assessment.RuleSetFingerprint,
            Enum.Parse<DryRunQualificationStatus>(qualification.Status, ignoreCase: true),
            qualification.DryRunFingerprint, qualification.PolicyFingerprint, qualification.AssessmentFingerprint,
            qualification.PlanFingerprint, qualification.RehearsalFingerprint, qualification.VerificationEvidenceFingerprint,
            qualification.RecoveryEvidenceFingerprint, assessment.ExecutedEdges,
            assessment.ReverseEdges, assessment.RestoreEdges, assessment.CompensateEdges, assessment.IrreversibleEdges,
            assessment.Edges.LongCount(edge => string.Equals(edge.ConfiguredMode, nameof(RecoveryMode.Reverse), StringComparison.Ordinal) && edge.IsLossy),
            assessment.AffectedArtifacts, assessment.RecoverableArtifacts, assessment.IrrecoverableArtifacts,
            assessment.UnknownArtifacts, assessment.RecoverablePercentage,
            assessment.FailedEdges, rehearsal.Outcome,
            qualification.Reasons.Select(reason => $"{reason.Code}: {reason.Message}").ToArray(),
            assessment.SemanticTypeCoverage.Select(item => new RecoverySemanticTypeCoverage(item.SemanticType,
                item.AffectedArtifacts, item.RecoverableArtifacts, item.RecoverablePercentage)));
    }

    private string GetDirectory(DryRunId id)
    {
        if (id.Value == Guid.Empty) throw new ArgumentException("Dry-run ID must not be empty.", nameof(id));
        var directory = Path.GetFullPath(Path.Combine(_root, id.Value.ToString("N", CultureInfo.InvariantCulture)));
        var relative = Path.GetRelativePath(_root, directory);
        if (Path.IsPathRooted(relative) || relative.StartsWith("..", StringComparison.Ordinal))
            throw new InvalidOperationException("Recovery artifact path is outside its configured store.");
        return directory;
    }

    private static string ResolveArtifactPath(string directory, string name)
    {
        if (Path.GetFileName(name) != name) throw new InvalidOperationException("Recovery artifact name is invalid.");
        var path = Path.GetFullPath(Path.Combine(directory, name));
        if (!string.Equals(Path.GetDirectoryName(path), Path.GetFullPath(directory),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("Recovery artifact path escaped its run directory.");
        return path;
    }

    private static async Task WriteAtomicAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        var temporary = path + $".{Guid.NewGuid():N}.partial";
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporary, path, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void RestrictDirectory(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static async Task<T> ReadAsync<T>(string directory, string name, CancellationToken cancellationToken) where T : class
    {
        await using var stream = File.OpenRead(Path.Combine(directory, name));
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Recovery artifact is empty.");
    }

    private static AssessmentDocument ToDocument(RecoveryAssessment assessment) => new(
        "proofshift-recovery-assessment-v2", RecoveryAssessment.FingerprintVersion,
        EffectiveRecoveryPolicy.FingerprintVersion, assessment.Id.Value.ToString("D", CultureInfo.InvariantCulture),
        assessment.State.ToString(), assessment.Outcome.ToString(),
        assessment.Binding.VerificationRunId.Value.ToString("D", CultureInfo.InvariantCulture),
        assessment.Binding.ConfigurationHash, assessment.Binding.GraphHash,
        assessment.Binding.CheckpointId.Value.ToString("D", CultureInfo.InvariantCulture),
        assessment.Binding.CheckpointManifestHash, assessment.Binding.SourceFingerprint,
        assessment.Binding.ProjectionRunId.Value.ToString("D", CultureInfo.InvariantCulture),
        assessment.Binding.ProjectionManifestHash, assessment.Binding.ProjectionFingerprint,
        assessment.Binding.RuleSetFingerprint, assessment.Binding.EvidenceFingerprint,
        assessment.PolicyFingerprint, assessment.Fingerprint,
        assessment.Coverage.ExecutedEdges, assessment.Coverage.ReverseEdges, assessment.Coverage.RestoreEdges,
        assessment.Coverage.CompensateEdges, assessment.Coverage.IrreversibleEdges, assessment.Coverage.ValidatedEdges,
        assessment.Coverage.FailedEdges, assessment.Coverage.AffectedArtifacts, assessment.Coverage.RecoverableArtifacts,
        assessment.Coverage.IrrecoverableArtifacts, assessment.Coverage.UnknownArtifacts, assessment.Coverage.RecoverablePercentage,
        assessment.Coverage.BySemanticType.Values.Select(item => new SemanticCoverageDocument(item.SemanticType,
            item.AffectedArtifacts, item.RecoverableArtifacts, item.RecoverablePercentage)).ToArray(),
        assessment.Edges.Select(edge => new EdgeDocument(edge.EdgeId.Value.ToString("D", CultureInfo.InvariantCulture), edge.EdgeName,
            edge.Operation.ToString(), edge.IsDestructive, edge.AffectedSemanticTypes.ToArray(),
            edge.ConfiguredMode?.ToString(), edge.Strategy, edge.Result.ToString(),
            edge.CapabilityAvailable, edge.CapabilityValidated, edge.IsLossy, edge.Risk.ToString(), edge.ValidationMode.ToString(),
            edge.AffectedSourceArtifacts, edge.AffectedTargetArtifacts, edge.Issues.Select(ToDocument).ToArray())).ToArray(),
        assessment.VerificationLedger, assessment.Issues.Select(ToDocument).ToArray(),
        assessment.StartedAt, assessment.CompletedAt);

    private static PlanDocument ToDocument(RecoveryPlan plan) => new(
        "proofshift-recovery-plan-v2", RecoveryPlan.FingerprintVersion, plan.Id.Value.ToString("D", CultureInfo.InvariantCulture),
        plan.VerificationRunId.Value.ToString("D", CultureInfo.InvariantCulture),
        plan.SourceCheckpointId.Value.ToString("D", CultureInfo.InvariantCulture),
        plan.TargetRecoveryCheckpointIds.Select(id => id.Value.ToString("D", CultureInfo.InvariantCulture)).ToArray(),
        plan.PolicyFingerprint, plan.Fingerprint, plan.Preconditions.ToArray(),
        plan.Steps.Select(step => new PlanStepDocument(step.Sequence, step.EdgeId.Value.ToString("D", CultureInfo.InvariantCulture),
            step.EdgeName, step.SystemId.Value, step.EndpointId.Value, step.Operation, step.Mode?.ToString() ?? "missing", step.Strategy,
            step.AffectedSourceArtifacts, step.AffectedTargetArtifacts,
            step.Preconditions, step.ExpectedOutcome, step.ValidationMethod)).ToArray(),
        plan.IrreversibleRisks.Select(ToDocument).ToArray());

    private static RehearsalDocument ToDocument(RecoveryRehearsal rehearsal) => new(
        "proofshift-recovery-rehearsal-v1", RecoveryRehearsal.FingerprintVersion,
        RecoveryRehearsal.Environment, rehearsal.Id.Value.ToString("D", CultureInfo.InvariantCulture),
        rehearsal.State.ToString(), rehearsal.Outcome.ToString(),
        rehearsal.VerificationRunId.Value.ToString("D", CultureInfo.InvariantCulture), rehearsal.BaselineFingerprint,
        rehearsal.RestoredFingerprint, rehearsal.ShadowCleanupFingerprint, rehearsal.Fingerprint, rehearsal.MutatedArtifacts,
        rehearsal.Checkpoints.Select(checkpoint => new CheckpointDocument(checkpoint.Id.Value.ToString("D", CultureInfo.InvariantCulture),
            checkpoint.ShadowRunId.Value.ToString("D", CultureInfo.InvariantCulture), checkpoint.SystemId.Value,
            checkpoint.EndpointId.Value, checkpoint.ConnectorId.Value, checkpoint.ConnectorVersion,
            checkpoint.GraphHash, checkpoint.BaselineFingerprint, checkpoint.TargetArtifactCount,
            checkpoint.Reference, checkpoint.ContentSha256, checkpoint.CapturedAt)).ToArray(),
        rehearsal.Issues.Select(ToDocument).ToArray(), rehearsal.StartedAt, rehearsal.CompletedAt);

    private static RecoveryEvidenceDocument ToDocument(RecoveryEvidenceGraph recoveryEvidence) => new(
        RecoveryEvidenceGraph.FormatVersion, recoveryEvidence.Graph.VerificationRunId.Value.ToString("D", CultureInfo.InvariantCulture),
        recoveryEvidence.Graph.CanonicalizationVersion, recoveryEvidence.VerificationEvidenceFingerprint,
        recoveryEvidence.Graph.Fingerprint, recoveryEvidence.Fingerprint,
        recoveryEvidence.Graph.Records.Select(record => new RecoveryEvidenceRecordDocument(
            record.Id.Value.ToString("D", CultureInfo.InvariantCulture), record.Type.ToString(), record.RuleId.Value,
            record.RuleVersion, record.Result.ToString(), record.Severity.ToString(), record.Code, record.Explanation,
            record.Inputs.Select(ToDocument).ToArray(), ToValueHash(record.Expected), ToValueHash(record.Actual))).ToArray());

    private static QualificationDocument ToDocument(DryRunQualification qualification) => new(
        "proofshift-dry-run-record-v1", DryRunQualification.FingerprintVersion,
        qualification.Id.Value.ToString("D", CultureInfo.InvariantCulture), qualification.Status.ToString(),
        qualification.DryRunFingerprint, qualification.PolicyFingerprint, qualification.RecoveryAssessmentFingerprint,
        qualification.RecoveryPlanFingerprint, qualification.RecoveryRehearsalFingerprint,
        qualification.CheckpointFingerprint, qualification.ProjectionFingerprint, qualification.RuleSetFingerprint,
        qualification.VerificationEvidenceFingerprint, qualification.RecoveryEvidenceFingerprint,
        qualification.Reasons.Select(ToDocument).ToArray());

    private static IssueDocument ToDocument(RecoveryAssessmentIssue issue) => new(issue.Code, issue.Message,
        issue.EdgeId?.Value.ToString("D", CultureInfo.InvariantCulture), issue.ArtifactId?.Value,
        issue.GraphNodeId?.Value.ToString("D", CultureInfo.InvariantCulture));

    private static ReferenceDocument ToDocument(EvidenceReference reference) => new(
        reference.ArtifactId?.Value, reference.GraphNodeId?.Value.ToString("D", CultureInfo.InvariantCulture),
        reference.EvidenceId?.Value.ToString("D", CultureInfo.InvariantCulture), reference.RuleId?.Value,
        reference.MigrationEdgeId?.Value.ToString("D", CultureInfo.InvariantCulture),
        reference.RunId?.Value.ToString("D", CultureInfo.InvariantCulture),
        reference.CheckpointId?.Value.ToString("D", CultureInfo.InvariantCulture),
        reference.ProjectionRunId?.Value.ToString("D", CultureInfo.InvariantCulture));

    private static string? ToValueHash(EvidenceValue? value) => value is null ? null : "sha256:" + Hash(Encoding.UTF8.GetBytes(CanonicalValue(value.Value)));

    private static string CanonicalValue(ValueNode value) => value switch
    {
        NullValue => "null",
        StringValue text => "string:" + text.Value.Normalize(NormalizationForm.FormC),
        IntegerValue integer => "integer:" + integer.Value.ToString(CultureInfo.InvariantCulture),
        DecimalValue number => "decimal:" + number.Value.ToString("G29", CultureInfo.InvariantCulture),
        BooleanValue boolean => boolean.Value ? "boolean:true" : "boolean:false",
        DateValue date => "date:" + date.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        InstantValue instant => "instant:" + instant.Value.ToString("O", CultureInfo.InvariantCulture),
        OffsetDateTimeValue offset => "offset:" + offset.Value.ToString("O", CultureInfo.InvariantCulture),
        LocalDateTimeValue local => "local:" + local.Value.ToString("O", CultureInfo.InvariantCulture),
        BinaryReferenceValue binary => $"binary:{binary.ContentLength.ToString(CultureInfo.InvariantCulture)}:{binary.Sha256}",
        CollectionValue collection => "collection:[" + string.Join(',', collection.Values.Select(CanonicalValue)) + "]",
        ObjectValue obj => "object:{" + string.Join(',', obj.Values.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => pair.Key + ":" + CanonicalValue(pair.Value))) + "}",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static void TryDeleteDirectory(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
        catch { }
    }

    private sealed record ManifestDocument(string Format, string DryRunId, SortedDictionary<string, string> Files, string? IntegrityHash);
    private sealed record AssessmentDocument(string Format, string FingerprintVersion, string PolicyFingerprintVersion,
        string Id, string State, string Outcome, string VerificationRunId,
        string ConfigurationHash, string GraphHash, string CheckpointId, string CheckpointManifestHash,
        string SourceFingerprint, string ProjectionRunId, string ProjectionManifestHash, string ProjectionFingerprint,
        string RuleSetFingerprint, string EvidenceFingerprint, string PolicyFingerprint, string Fingerprint,
        long ExecutedEdges, long ReverseEdges, long RestoreEdges, long CompensateEdges, long IrreversibleEdges,
        long ValidatedEdges, long FailedEdges, long AffectedArtifacts, long RecoverableArtifacts,
        long IrrecoverableArtifacts, long UnknownArtifacts, decimal? RecoverablePercentage,
        SemanticCoverageDocument[] SemanticTypeCoverage,
        EdgeDocument[] Edges, ProofShift.Verification.VerificationLedgerStoreReceipt VerificationLedger,
        IssueDocument[] Issues,
        DateTimeOffset StartedAt, DateTimeOffset CompletedAt);
    private sealed record EdgeDocument(string EdgeId, string EdgeName, string Operation, bool IsDestructive,
        string[] AffectedSemanticTypes, string? ConfiguredMode, string? Strategy, string Result,
        bool CapabilityAvailable, bool CapabilityValidated, bool IsLossy,
        string Risk, string ValidationMode, long AffectedSourceArtifacts, long AffectedTargetArtifacts, IssueDocument[] Issues);
    private sealed record SemanticCoverageDocument(string SemanticType, long AffectedArtifacts, long RecoverableArtifacts,
        decimal? RecoverablePercentage);
    private sealed record ArtifactCoverageDocument(string NodeId, string ArtifactId, string? SemanticType,
        string[] EdgePath, string[] RecoveryModes, bool Covered, bool Recoverable,
        bool ApprovedIrreversible, string Reason);
    private sealed record PlanDocument(string Format, string FingerprintVersion, string Id, string VerificationRunId,
        string SourceCheckpointId, string[] TargetRecoveryCheckpointIds,
        string PolicyFingerprint, string Fingerprint,
        string[] Preconditions, PlanStepDocument[] Steps, IssueDocument[] IrreversibleRisks);
    private sealed record PlanStepDocument(int Sequence, string EdgeId, string EdgeName, string SystemId, string EndpointId,
        string Operation, string Mode, string Strategy, long AffectedSourceArtifacts, long AffectedTargetArtifacts,
        string Preconditions, string ExpectedOutcome, string ValidationMethod);
    private sealed record RehearsalDocument(string Format, string FingerprintVersion, string Environment,
        string Id, string State,
        string Outcome, string VerificationRunId,
        string BaselineFingerprint, string? RestoredFingerprint, string? ShadowCleanupFingerprint,
        string Fingerprint, long MutatedArtifacts,
        CheckpointDocument[] Checkpoints, IssueDocument[] Issues, DateTimeOffset StartedAt, DateTimeOffset CompletedAt);
    private sealed record CheckpointDocument(string Id, string ShadowRunId, string SystemId, string EndpointId,
        string ConnectorId, string ConnectorVersion, string GraphHash, string BaselineFingerprint,
        long TargetArtifactCount, string Reference, string ContentSha256, DateTimeOffset CapturedAt);
    private sealed record RecoveryEvidenceDocument(string FormatVersion, string RunId, string CanonicalizationVersion,
        string VerificationEvidenceFingerprint, string RecoveryGraphFingerprint, string Fingerprint,
        RecoveryEvidenceRecordDocument[] Records);
    private sealed record RecoveryEvidenceRecordDocument(string Id, string Type, string RuleId, string RuleVersion,
        string Result, string Severity, string? Code, string Explanation, ReferenceDocument[] Inputs,
        string? ExpectedFingerprint, string? ActualFingerprint);
    private sealed record QualificationDocument(string Format, string FingerprintVersion, string Id, string Status, string DryRunFingerprint,
        string PolicyFingerprint, string AssessmentFingerprint, string PlanFingerprint, string RehearsalFingerprint,
        string CheckpointFingerprint, string ProjectionFingerprint, string RuleSetFingerprint,
        string VerificationEvidenceFingerprint, string RecoveryEvidenceFingerprint, IssueDocument[] Reasons);
    private sealed record IssueDocument(string Code, string Message, string? EdgeId, string? ArtifactId, string? GraphNodeId);
    private sealed record ReferenceDocument(string? ArtifactId, string? GraphNodeId, string? EvidenceId, string? RuleId,
        string? MigrationEdgeId, string? RunId, string? CheckpointId, string? ProjectionRunId);
}
