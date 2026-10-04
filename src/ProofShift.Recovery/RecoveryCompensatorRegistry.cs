using ProofShift.Connectors.Abstractions;
using ProofShift.Domain;

namespace ProofShift.Recovery;

public sealed record RecoveryShadowCheckpointBinding(
    IShadowTargetRecoveryConnector Connector,
    ShadowRecoveryRequest Request,
    ShadowRecoveryCheckpoint Checkpoint);

public sealed record RecoveryCompensationValidationContext(
    IReadOnlyCollection<RecoveryShadowCheckpointBinding> Checkpoints,
    string BaselineFingerprint,
    string RestoredFingerprint,
    long BaselineArtifactCount,
    long RestoredArtifactCount);

public interface IRecoveryCompensator
{
    string StrategyId { get; }
    string Version { get; }
    RecoveryValidationMode ValidationMode { get; }
    RecoveryCheckResult Validate(MigrationEdge edge);
    Task<RecoveryCheckResult> ValidateShadowOutcomeAsync(MigrationEdge edge,
        RecoveryCompensationValidationContext context, CancellationToken cancellationToken);
    Task ExecuteShadowRehearsalAsync(IReadOnlyCollection<RecoveryShadowCheckpointBinding> checkpoints,
        CancellationToken cancellationToken);
}

public sealed class RecoveryCompensatorRegistry
{
    private readonly System.Collections.ObjectModel.ReadOnlyDictionary<string, IRecoveryCompensator> _compensators;

    public RecoveryCompensatorRegistry(IEnumerable<IRecoveryCompensator> compensators)
    {
        ArgumentNullException.ThrowIfNull(compensators);
        var map = new Dictionary<string, IRecoveryCompensator>(StringComparer.Ordinal);
        foreach (var compensator in compensators.OrderBy(item => item.StrategyId, StringComparer.Ordinal))
        {
            ArgumentNullException.ThrowIfNull(compensator);
            if (string.IsNullOrWhiteSpace(compensator.StrategyId) || string.IsNullOrWhiteSpace(compensator.Version))
                throw new ArgumentException("Compensator ID and version are required.", nameof(compensators));
            if (!map.TryAdd(compensator.StrategyId, compensator))
                throw new ArgumentException($"Compensator '{compensator.StrategyId}' is registered more than once.", nameof(compensators));
        }
        _compensators = new System.Collections.ObjectModel.ReadOnlyDictionary<string, IRecoveryCompensator>(map);
    }

    public IRecoveryCompensator Resolve(string strategyId) =>
        _compensators.TryGetValue(strategyId, out var compensator)
            ? compensator
            : throw new RecoveryException(RecoveryIssueCodes.UnknownCompensator,
                $"Recovery compensator '{strategyId}' is not registered.");
}

public sealed class ShadowBaselineRestoreCompensator : IRecoveryCompensator
{
    public string StrategyId => "restore-shadow-baseline";
    public string Version => "1";
    public RecoveryValidationMode ValidationMode => RecoveryValidationMode.ExactRestoration;

    public RecoveryCheckResult Validate(MigrationEdge edge) =>
        edge.Recovery?.Strategy == StrategyId ? RecoveryCheckResult.Pass : RecoveryCheckResult.Fail;

    public Task<RecoveryCheckResult> ValidateShadowOutcomeAsync(MigrationEdge edge,
        RecoveryCompensationValidationContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(context.BaselineFingerprint == context.RestoredFingerprint &&
            context.BaselineArtifactCount == context.RestoredArtifactCount
                ? RecoveryCheckResult.Pass : RecoveryCheckResult.Fail);
    }

    public async Task ExecuteShadowRehearsalAsync(IReadOnlyCollection<RecoveryShadowCheckpointBinding> checkpoints,
        CancellationToken cancellationToken)
    {
        foreach (var item in checkpoints.OrderBy(item => item.Request.SystemId.Value, StringComparer.Ordinal)
            .ThenBy(item => item.Request.EndpointId.Value, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await item.Connector.RestoreRecoveryCheckpointAsync(item.Request, item.Checkpoint, cancellationToken).ConfigureAwait(false);
        }
    }
}
