using System.Globalization;
using ProofShift.Domain;
using ProofShift.Verification;

namespace ProofShift.Packs.Pension;

public sealed class PensionMemberAccountingRule(VerificationRuleDefinition definition) : PensionRuleBase(definition)
{
    public override VerificationScope Scope => VerificationScope.Accounting;

    public override async IAsyncEnumerable<VerificationFinding> EvaluateAsync(VerificationExecutionContext context,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var semanticType = Option("semanticType", "Pension.Member");
        var sourceCount = 0;
        var accountedCount = 0;
        await foreach (var source in context.Workspace.ReadSourceFactsAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (source.SemanticType != semanticType) continue;
            sourceCount++;
            var isAccounted = source.FailedEntries == 0 &&
                ((source.ProducedEntries > 0) ^ (source.ExcludedEntries > 0));
            if (isAccounted)
            {
                accountedCount++;
                continue;
            }

            yield return Fail(context, EvidenceType.Accounting, "UnaccountedSource",
                $"{source.NodeKey}:{source.Artifact.Id.Value}",
                "Pension member source is unaccounted or has conflicting dispositions.",
                [ArtifactInput(context, source.NodeKey, source.Artifact)]);
        }

        if (sourceCount == 0)
        {
            yield return Finding(context, EvidenceType.Accounting, EvidenceResult.NotApplicable,
                "MemberAccountingNotApplicable", $"{Id.Value}:none",
                "No source artifacts match the configured pension member semantic type.");
        }
        else if (accountedCount > 0)
        {
            yield return Finding(context, EvidenceType.Accounting, EvidenceResult.Pass, "MemberAccounting",
                $"{Id.Value}:population",
                $"{accountedCount.ToString(CultureInfo.InvariantCulture)} of {sourceCount.ToString(CultureInfo.InvariantCulture)} pension member source artifacts are accounted for.",
                context.BindingReferences, new EvidenceValue(new IntegerValue(sourceCount)),
                new EvidenceValue(new IntegerValue(accountedCount)));
        }
    }
}