using System.Globalization;
using ProofShift.Domain;

namespace ProofShift.Connectors.Abstractions;

public static class ShadowRecoveryIssueCodes
{
    public const string CheckpointUnavailable = "PSREC004";
    public const string CheckpointIntegrityFailure = "PSREC005";
    public const string RehearsalMutationFailed = "PSREC008";
    public const string ContextMismatch = "PSREC009";
}

public sealed record ShadowRecoveryTarget
{
    public ShadowTargetContext Context { get; }
    public ArtifactSelector Selector { get; }

    public ShadowRecoveryTarget(ShadowTargetContext context, ArtifactSelector selector)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
        Selector = selector ?? throw new ArgumentNullException(nameof(selector));
    }
}

public sealed record ShadowRecoveryRequest
{
    public RecoveryCheckpointId CheckpointId { get; }
    public string GraphHash { get; }
    public string BaselineFingerprint { get; }
    public long TargetArtifactCount { get; }
    public DomainList<ShadowRecoveryTarget> Targets { get; }
    public RunId ShadowRunId { get; }
    public SystemId SystemId { get; }
    public StorageEndpointId EndpointId { get; }
    public ConnectorId ConnectorId { get; }

    public ShadowRecoveryRequest(RecoveryCheckpointId checkpointId, string graphHash, string baselineFingerprint,
        long targetArtifactCount, IEnumerable<ShadowRecoveryTarget> targets)
    {
        CheckpointId = Required(checkpointId, nameof(checkpointId));
        GraphHash = RequiredHash(graphHash, nameof(graphHash));
        BaselineFingerprint = RequiredHash(baselineFingerprint, nameof(baselineFingerprint));
        ArgumentOutOfRangeException.ThrowIfNegative(targetArtifactCount);
        TargetArtifactCount = targetArtifactCount;
        Targets = new DomainList<ShadowRecoveryTarget>(targets.OrderBy(target => target.Context.ConnectorContext.NodeKey, StringComparer.Ordinal));
        if (Targets.Count == 0)
            throw new ArgumentException("A shadow recovery request requires at least one target node.", nameof(targets));
        var first = Targets[0].Context;
        if (Targets.Any(target => target.Context.RunId != first.RunId ||
            target.Context.ConnectorContext.SystemKey != first.ConnectorContext.SystemKey ||
            target.Context.ConnectorContext.EndpointKey != first.ConnectorContext.EndpointKey ||
            target.Context.ConnectorContext.Connector != first.ConnectorContext.Connector))
            throw new ArgumentException("One shadow recovery request must identify one run, system, endpoint, and connector.", nameof(targets));
        if (Targets.Select(target => target.Context.ConnectorContext.NodeKey).Distinct(StringComparer.Ordinal).Count() != Targets.Count)
            throw new ArgumentException("Shadow recovery target node keys must be unique.", nameof(targets));
        ShadowRunId = first.RunId;
        SystemId = new SystemId(first.ConnectorContext.SystemKey);
        EndpointId = new StorageEndpointId(first.ConnectorContext.EndpointKey);
        ConnectorId = first.ConnectorContext.Connector;
    }

    private static RecoveryCheckpointId Required(RecoveryCheckpointId value, string parameterName) =>
        value.Value == Guid.Empty ? throw new ArgumentException("Recovery checkpoint ID must not be empty.", parameterName) : value;

    private static string RequiredHash(string value, string parameterName)
    {
        var hash = string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Hash is required.", parameterName) : value.Trim().ToLowerInvariant();
        if (hash.Length != 64 || hash.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("Value must be a SHA-256 digest.", parameterName);
        return hash;
    }
}

public sealed record ShadowRecoveryCheckpoint
{
    public RecoveryCheckpointId Id { get; }
    public RunId ShadowRunId { get; }
    public SystemId SystemId { get; }
    public StorageEndpointId EndpointId { get; }
    public ConnectorId ConnectorId { get; }
    public string ConnectorVersion { get; }
    public string GraphHash { get; }
    public string BaselineFingerprint { get; }
    public long TargetArtifactCount { get; }
    public string Reference { get; }
    public string ContentSha256 { get; }
    public DateTimeOffset CapturedAt { get; }

    public ShadowRecoveryCheckpoint(RecoveryCheckpointId id, RunId shadowRunId, SystemId systemId,
        StorageEndpointId endpointId, ConnectorId connectorId, string connectorVersion, string graphHash,
        string baselineFingerprint, long targetArtifactCount, string reference, string contentSha256,
        DateTimeOffset capturedAt)
    {
        if (id.Value == Guid.Empty || shadowRunId.Value == Guid.Empty)
            throw new ArgumentException("Recovery and shadow run IDs must not be empty.");
        Id = id;
        ShadowRunId = shadowRunId;
        SystemId = systemId;
        EndpointId = endpointId;
        ConnectorId = connectorId;
        ConnectorVersion = Required(connectorVersion, nameof(connectorVersion));
        GraphHash = Hash(graphHash, nameof(graphHash));
        BaselineFingerprint = Hash(baselineFingerprint, nameof(baselineFingerprint));
        ArgumentOutOfRangeException.ThrowIfNegative(targetArtifactCount);
        TargetArtifactCount = targetArtifactCount;
        Reference = Required(reference, nameof(reference));
        ContentSha256 = Hash(contentSha256, nameof(contentSha256));
        CapturedAt = capturedAt;
    }

    private static string Hash(string value, string parameterName)
    {
        var hash = Required(value, parameterName).ToLowerInvariant();
        if (hash.Length != 64 || hash.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("Value must be a SHA-256 digest.", parameterName);
        return hash;
    }

    private static string Required(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Value must not be empty.", parameterName) : value.Trim();
}

public sealed record ShadowRecoveryMutation(long AffectedArtifacts, string Description);

public interface IShadowTargetRecoveryConnector : IShadowTargetConnector
{
    Task<ShadowRecoveryCheckpoint> CaptureRecoveryCheckpointAsync(ShadowRecoveryRequest request,
        CancellationToken cancellationToken);

    Task ValidateRecoveryCheckpointAsync(ShadowRecoveryRequest request, ShadowRecoveryCheckpoint checkpoint,
        CancellationToken cancellationToken);

    Task<ShadowRecoveryMutation> ApplyControlledMutationAsync(ShadowRecoveryRequest request,
        CancellationToken cancellationToken);

    Task RestoreRecoveryCheckpointAsync(ShadowRecoveryRequest request, ShadowRecoveryCheckpoint checkpoint,
        CancellationToken cancellationToken);
}

public sealed class ShadowRecoveryConnectorException : Exception
{
    public string Code { get; }

    public ShadowRecoveryConnectorException(string code, string message) : base(message)
    {
        Code = string.IsNullOrWhiteSpace(code) ? throw new ArgumentException("Issue code is required.", nameof(code)) : code.Trim();
    }
}
