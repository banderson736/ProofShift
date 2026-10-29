using ProofShift.Domain;

namespace ProofShift.Connectors.Abstractions;

public sealed record ConnectorCapabilityDescriptor(string Id, string Version, bool Discovery, bool SourceRead,
    bool CheckpointCapture, bool TargetObservation, bool ShadowWrite, bool BinaryStreaming, bool SchemaInspection,
    string Consistency, bool Partitioning, string? Transport = null, bool StructuredRead = false,
    bool RangeRead = false, bool VersionPinning = false, bool OfflineReplay = false,
    IReadOnlyCollection<string>? EvaluationScopePredicates = null);

public static class EvaluationScopeCapability
{
    public static void Validate(ISourceConnector connector, ArtifactSelector selector)
    {
        ArgumentNullException.ThrowIfNull(connector);
        ArgumentNullException.ThrowIfNull(selector);
        if (selector.EvaluationScope is not { } scope) return;
        if (scope.Type == EvaluationScopeType.BoundedEvaluation &&
            string.Equals(selector.Kind, "table", StringComparison.OrdinalIgnoreCase) &&
            scope.Predicates.Count == 0)
            throw new ConnectorReadException(ConnectorIssueCodes.InvalidEvaluationPredicate,
                "A bounded table evaluation scope requires at least one source predicate.");

        var supported = connector.Capabilities.EvaluationScopePredicates ?? [];
        var unsupported = scope.Predicates.FirstOrDefault(predicate =>
            !supported.Contains(EvaluationPredicate.OperatorName(predicate.Operator), StringComparer.Ordinal));
        if (unsupported is not null)
            throw new ConnectorReadException(ConnectorIssueCodes.EvaluationScopeUnsupported,
                $"Connector '{connector.Id.Value}' does not support the configured evaluation-scope predicate '{EvaluationPredicate.OperatorName(unsupported.Operator)}'.");
    }
}