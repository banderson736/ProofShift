using ProofShift.Domain;
using Xunit;

namespace ProofShift.Evidence.Tests;

public sealed class EvidenceRecordTests
{
    [Fact]
    public void EvidenceRecordCopiesAndRetainsArtifactAndEvidenceInputs()
    {
        var inputs = new List<EvidenceReference>
        {
            new(artifactId: new ArtifactId("source-artifact"))
        };
        var evidence = new EvidenceRecord(
            new EvidenceId(Guid.NewGuid()),
            new RunId(Guid.NewGuid()),
            EvidenceType.Comparison,
            new RuleId("attribute-equality"),
            "1",
            EvidenceResult.Pass,
            inputs,
            "Values match.",
            DateTimeOffset.UnixEpoch,
            new EvidenceValue(new StringValue("ACTIVE")),
            new EvidenceValue(new StringValue("ACTIVE")));
        inputs.Add(new EvidenceReference(evidenceId: new EvidenceId(Guid.NewGuid())));

        var input = Assert.Single(evidence.Inputs);

        Assert.Equal(new ArtifactId("source-artifact"), input.ArtifactId);
        Assert.Null(input.EvidenceId);
        Assert.Equal(EvidenceResult.Pass, evidence.Result);
    }
}
