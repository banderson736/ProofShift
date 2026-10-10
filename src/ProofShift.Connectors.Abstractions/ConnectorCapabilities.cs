namespace ProofShift.Connectors.Abstractions;

public sealed record ConnectorCapabilityDescriptor(string Id, string Version, bool Discovery, bool SourceRead,
    bool CheckpointCapture, bool TargetObservation, bool ShadowWrite, bool BinaryStreaming, bool SchemaInspection,
    string Consistency, bool Partitioning, string? Transport = null, bool StructuredRead = false,
    bool RangeRead = false, bool VersionPinning = false, bool OfflineReplay = false);