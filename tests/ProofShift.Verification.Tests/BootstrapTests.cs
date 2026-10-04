using ProofShift.Domain;
using Xunit;

namespace ProofShift.Verification.Tests;

public sealed class VerificationModelTests
{
    [Fact]
    public void PolicyRetainsUniqueRequiredScopesAndEvaluationCarriesExplainableResult()
    {
        var policy = new VerificationPolicy(
        [
            VerificationScope.Attribute,
            VerificationScope.Accounting,
            VerificationScope.Attribute
        ]);
        var evaluation = new RuleEvaluation(
            EvidenceResult.Fail,
            "The normalized values differ.",
            [new EvidenceReference(artifactId: new ArtifactId("source"))],
            new EvidenceValue(new StringValue("ACTIVE")),
            new EvidenceValue(new StringValue("RETIRED")));

        Assert.Equal(2, policy.RequiredScopes.Count);
        Assert.Equal(EvidenceResult.Fail, evaluation.Result);
        Assert.Equal("The normalized values differ.", evaluation.Explanation);
        Assert.Single(evaluation.Inputs);
    }
}
