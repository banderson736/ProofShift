namespace ProofShift.Domain;

public enum EvidenceResult
{
    Pass,
    Fail,
    Warning,
    NotApplicable
}

public enum EvidenceType
{
    Observation,
    Transformation,
    Comparison,
    Relationship,
    Timeline,
    Aggregate,
    Accounting,
    Recovery,
    Decision
}

public sealed record EvidenceValue
{
    public ValueNode Value { get; }

    public EvidenceValue(ValueNode value) => Value = DomainGuard.NotNull(value, nameof(value));
}

public sealed record EvidenceReference
{
    public ArtifactId? ArtifactId { get; }
    public EvidenceId? EvidenceId { get; }

    public EvidenceReference(ArtifactId? artifactId = null, EvidenceId? evidenceId = null)
    {
        if ((artifactId is null) == (evidenceId is null))
        {
            throw new ArgumentException("An evidence reference must identify exactly one artifact or evidence record.");
        }

        ArtifactId = artifactId is { } artifact ? DomainGuard.Required(artifact, nameof(artifactId)) : null;
        EvidenceId = evidenceId is { } evidence ? DomainGuard.Required(evidence, nameof(evidenceId)) : null;
    }
}

public sealed record EvidenceRecord
{
    public EvidenceId Id { get; }
    public RunId RunId { get; }
    public EvidenceType Type { get; }
    public RuleId RuleId { get; }
    public string RuleVersion { get; }
    public EvidenceResult Result { get; }
    public DomainList<EvidenceReference> Inputs { get; }
    public EvidenceValue? Expected { get; }
    public EvidenceValue? Actual { get; }
    public string Explanation { get; }
    public DateTimeOffset EvaluatedAt { get; }

    public EvidenceRecord(
        EvidenceId id,
        RunId runId,
        EvidenceType type,
        RuleId ruleId,
        string ruleVersion,
        EvidenceResult result,
        IEnumerable<EvidenceReference> inputs,
        string explanation,
        DateTimeOffset evaluatedAt,
        EvidenceValue? expected = null,
        EvidenceValue? actual = null)
    {
        Id = DomainGuard.Required(id, nameof(id));
        RunId = DomainGuard.Required(runId, nameof(runId));
        Type = type;
        RuleId = DomainGuard.Required(ruleId, nameof(ruleId));
        RuleVersion = DomainGuard.Required(ruleVersion, nameof(ruleVersion));
        Result = result;
        Inputs = new DomainList<EvidenceReference>(inputs);
        Expected = expected;
        Actual = actual;
        Explanation = DomainGuard.Required(explanation, nameof(explanation));
        EvaluatedAt = evaluatedAt;
    }
}

public enum RunType
{
    Inspection,
    Snapshot,
    Projection,
    Verification,
    DryRun,
    ProductionObservation
}

public enum RunStatus
{
    Running,
    Completed,
    Failed,
    Cancelled
}

public sealed record RuntimeFingerprint
{
    public string ProofShiftVersion { get; }
    public string ConfigurationHash { get; }
    public string GraphHash { get; }
    public DomainDictionary<string> ConnectorVersions { get; }
    public DomainDictionary<string> DomainPackVersions { get; }

    public RuntimeFingerprint(
        string proofShiftVersion,
        string configurationHash,
        string graphHash,
        IEnumerable<KeyValuePair<string, string>>? connectorVersions = null,
        IEnumerable<KeyValuePair<string, string>>? domainPackVersions = null)
    {
        ProofShiftVersion = DomainGuard.Required(proofShiftVersion, nameof(proofShiftVersion));
        ConfigurationHash = DomainGuard.Required(configurationHash, nameof(configurationHash));
        GraphHash = DomainGuard.Required(graphHash, nameof(graphHash));
        ConnectorVersions = new DomainDictionary<string>(connectorVersions ?? Array.Empty<KeyValuePair<string, string>>());
        DomainPackVersions = new DomainDictionary<string>(domainPackVersions ?? Array.Empty<KeyValuePair<string, string>>());
    }
}

public sealed record MigrationRun
{
    public RunId Id { get; }
    public MigrationPlanId PlanId { get; }
    public int PlanVersion { get; }
    public RunType Type { get; }
    public RunStatus Status { get; }
    public DateTimeOffset StartedAt { get; }
    public DateTimeOffset? CompletedAt { get; }
    public RuntimeFingerprint Runtime { get; }

    public MigrationRun(
        RunId id,
        MigrationPlanId planId,
        int planVersion,
        RunType type,
        RunStatus status,
        DateTimeOffset startedAt,
        RuntimeFingerprint runtime,
        DateTimeOffset? completedAt = null)
    {
        if (planVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(planVersion), "Plan version must be positive.");
        }

        if (completedAt < startedAt)
        {
            throw new ArgumentException("CompletedAt must not precede StartedAt.", nameof(completedAt));
        }

        if ((status == RunStatus.Running) == (completedAt is not null))
        {
            throw new ArgumentException("Running runs must not have CompletedAt; terminal runs must have it.", nameof(completedAt));
        }

        Id = DomainGuard.Required(id, nameof(id));
        PlanId = DomainGuard.Required(planId, nameof(planId));
        PlanVersion = planVersion;
        Type = type;
        Status = status;
        StartedAt = startedAt;
        CompletedAt = completedAt;
        Runtime = DomainGuard.NotNull(runtime, nameof(runtime));
    }
}
