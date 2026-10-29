using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ProofShift.Connectors.Abstractions;
using ProofShift.Evidence;
using ProofShift.Projection;
using ProofShift.Recovery;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("ProofShift.EndToEnd.Tests")]

namespace ProofShift.Cli;

internal sealed record WorkspaceEvidencePackageArtifact(
    string Role,
    string Classification,
    string? Path,
    string? Sha256,
    long? Length,
    string? ExternalReference,
    string Reason,
    bool Required);

internal sealed record WorkspaceEvidencePackageManifest
{
    public const string CurrentFormat = "proofshift-evidence-package-v1";

    public string Format { get; init; } = CurrentFormat;
    public required string PackageType { get; init; }
    public required Guid RunId { get; init; }
    public required string RunManifestIdentityFingerprint { get; init; }
    public required string RunManifestHash { get; init; }
    public required string ProductVersion { get; init; }
    public string? SourceRevision { get; init; }
    public string? SourceTree { get; init; }
    public string? PlanFingerprint { get; init; }
    public required string ExecutionState { get; init; }
    public string? Disposition { get; init; }
    public required IReadOnlyList<WorkspaceEvidencePackageArtifact> Artifacts { get; init; }
    public string PackageIdentity { get; set; } = string.Empty;
    public string ManifestIntegrityHash { get; set; } = string.Empty;
}

internal sealed record WorkspaceEvidencePackageValidation(
    bool Valid,
    string? PackageIdentity,
    Guid? RunId,
    string? PackageType,
    string? ExecutionState,
    string? Disposition,
    int EmbeddedArtifactCount,
    int ExternalReferenceCount,
    int OmittedArtifactCount,
    IReadOnlyList<string> Failures);

internal sealed record WorkspaceEvidencePackageCreation(
    string PackagePath,
    string PackageIdentity,
    string? ArchiveSha256,
    Guid RunId,
    string PackageType,
    string ExecutionState,
    string? Disposition,
    int EmbeddedArtifactCount,
    int ExternalReferenceCount,
    int OmittedArtifactCount);

internal static class WorkspaceEvidencePackageService
{
    private const string PackageManifestName = "package.json";
    private const long MaximumManifestLength = 2 * 1024 * 1024;
    private const int MaximumArtifactCount = 256;
    private const int MaximumArchiveEntryCount = MaximumArtifactCount + 1;
    private static readonly string[] RecoveryArtifactNames =
        ["assessment.json", "plan.json", "recovery-evidence.json", "qualification.json", "rehearsal.json"];
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };
    private static readonly JsonSerializerOptions ArtifactJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static async Task<WorkspaceEvidencePackageCreation> CreateAsync(string workspaceDirectory,
        string runReference, string? outputPath, bool diagnostic, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(workspaceDirectory);
        var validation = await EvaluationWorkspaceManifestStore.ValidateAsync(root).ConfigureAwait(false);
        if (!validation.Valid)
            throw new InvalidDataException("Workspace must pass offline structural validation before packaging.");
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
        if (run.RunId != runId || run.WorkspaceId != workspace.WorkspaceId)
            throw new InvalidDataException("Run manifest identity does not match the selected workspace and run.");

        var completedDisposition = run.State == "Completed" &&
            run.Disposition is "QUALIFIED" or "NOT QUALIFIED";
        if (!completedDisposition && !diagnostic)
            throw new InvalidOperationException("Only a completed QUALIFIED or NOT QUALIFIED run can be packaged as final evidence; use --diagnostic for an incomplete or unqualified run.");
        if (run.Disposition is not (null or "QUALIFIED" or "NOT QUALIFIED"))
            throw new InvalidDataException("Run contains an unsupported business disposition.");
        if (run.State != "Completed" && run.Disposition is not null)
            throw new InvalidDataException("An incomplete run cannot claim a business disposition.");

        var planResult = await WorkspaceEvaluationService.PlanAsync(root, cancellationToken).ConfigureAwait(false);
        var plan = planResult.Plan
            ?? throw new InvalidDataException("A safe effective plan could not be generated for this run.");
        if (!string.Equals(run.PlanFingerprint, plan.Fingerprint, StringComparison.Ordinal) ||
            !string.Equals(run.GraphHash, plan.GraphHash, StringComparison.Ordinal))
            throw new InvalidDataException("Current workspace plan does not match the completed run; refusing to package changed configuration.");

        var candidates = new List<PackageArtifactSource>();
        candidates.Add(PackageArtifactSource.FromBytes("run-manifest", "artifacts/run.json",
            await File.ReadAllBytesAsync(Path.Combine(runDirectory, "run.json"), cancellationToken).ConfigureAwait(false),
            "Authoritative B.3 execution record.", required: true));
        candidates.Add(PackageArtifactSource.FromBytes("effective-plan", "artifacts/effective-plan.json",
            JsonSerializer.SerializeToUtf8Bytes(plan, JsonOptions),
            "Safe effective plan regenerated from the workspace and matched to the run fingerprint.", required: true));
        var summaryContent = await WorkspaceEvaluationSummaryService.CreateContentAsync(runDirectory, run, plan,
            cancellationToken).ConfigureAwait(false);
        var summaryPath = Path.Combine(runDirectory, "summary.md");
        if (File.Exists(summaryPath) &&
            !string.Equals(await File.ReadAllTextAsync(summaryPath, cancellationToken).ConfigureAwait(false),
                summaryContent, StringComparison.Ordinal))
            throw new InvalidDataException("Saved evaluation summary disagrees with authoritative run and Evidence data.");
        candidates.Add(PackageArtifactSource.FromBytes(WorkspaceEvaluationSummaryService.SummaryRole,
            WorkspaceEvaluationSummaryService.SummaryPath, Encoding.UTF8.GetBytes(summaryContent),
            "Deterministic human-readable projection bound to the authoritative run, plan, and Evidence.", required: true));

        var runRelative = NormalizeRelative(Path.GetRelativePath(root, runDirectory).Replace('\\', '/'));
        var artifactDeclarations = new List<WorkspaceEvidencePackageArtifact>();

        if (run.EvidenceReference is { } evidenceReference)
        {
            var evidenceManifestPath = ResolveRunReference(root, runDirectory, evidenceReference);
            var evidenceDirectory = Path.GetDirectoryName(evidenceManifestPath)!;
            var evidenceRoot = Path.GetDirectoryName(evidenceDirectory)!;
            if (!Guid.TryParse(run.VerificationRunId, out var verificationId) || verificationId == Guid.Empty)
                throw new InvalidDataException("Evidence reference has no valid Verification run identity.");
            var evidenceStore = new FileSystemEvidenceStore(evidenceRoot);
            if (!await evidenceStore.VerifyIntegrityAsync(new ProofShift.Domain.RunId(verificationId), cancellationToken)
                    .ConfigureAwait(false))
                throw new InvalidDataException("Run Evidence failed its existing integrity validation.");
            var evidenceManifest = await evidenceStore.ReadManifestAsync(
                new ProofShift.Domain.RunId(verificationId), cancellationToken).ConfigureAwait(false);
            if (!string.Equals(evidenceManifest.IntegrityHash, run.EvidenceManifestIntegrityHash, StringComparison.Ordinal) ||
                evidenceManifest.Fingerprint != run.VerificationEvidenceFingerprint)
                throw new InvalidDataException("Evidence store identity does not match the run manifest.");
            var segmentPath = Path.Combine(evidenceDirectory, evidenceManifest.SegmentFile);
            candidates.Add(PackageArtifactSource.FromFile("evidence-store-manifest", "artifacts/evidence/manifest.json",
                evidenceManifestPath, "Existing integrity-verified Evidence store manifest.", required: true));
            candidates.Add(PackageArtifactSource.FromFile("evidence-records", "artifacts/evidence/evidence.ndjson",
                segmentPath, "Evidence records, including finding values and provenance references.", required: true));
        }
        else if (completedDisposition)
        {
            throw new InvalidDataException("A completed business evaluation has no persisted Evidence reference.");
        }

        if (run.ProjectionManifestReference is { } projectionReference)
        {
            var projectionPath = ResolveRunReference(root, runDirectory, projectionReference);
            var projectionBytes = await File.ReadAllBytesAsync(projectionPath, cancellationToken).ConfigureAwait(false);
            if (!Guid.TryParse(run.ProjectionRunId, out var projectionRunId))
                throw new InvalidDataException("Projection reference has no valid Projection run identity.");
            var projection = ProjectionRunManifestStore.ReadAndValidate(projectionBytes,
                new ProofShift.Domain.RunId(projectionRunId));
            if (projection.ManifestHash != run.ProjectionManifestHash ||
                projection.ProjectionFingerprint != run.ProjectionFingerprint ||
                projection.CheckpointId != run.CheckpointId ||
                projection.CheckpointManifestHash != run.CheckpointManifestHash)
                throw new InvalidDataException("Projection manifest does not match the run's bound identities.");
            var journalPath = ResolveRunReference(root, runDirectory,
                NormalizeWorkspaceReference(runRelative, projection.JournalPath));
            candidates.Add(PackageArtifactSource.FromFile("projection-manifest", "artifacts/projection/run.json",
                projectionPath, "Projection identity and checkpoint/projection binding.", required: true));
            candidates.Add(PackageArtifactSource.FromFile("projection-journal", "artifacts/projection/journal.ndjson",
                journalPath, "Projection source-to-target lineage journal; contains artifact identities, not raw records.", required: true));
        }
        else if (completedDisposition && run.RecoveryRunId is not null)
        {
            throw new InvalidDataException("A completed shadow evaluation has no Projection manifest reference.");
        }

        if (run.RecoveryArtifactReference is { } recoveryReference)
        {
            var recoveryManifestPath = ResolveRunReference(root, runDirectory, recoveryReference);
            var recoveryBytes = await File.ReadAllBytesAsync(recoveryManifestPath, cancellationToken).ConfigureAwait(false);
            using var recoveryJson = JsonDocument.Parse(recoveryBytes);
            var recoveryRoot = recoveryJson.RootElement;
            if (recoveryRoot.GetProperty("format").GetString() != FileSystemRecoveryArtifactStore.StoreFormatVersion ||
                recoveryRoot.GetProperty("dryRunId").GetString() != run.RecoveryRunId ||
                recoveryRoot.GetProperty("integrityHash").GetString() != run.RecoveryArtifactIntegrityHash)
                throw new InvalidDataException("Recovery artifact manifest does not match the run's identity or integrity hash.");
            var recoveryFiles = recoveryRoot.GetProperty("files");
            var recoveryNames = recoveryFiles.EnumerateObject().Select(property => property.Name)
                .Order(StringComparer.Ordinal).ToArray();
            var expectedRecoveryNames = RecoveryArtifactNames.Order(StringComparer.Ordinal).ToArray();
            if (!recoveryNames.SequenceEqual(expectedRecoveryNames, StringComparer.Ordinal))
                throw new InvalidDataException("Recovery manifest has an unsupported artifact set.");
            foreach (var name in recoveryNames)
            {
                var recoveryDirectoryRelative = Path.GetRelativePath(root, Path.GetDirectoryName(recoveryManifestPath)!)
                    .Replace('\\', '/');
                var artifactPath = ResolveRunReference(root, runDirectory,
                    NormalizeWorkspaceReference(runRelative, $"{recoveryDirectoryRelative[(runRelative.Length + 1)..]}/{name}"));
                var actualHash = await HashFileAsync(artifactPath, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(actualHash.Hash, recoveryFiles.GetProperty(name).GetString(), StringComparison.Ordinal))
                    throw new InvalidDataException($"Recovery artifact '{name}' failed its existing integrity hash.");
                candidates.Add(PackageArtifactSource.FromFile($"recovery-{Path.GetFileNameWithoutExtension(name)}",
                    $"artifacts/recovery/{name}", artifactPath, "Recovery assessment, rehearsal, qualification, or evidence artifact.", required: true));
            }
            candidates.Add(PackageArtifactSource.FromFile("recovery-manifest", "artifacts/recovery/manifest.json",
                recoveryManifestPath, "Existing integrity-verified Recovery artifact index.", required: true));
        }
        else if (completedDisposition && run.RecoveryQualification is not null)
        {
            throw new InvalidDataException("A completed qualified evaluation has no Recovery artifact reference.");
        }

        AddExternal(artifactDeclarations, "checkpoint-manifest", run.CheckpointManifestHash,
            run.CheckpointId is null ? null : $"checkpoint:{run.CheckpointId}",
            "Immutable checkpoint metadata remains in the workspace checkpoint store; its manifest hash is bound by the run.");
        AddOmitted(artifactDeclarations, "checkpoint-payload", null, "Raw checkpoint rows and binary payloads are intentionally not copied.");
        AddExternal(artifactDeclarations, "verification-ledger", run.VerificationLedgerFingerprint,
            run.VerificationLedgerReference is null || run.VerificationRunId is null
                ? null : $"verification-ledger:{run.VerificationRunId}",
            "The potentially large working ledger remains an external run artifact; Evidence records are embedded for offline review.");
        AddOmitted(artifactDeclarations, "source-data", null, "Raw source rows and customer files are intentionally excluded.");
        AddOmitted(artifactDeclarations, "projected-target-data", run.ProjectionFingerprint,
            "Materialized shadow target rows are intentionally excluded; projection identity and lineage journal are embedded.");
        AddOmitted(artifactDeclarations, "resolved-configuration", run.ConfigurationHash,
            "Resolved configuration and credentials are not embedded; the safe effective plan and configuration fingerprint are included.");
        AddOmitted(artifactDeclarations, "graph-source-mapping", run.GraphHash,
            "The compiled graph and mapping definitions are represented by the safe effective plan and graph fingerprint.");
        artifactDeclarations.AddRange(await DescribeCandidatesAsync(candidates, cancellationToken).ConfigureAwait(false));

        var packageType = completedDisposition ? "final" : "diagnostic";
        var manifest = new WorkspaceEvidencePackageManifest
        {
            PackageType = packageType,
            RunId = run.RunId,
            RunManifestIdentityFingerprint = run.IdentityFingerprint,
            RunManifestHash = run.ManifestHash!,
            ProductVersion = run.ProductVersion,
            SourceRevision = run.SourceRevision,
            SourceTree = run.SourceTree,
            PlanFingerprint = run.PlanFingerprint,
            ExecutionState = run.State,
            Disposition = run.Disposition,
            Artifacts = artifactDeclarations
                .OrderBy(item => item.Role, StringComparer.Ordinal)
                .ThenBy(item => item.Path, StringComparer.Ordinal)
                .ToArray()
        };
        manifest.PackageIdentity = ComputePackageIdentity(manifest);
        manifest.ManifestIntegrityHash = Hash(JsonSerializer.SerializeToUtf8Bytes(
            manifest with { ManifestIntegrityHash = string.Empty }, JsonOptions));
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);

        var evidenceDirectoryPath = ResolveContained(root, workspace.Paths.Evidence);
        var destination = outputPath is null
            ? Path.Combine(evidenceDirectoryPath, $"{runId:D}.zip")
            : Path.GetFullPath(outputPath);
        if (IsContained(runDirectory, destination))
            throw new ArgumentException("Evidence package output must be outside the authoritative run directory.", nameof(outputPath));
        var destinationDirectory = Path.GetDirectoryName(destination)
            ?? throw new ArgumentException("Evidence package output must include a parent directory.", nameof(outputPath));
        Directory.CreateDirectory(destinationDirectory);
        var temporary = Path.Combine(destinationDirectory, $".{Path.GetFileName(destination)}-{Guid.NewGuid():N}.partial");
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                using (var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true))
                {
                    foreach (var candidate in candidates.OrderBy(item => item.ArchivePath, StringComparer.Ordinal))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var entry = archive.CreateEntry(candidate.ArchivePath, CompressionLevel.Optimal);
                        entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                        await using var target = entry.Open();
                        await candidate.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
                    }

                    var manifestEntry = archive.CreateEntry(PackageManifestName, CompressionLevel.Optimal);
                    manifestEntry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                    await using var manifestStream = manifestEntry.Open();
                    await manifestStream.WriteAsync(manifestBytes, cancellationToken).ConfigureAwait(false);
                }
                await file.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }

        var archiveHash = await HashFileAsync(destination, cancellationToken).ConfigureAwait(false);
        return new WorkspaceEvidencePackageCreation(destination, manifest.PackageIdentity, archiveHash.Hash,
            manifest.RunId, manifest.PackageType, manifest.ExecutionState, manifest.Disposition,
            manifest.Artifacts.Count(item => item.Classification == "embedded"),
            manifest.Artifacts.Count(item => item.Classification == "external-reference"),
            manifest.Artifacts.Count(item => item.Classification == "omitted"));
    }

    public static async Task<WorkspaceEvidencePackageValidation> ValidateAsync(string packagePath,
        CancellationToken cancellationToken)
    {
        var failures = new List<string>();
        WorkspaceEvidencePackageManifest? manifest = null;
        try
        {
            await using var package = new FileStream(Path.GetFullPath(packagePath), FileMode.Open, FileAccess.Read,
                FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var zip = new ZipArchive(package, ZipArchiveMode.Read, leaveOpen: false);
            var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
            if (zip.Entries.Count > MaximumArchiveEntryCount)
                return Invalid(["Evidence package contains too many archive entries."]);
            foreach (var entry in zip.Entries)
            {
                if (!IsSafeArchivePath(entry.FullName) || IsSymbolicLink(entry))
                    failures.Add($"Unsafe archive entry path or link: {entry.FullName}");
                if (entry.FullName.EndsWith('/') ||
                    !entries.TryAdd(entry.FullName, entry))
                    failures.Add($"Duplicate, directory, or colliding archive entry: {entry.FullName}");
            }

            if (failures.Count > 0) return Invalid(failures);
            if (!entries.TryGetValue(PackageManifestName, out var packageManifestEntry) ||
                packageManifestEntry.Length > MaximumManifestLength)
                return Invalid(["Package manifest is missing or exceeds the supported size."]);
            await using (var manifestStream = packageManifestEntry.Open())
            {
                using var buffer = new MemoryStream();
                await manifestStream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
                manifest = JsonSerializer.Deserialize<WorkspaceEvidencePackageManifest>(buffer.ToArray(), JsonOptions);
            }

            if (manifest is null || manifest.Format != WorkspaceEvidencePackageManifest.CurrentFormat ||
                manifest.RunId == Guid.Empty || manifest.PackageType is not ("final" or "diagnostic") ||
                manifest.Artifacts is null || manifest.ManifestIntegrityHash is null ||
                manifest.PackageIdentity is null || manifest.ManifestIntegrityHash.Length != 64 ||
                manifest.PackageIdentity.Length != 64 || manifest.Artifacts.Count > MaximumArtifactCount ||
                manifest.Artifacts.Any(item => item is null))
                return Invalid(["Package manifest format, required fields, or integrity identity is invalid."], manifest);

            var expectedManifestHash = Hash(JsonSerializer.SerializeToUtf8Bytes(
                manifest with { ManifestIntegrityHash = string.Empty }, JsonOptions));
            if (!string.Equals(expectedManifestHash, manifest.ManifestIntegrityHash, StringComparison.Ordinal))
                failures.Add("Package manifest integrity hash does not match.");
            if (!string.Equals(ComputePackageIdentity(manifest), manifest.PackageIdentity, StringComparison.Ordinal))
                failures.Add("Logical package identity does not match the manifest contents.");

            var artifactPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var roles = new HashSet<string>(StringComparer.Ordinal);
            foreach (var artifact in manifest.Artifacts)
            {
                if (artifact is null || string.IsNullOrWhiteSpace(artifact.Role) ||
                    string.IsNullOrWhiteSpace(artifact.Classification) || string.IsNullOrWhiteSpace(artifact.Reason))
                {
                    failures.Add("Package contains a malformed artifact declaration.");
                    continue;
                }
                if (!roles.Add(artifact.Role))
                    failures.Add($"Duplicate artifact role: {artifact.Role}");
                var policy = GetArtifactPolicy(artifact.Role);
                if (policy is null)
                {
                    failures.Add($"Unsupported artifact role: {artifact.Role}");
                    continue;
                }
                if (artifact.Classification != policy.Classification || artifact.Required != policy.Required)
                {
                    failures.Add($"Artifact role has an invalid classification or required flag: {artifact.Role}");
                    continue;
                }
                if (artifact.Classification == "embedded")
                {
                    if (artifact.Path is null || !IsSafeArchivePath(artifact.Path) ||
                        artifact.Path.Equals(PackageManifestName, StringComparison.OrdinalIgnoreCase) ||
                        !artifactPaths.Add(artifact.Path) || artifact.Sha256 is null || artifact.Length is null)
                    {
                        failures.Add($"Malformed or colliding embedded artifact declaration: {artifact.Role}");
                        continue;
                    }
                    if (artifact.Required is false)
                        failures.Add($"Embedded artifact is not marked required: {artifact.Role}");
                    if (!entries.TryGetValue(artifact.Path, out var packagedEntry))
                    {
                        failures.Add($"Required embedded artifact is missing: {artifact.Role}");
                        continue;
                    }
                    if (packagedEntry.Length != artifact.Length)
                    {
                        failures.Add($"Embedded artifact size does not match: {artifact.Role}");
                        continue;
                    }
                    var actual = await HashEntryAsync(packagedEntry, artifact.Length.Value, cancellationToken)
                        .ConfigureAwait(false);
                    if (!string.Equals(actual.Hash, artifact.Sha256, StringComparison.Ordinal))
                        failures.Add($"Embedded artifact SHA-256 does not match: {artifact.Role}");
                }
                else if (artifact.Classification == "external-reference")
                {
                    if (artifact.Path is not null || string.IsNullOrWhiteSpace(artifact.ExternalReference))
                        failures.Add($"Malformed external reference declaration: {artifact.Role}");
                }
                else if (artifact.Classification == "omitted")
                {
                    if (artifact.Path is not null || string.IsNullOrWhiteSpace(artifact.Reason))
                        failures.Add($"Malformed omitted artifact declaration: {artifact.Role}");
                }
                else
                {
                    failures.Add($"Unsupported artifact classification: {artifact.Classification}");
                }
            }

            var declaredEntries = artifactPaths.Append(PackageManifestName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var path in entries.Keys)
                if (!declaredEntries.Contains(path))
                    failures.Add($"Undeclared archive entry: {path}");
            if (artifactPaths.Count != manifest.Artifacts.Count(item => item.Classification == "embedded"))
                failures.Add("Package contains duplicate embedded artifact paths.");
            if (failures.Count == 0)
                await ValidateEmbeddedSemanticsAsync(manifest, entries, failures, cancellationToken).ConfigureAwait(false);

            return new WorkspaceEvidencePackageValidation(failures.Count == 0, manifest.PackageIdentity,
                manifest.RunId, manifest.PackageType, manifest.ExecutionState, manifest.Disposition,
                manifest.Artifacts.Count(item => item.Classification == "embedded"),
                manifest.Artifacts.Count(item => item.Classification == "external-reference"),
                manifest.Artifacts.Count(item => item.Classification == "omitted"), failures);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or
            InvalidDataException or ArgumentException or NotSupportedException or ProjectionManifestException or
            KeyNotFoundException or FormatException)
        {
            failures.Add($"Package validation could not complete ({exception.GetType().Name}).");
            return Invalid(failures, manifest);
        }

        static WorkspaceEvidencePackageValidation Invalid(IReadOnlyList<string> issues,
            WorkspaceEvidencePackageManifest? value = null)
        {
            var artifacts = value?.Artifacts ?? [];
            return new(false, value?.PackageIdentity, value?.RunId, value?.PackageType, value?.ExecutionState,
                value?.Disposition, artifacts.Count(item => item?.Classification == "embedded"),
                artifacts.Count(item => item?.Classification == "external-reference"),
                artifacts.Count(item => item?.Classification == "omitted"), issues);
        }
    }

    private static async Task ValidateEmbeddedSemanticsAsync(WorkspaceEvidencePackageManifest package,
        Dictionary<string, ZipArchiveEntry> entries, List<string> failures, CancellationToken cancellationToken)
    {
        var runEntry = package.Artifacts.SingleOrDefault(item => item.Role == "run-manifest");
        if (runEntry?.Path is null || !entries.TryGetValue(runEntry.Path, out var runArchiveEntry))
        {
            failures.Add("Authoritative run manifest is not embedded.");
            return;
        }
        var runBytes = await ReadEntryAsync(runArchiveEntry, MaximumManifestLength, cancellationToken).ConfigureAwait(false);
        var run = WorkspaceRunManifestStore.ReadAndValidate(runBytes);
        if (run.RunId != package.RunId || run.State != package.ExecutionState ||
            run.Disposition != package.Disposition ||
            run.IdentityFingerprint != package.RunManifestIdentityFingerprint ||
            run.ManifestHash != package.RunManifestHash ||
            run.ProductVersion != package.ProductVersion ||
            run.PlanFingerprint != package.PlanFingerprint)
            failures.Add("Embedded run manifest does not match package run metadata.");
        if (package.PackageType == "final" &&
            (run.State != "Completed" || run.Disposition is not ("QUALIFIED" or "NOT QUALIFIED")))
            failures.Add("Final evidence package does not represent a completed business evaluation.");
        if (package.PackageType == "final" &&
            (!package.Artifacts.Any(item => item.Role == "evidence-store-manifest") ||
             !package.Artifacts.Any(item => item.Role == "evidence-records") ||
             !package.Artifacts.Any(item => item.Role == WorkspaceEvaluationSummaryService.SummaryRole)))
            failures.Add("Final evidence package is missing required Evidence artifacts.");
        if (package.PackageType == "final" &&
            (run.EvidenceReference is null || run.ProjectionManifestReference is null ||
             run.RecoveryArtifactReference is null || run.RecoveryQualification is null))
            failures.Add("Final evidence package is missing run-bound Verification, Projection, or Recovery references.");
        if (package.PackageType == "diagnostic" && run.Disposition is not null &&
            run.State != "Completed")
            failures.Add("Diagnostic package contains a disposition on an incomplete run.");
        var checkpointArtifact = package.Artifacts.SingleOrDefault(item => item.Role == "checkpoint-manifest");
        if (run.CheckpointId is null)
        {
            if (checkpointArtifact is not null)
                failures.Add("Package declares a checkpoint that is absent from the run manifest.");
        }
        else if (checkpointArtifact is null || checkpointArtifact.Classification != "external-reference" ||
            checkpointArtifact.ExternalReference != $"checkpoint:{run.CheckpointId}" ||
            checkpointArtifact.Sha256 != run.CheckpointManifestHash)
        {
            failures.Add("Checkpoint external-reference metadata does not match the run manifest.");
        }
        var ledgerArtifact = package.Artifacts.SingleOrDefault(item => item.Role == "verification-ledger");
        if (run.VerificationLedgerReference is null)
        {
            if (ledgerArtifact is not null)
                failures.Add("Package declares a Verification ledger that is absent from the run manifest.");
        }
        else if (ledgerArtifact is null || ledgerArtifact.Classification != "external-reference" ||
            ledgerArtifact.ExternalReference != $"verification-ledger:{run.VerificationRunId}" ||
            ledgerArtifact.Sha256 != run.VerificationLedgerFingerprint)
        {
            failures.Add("Verification ledger external-reference metadata does not match the run manifest.");
        }
        if (package.Artifacts.SingleOrDefault(item => item.Role == "resolved-configuration")?.Sha256 != run.ConfigurationHash ||
            package.Artifacts.SingleOrDefault(item => item.Role == "graph-source-mapping")?.Sha256 != run.GraphHash)
            failures.Add("Omitted configuration or graph metadata does not match the run identity.");

        var planArtifact = package.Artifacts.SingleOrDefault(item => item.Role == "effective-plan");
        if (planArtifact?.Path is null || !entries.TryGetValue(planArtifact.Path, out var planEntry))
        {
            failures.Add("Safe effective plan is not embedded.");
            return;
        }
        using (var planJson = JsonDocument.Parse(await ReadEntryAsync(planEntry, MaximumManifestLength, cancellationToken)
                   .ConfigureAwait(false)))
        {
            if (planJson.RootElement.GetProperty("fingerprint").GetString() != run.PlanFingerprint ||
                planJson.RootElement.GetProperty("graphHash").GetString() != run.GraphHash)
                failures.Add("Embedded effective plan does not match run fingerprints.");
        }
        var summaryArtifact = package.Artifacts.SingleOrDefault(item =>
            item.Role == WorkspaceEvaluationSummaryService.SummaryRole);
        if (summaryArtifact?.Path is null || !entries.TryGetValue(summaryArtifact.Path, out var summaryEntry))
            failures.Add("Run-bound human-readable evaluation summary is not embedded.");
        else
        {
            var summary = Encoding.UTF8.GetString(await ReadEntryAsync(summaryEntry, MaximumManifestLength, cancellationToken)
                .ConfigureAwait(false));
            var summaryMetadata = WorkspaceEvaluationSummaryService.ReadMetadata(summary);
            if (summaryMetadata.RunId != run.RunId ||
                summaryMetadata.RunManifestIdentityFingerprint != run.IdentityFingerprint ||
                summaryMetadata.RunManifestHash != run.ManifestHash ||
                summaryMetadata.PlanFingerprint != run.PlanFingerprint ||
                summaryMetadata.EvidenceFingerprint != run.VerificationEvidenceFingerprint ||
                summaryMetadata.ProductVersion != run.ProductVersion)
                failures.Add("Embedded evaluation summary does not match the authoritative run, plan, or Evidence.");
        }

        var evidenceManifestArtifact = package.Artifacts.SingleOrDefault(item => item.Role == "evidence-store-manifest");
        var evidenceRecordsArtifact = package.Artifacts.SingleOrDefault(item => item.Role == "evidence-records");
        if (run.EvidenceReference is not null)
        {
            if (evidenceManifestArtifact?.Path is null || evidenceRecordsArtifact?.Path is null ||
                !entries.TryGetValue(evidenceManifestArtifact.Path, out var evidenceManifestEntry) ||
                !entries.TryGetValue(evidenceRecordsArtifact.Path, out var evidenceRecordsEntry))
            {
                failures.Add("Run references Evidence that is not completely embedded.");
                return;
            }
            var evidenceManifest = JsonSerializer.Deserialize<EvidenceStoreManifest>(
                await ReadEntryAsync(evidenceManifestEntry, MaximumManifestLength, cancellationToken).ConfigureAwait(false),
                JsonOptions);
            if (evidenceManifest is null || evidenceManifest.Format != EvidenceFormat.StoreFormatVersion ||
                evidenceManifest.CanonicalizationVersion != EvidenceFormat.CanonicalizationVersion ||
                evidenceManifest.RunId != run.VerificationRunId ||
                evidenceManifest.IntegrityHash != run.EvidenceManifestIntegrityHash ||
                evidenceManifest.Fingerprint != run.VerificationEvidenceFingerprint ||
                evidenceManifest.SegmentFile != "evidence.ndjson" || !evidenceManifest.Complete ||
                evidenceManifest.RecordCount < 0 || evidenceManifest.SegmentSha256.Length != 64)
            {
                failures.Add("Embedded Evidence manifest is incompatible or mismatched.");
                return;
            }
            var manifestHash = Hash(JsonSerializer.SerializeToUtf8Bytes(
                evidenceManifest with { IntegrityHash = null }, ArtifactJsonOptions));
            if (!string.Equals(manifestHash, evidenceManifest.IntegrityHash, StringComparison.Ordinal))
                failures.Add("Embedded Evidence manifest integrity validation failed.");
            var (recordsHash, recordCount) = await HashEvidenceRecordsAsync(evidenceRecordsEntry, cancellationToken)
                .ConfigureAwait(false);
            if (recordsHash != evidenceManifest.SegmentSha256)
                failures.Add("Embedded Evidence records do not match the store segment hash.");
            if (recordCount != evidenceManifest.RecordCount)
                failures.Add("Embedded Evidence record count does not match the store manifest.");
        }
        else if (package.PackageType == "final")
        {
            failures.Add("Final package run manifest has no Evidence reference.");
        }

        await ValidateProjectionAsync(run, package, entries, failures, cancellationToken).ConfigureAwait(false);
        await ValidateRecoveryAsync(run, package, entries, failures, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ValidateProjectionAsync(WorkspaceRunManifest run, WorkspaceEvidencePackageManifest package,
        Dictionary<string, ZipArchiveEntry> entries, List<string> failures, CancellationToken cancellationToken)
    {
        if (run.ProjectionManifestReference is null) return;
        var projectionArtifact = package.Artifacts.SingleOrDefault(item => item.Role == "projection-manifest");
        var journalArtifact = package.Artifacts.SingleOrDefault(item => item.Role == "projection-journal");
        if (projectionArtifact?.Path is null || journalArtifact?.Path is null ||
            !entries.TryGetValue(projectionArtifact.Path, out var projectionEntry) ||
            !entries.TryGetValue(journalArtifact.Path, out var journalEntry))
        {
            failures.Add("Run references Projection artifacts that are not embedded.");
            return;
        }
        if (!Guid.TryParse(run.ProjectionRunId, out var projectionRunId))
        {
            failures.Add("Run manifest has an invalid Projection run identity.");
            return;
        }
        var projection = ProjectionRunManifestStore.ReadAndValidate(
            await ReadEntryAsync(projectionEntry, MaximumManifestLength, cancellationToken).ConfigureAwait(false),
            new ProofShift.Domain.RunId(projectionRunId));
        if (projection.ManifestHash != run.ProjectionManifestHash ||
            projection.ProjectionFingerprint != run.ProjectionFingerprint ||
            projection.CheckpointId != run.CheckpointId ||
            projection.CheckpointManifestHash != run.CheckpointManifestHash)
            failures.Add("Embedded Projection manifest does not match the run's bound identities.");
        if (journalEntry.Length == 0)
            failures.Add("Embedded Projection lineage journal is empty.");
    }

    private static async Task ValidateRecoveryAsync(WorkspaceRunManifest run, WorkspaceEvidencePackageManifest package,
        Dictionary<string, ZipArchiveEntry> entries, List<string> failures, CancellationToken cancellationToken)
    {
        if (run.RecoveryArtifactReference is null) return;
        var manifestArtifact = package.Artifacts.SingleOrDefault(item => item.Role == "recovery-manifest");
        if (manifestArtifact?.Path is null || !entries.TryGetValue(manifestArtifact.Path, out var recoveryManifestEntry))
        {
            failures.Add("Run references a Recovery manifest that is not embedded.");
            return;
        }
        using var recoveryJson = JsonDocument.Parse(await ReadEntryAsync(recoveryManifestEntry, MaximumManifestLength, cancellationToken)
            .ConfigureAwait(false));
        var root = recoveryJson.RootElement;
        if (root.GetProperty("format").GetString() != FileSystemRecoveryArtifactStore.StoreFormatVersion ||
            root.GetProperty("dryRunId").GetString() != run.RecoveryRunId ||
            root.GetProperty("integrityHash").GetString() != run.RecoveryArtifactIntegrityHash)
            failures.Add("Embedded Recovery manifest does not match the run.");
        var files = root.GetProperty("files");
        var recoveryNames = files.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray();
        if (!recoveryNames.SequenceEqual(RecoveryArtifactNames.Order(StringComparer.Ordinal), StringComparer.Ordinal))
        {
            failures.Add("Embedded Recovery manifest has an unsupported artifact set.");
            return;
        }
        var manifestDraft = new RecoveryManifestIdentity(root.GetProperty("format").GetString()!,
            root.GetProperty("dryRunId").GetString()!, files, null);
        var recoveryHash = Hash(JsonSerializer.SerializeToUtf8Bytes(manifestDraft, ArtifactJsonOptions));
        if (recoveryHash != run.RecoveryArtifactIntegrityHash)
            failures.Add("Embedded Recovery manifest integrity hash does not match.");
        foreach (var file in files.EnumerateObject())
        {
            var artifact = package.Artifacts.SingleOrDefault(item => item.Role ==
                $"recovery-{Path.GetFileNameWithoutExtension(file.Name)}");
            if (artifact?.Path is null || !entries.TryGetValue(artifact.Path, out var entry))
            {
                failures.Add($"Recovery artifact is not embedded: {file.Name}");
                continue;
            }
            var actual = await HashEntryAsync(entry, entry.Length, cancellationToken).ConfigureAwait(false);
            if (actual.Hash != file.Value.GetString())
                failures.Add($"Recovery artifact integrity validation failed: {file.Name}");
        }
    }

    private static WorkspaceEvidencePackageArtifactPolicy? GetArtifactPolicy(string role)
    {
        if (role is "run-manifest" or "effective-plan" or "evidence-store-manifest" or "evidence-records" or
            "projection-manifest" or "projection-journal" or "recovery-manifest")
            return new("embedded", true);
        if (role == WorkspaceEvaluationSummaryService.SummaryRole)
            return new("embedded", true);
        if (RecoveryArtifactNames.Any(name =>
                role == $"recovery-{Path.GetFileNameWithoutExtension(name)}"))
            return new("embedded", true);
        if (role is "checkpoint-manifest" or "verification-ledger")
            return new("external-reference", false);
        if (role is "checkpoint-payload" or "source-data" or "projected-target-data" or
            "resolved-configuration" or "graph-source-mapping")
            return new("omitted", false);
        return null;
    }

    private static async Task<IReadOnlyList<WorkspaceEvidencePackageArtifact>> DescribeCandidatesAsync(
        IReadOnlyList<PackageArtifactSource> candidates, CancellationToken cancellationToken)
    {
        var result = new List<WorkspaceEvidencePackageArtifact>(candidates.Count);
        foreach (var candidate in candidates)
        {
            var (hash, length) = candidate.Bytes is not null
                ? (Hash(candidate.Bytes), (long)candidate.Bytes.Length)
                : await HashFileAsync(candidate.SourcePath!, cancellationToken).ConfigureAwait(false);
            result.Add(new WorkspaceEvidencePackageArtifact(candidate.Role, "embedded", candidate.ArchivePath,
                hash, length, null, candidate.Reason, candidate.Required));
        }
        return result;
    }

    private static void AddExternal(List<WorkspaceEvidencePackageArtifact> entries, string role, string? hash,
        string? reference, string reason)
    {
        if (reference is null) return;
        entries.Add(new WorkspaceEvidencePackageArtifact(role, "external-reference", null, hash, null,
            reference, reason, false));
    }

    private static void AddOmitted(List<WorkspaceEvidencePackageArtifact> entries, string role, string? hash, string reason) =>
        entries.Add(new WorkspaceEvidencePackageArtifact(role, "omitted", null, hash, null, null, reason, false));

    private static string ComputePackageIdentity(WorkspaceEvidencePackageManifest manifest)
    {
        var identity = new PackageIdentityInput(manifest.Format, manifest.PackageType, manifest.RunId,
            manifest.RunManifestIdentityFingerprint, manifest.RunManifestHash, manifest.ProductVersion,
            manifest.SourceRevision, manifest.SourceTree, manifest.PlanFingerprint, manifest.ExecutionState,
            manifest.Disposition, manifest.Artifacts.OrderBy(item => item.Role, StringComparer.Ordinal)
                .ThenBy(item => item.Path, StringComparer.Ordinal).ToArray());
        return Hash(JsonSerializer.SerializeToUtf8Bytes(identity, JsonOptions));
    }

    private static async Task<(string Hash, long Length)> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long length = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            hash.AppendData(buffer.AsSpan(0, read));
            length += read;
        }
        return (Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), length);
    }

    private static async Task<(string Hash, long Length)> HashEntryAsync(ZipArchiveEntry entry, long expectedLength,
        CancellationToken cancellationToken)
    {
        await using var stream = entry.Open();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long length = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            length += read;
            if (length > expectedLength) throw new InvalidDataException("ZIP entry expands beyond its declared artifact size.");
            hash.AppendData(buffer.AsSpan(0, read));
        }
        if (length != expectedLength)
            throw new InvalidDataException("ZIP entry size does not match its declared artifact size.");
        return (Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), length);
    }

    private static async Task<(string Hash, long Count)> HashEvidenceRecordsAsync(ZipArchiveEntry entry,
        CancellationToken cancellationToken)
    {
        await using var stream = entry.Open();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long count = 0;
        var lastByte = -1;
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            hash.AppendData(buffer.AsSpan(0, read));
            for (var index = 0; index < read; index++)
                if (buffer[index] == (byte)'\n') count++;
            lastByte = buffer[read - 1];
        }
        if (lastByte != (byte)'\n' && entry.Length > 0)
            throw new InvalidDataException("Evidence NDJSON segment does not end at a record boundary.");
        return (Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), count);
    }

    private static async Task<byte[]> ReadEntryAsync(ZipArchiveEntry entry, long maximumLength,
        CancellationToken cancellationToken)
    {
        if (entry.Length > maximumLength)
            throw new InvalidDataException("Embedded metadata exceeds the supported size.");
        await using var input = entry.Open();
        using var output = new MemoryStream((int)entry.Length);
        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        if (output.Length != entry.Length || output.Length > maximumLength)
            throw new InvalidDataException("Embedded metadata length does not match the ZIP directory.");
        return output.ToArray();
    }

    private static string ResolveRunReference(string root, string runDirectory, string workspaceReference)
    {
        var relative = NormalizeRelative(workspaceReference);
        var full = ResolveContained(root, relative);
        if (!IsContained(runDirectory, full) || string.Equals(runDirectory, full, PathComparison))
            throw new InvalidDataException("Run artifact reference escapes the selected run directory.");
        var current = runDirectory;
        foreach (var segment in Path.GetRelativePath(runDirectory, full).Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Run artifact path traverses a symbolic link or reparse point.");
        }
        if (!File.Exists(full)) throw new FileNotFoundException("Run artifact reference is missing.", full);
        return full;
    }

    private static string NormalizeWorkspaceReference(string runRelative, string localReference)
    {
        var local = NormalizeRelative(localReference);
        var combined = $"{runRelative}/{local}";
        return NormalizeRelative(combined);
    }

    private static string NormalizeRelative(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Contains('\\') ||
            path.Contains(':') || path.StartsWith('/'))
            throw new InvalidDataException("Artifact reference is not a safe relative path.");
        var parts = path.Split('/');
        if (parts.Any(part => part.Length == 0 || part is "." or ".."))
            throw new InvalidDataException("Artifact reference contains an unsafe path segment.");
        return string.Join('/', parts);
    }

    private static string ResolveContained(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root);
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsContained(fullRoot, fullPath))
            throw new InvalidDataException("Path is outside its configured root.");
        return fullPath;
    }

    private static bool IsContained(string root, string candidate)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(candidate));
        return !Path.IsPathRooted(relative) && relative != ".." &&
            !relative.StartsWith($"..{Path.DirectorySeparatorChar}", PathComparison);
    }

    private static void EnsureNoReparsePoints(string root, string candidate)
    {
        var fullRoot = Path.GetFullPath(root);
        var relative = Path.GetRelativePath(fullRoot, Path.GetFullPath(candidate));
        if (Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", PathComparison))
            throw new InvalidDataException("Run path is outside the workspace.");
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

    private static bool IsSafeArchivePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\\') || value.Contains(':') ||
            value.StartsWith('/') || value.EndsWith('/'))
            return false;
        return value.Split('/').All(segment => segment.Length > 0 && segment is not ("." or ".."));
    }

    private static bool IsSymbolicLink(ZipArchiveEntry entry)
    {
        var unixMode = (entry.ExternalAttributes >> 16) & 0xF000;
        return unixMode == 0xA000 || ((FileAttributes)entry.ExternalAttributes & FileAttributes.ReparsePoint) != 0;
    }

    private static string Hash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private sealed record PackageIdentityInput(
        string Format,
        string PackageType,
        Guid RunId,
        string RunManifestIdentityFingerprint,
        string RunManifestHash,
        string ProductVersion,
        string? SourceRevision,
        string? SourceTree,
        string? PlanFingerprint,
        string ExecutionState,
        string? Disposition,
        IReadOnlyList<WorkspaceEvidencePackageArtifact> Artifacts);

    private sealed record RecoveryManifestIdentity(string Format, string DryRunId, JsonElement Files, string? IntegrityHash);

    private sealed record WorkspaceEvidencePackageArtifactPolicy(string Classification, bool Required);

    private sealed record PackageArtifactSource(
        string Role,
        string ArchivePath,
        string? SourcePath,
        byte[]? Bytes,
        string Reason,
        bool Required)
    {
        public static PackageArtifactSource FromBytes(string role, string archivePath, byte[] bytes,
            string reason, bool required) => new(role, archivePath, null, bytes, reason, required);

        public static PackageArtifactSource FromFile(string role, string archivePath, string path,
            string reason, bool required) => new(role, archivePath, path, null, reason, required);

        public async Task CopyToAsync(Stream destination, CancellationToken cancellationToken)
        {
            if (Bytes is not null)
            {
                await destination.WriteAsync(Bytes, cancellationToken).ConfigureAwait(false);
                return;
            }
            await using var source = new FileStream(SourcePath!, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        }
    }
}
