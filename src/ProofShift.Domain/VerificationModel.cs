namespace ProofShift.Domain;

public sealed record VerificationContext
{
    public MigrationPlan Plan { get; }
    public MigrationRun Run { get; }

    public VerificationContext(MigrationPlan plan, MigrationRun run)
    {
        Plan = DomainGuard.NotNull(plan, nameof(plan));
        Run = DomainGuard.NotNull(run, nameof(run));

        if (run.PlanId != plan.Id || run.PlanVersion != plan.Version)
        {
            throw new ArgumentException("The run must reference the supplied migration plan and version.", nameof(run));
        }
    }
}

public sealed record RuleEvaluation
{
    public EvidenceResult Result { get; }
    public string Explanation { get; }
    public DomainList<EvidenceReference> Inputs { get; }
    public EvidenceValue? Expected { get; }
    public EvidenceValue? Actual { get; }

    public RuleEvaluation(
        EvidenceResult result,
        string explanation,
        IEnumerable<EvidenceReference>? inputs = null,
        EvidenceValue? expected = null,
        EvidenceValue? actual = null)
    {
        Result = result;
        Explanation = DomainGuard.Required(explanation, nameof(explanation));
        Inputs = new DomainList<EvidenceReference>(inputs ?? Array.Empty<EvidenceReference>());
        Expected = expected;
        Actual = actual;
    }
}

public interface IVerificationRule
{
    RuleId Id { get; }
    string Version { get; }
    VerificationScope Scope { get; }

    Task<RuleEvaluation> EvaluateAsync(
        VerificationContext context,
        CancellationToken cancellationToken);
}
