using System.Text.Json;
using ProofShift.Packs.Pension;
using Xunit;

namespace ProofShift.EndToEnd.Tests;

public sealed class PensionReportAggregationTests
{
    [Fact]
    public void BusinessDefectSummaryCountsPrimaryDiscrepanciesWithoutCountingAggregateEvidenceTwice()
    {
        var records = new List<object>();
        Add("MissingMember", 7);
        Add("DuplicateMember", 4);
        Add("WrongMemberStatus", 3);
        Add("MissingEmploymentPeriod", 12);
        Add("IncorrectEmploymentDate", 8);
        Add("IncorrectServiceCreditTotal", 17);
        Add("MissingContribution", 39);
        Add("DuplicateContribution", 8);
        Add("IncorrectContributionAmount", 6);
        Add("BenefitPaymentAmountMismatch", 11);
        Add("BrokenBeneficiaryRelationship", 6);
        Add("WrongMemberBeneficiary", 2);
        Add("RetirementElectionMappingMismatch", 3);
        Add("CodeTransformationMismatch", 5);
        Add("MissingDocument", 9);
        Add("WrongMemberDocument", 4);
        Add("MissingHistoricalExport", 3);
        Add("ContributionPeriodTotalMismatch", 39);
        Add("BenefitPaymentTotalMismatch", 11);
        Add("UnexpectedTarget", 1);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(records));
        var defects = PensionAssuranceReportBuilder.CountBusinessDiscrepancies(
            document.RootElement.EnumerateArray().ToArray(), falseReversibleEdges: 2);

        Assert.Equal(18, defects.Count);
        Assert.Equal(149L, defects.Values.Sum());
        Assert.Equal(7L, defects["missingMembers"]);
        Assert.Equal(39L, defects["missingContributions"]);
        Assert.Equal(11L, defects["benefitPaymentDiscrepancies"]);
        Assert.Equal(2L, defects["falseReversibleTransformations"]);

        void Add(string code, int count)
        {
            for (var index = 0; index < count; index++) records.Add(new { code, result = "Fail" });
        }
    }
}
