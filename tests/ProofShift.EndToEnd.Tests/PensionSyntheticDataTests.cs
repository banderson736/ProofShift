using ProofShift.Domain;
using ProofShift.Packs.Pension;
using Xunit;

namespace ProofShift.EndToEnd.Tests;

public sealed class PensionSyntheticDataTests
{
    [Fact]
    public void FastGeneratorIsVersionedDeterministicAndUsesStructurallyDifferentTargetConcepts()
    {
        var first = PensionSyntheticDatasetGenerator.Generate().ToArray();
        var second = PensionSyntheticDatasetGenerator.Generate().ToArray();
        var target = PensionTargetDataModel.Transform(first).ToArray();
        var repeatedTarget = PensionTargetDataModel.Transform(first).ToArray();

        Assert.Equal("proofshift-pension-generator-v1", PensionSyntheticDatasetGenerator.Version);
        Assert.Equal(PensionSyntheticDatasetGenerator.Fingerprint(), PensionSyntheticDatasetGenerator.Fingerprint());
        Assert.NotEqual(PensionSyntheticDatasetGenerator.Fingerprint(),
            PensionSyntheticDatasetGenerator.Fingerprint(seed: PensionSyntheticDatasetGenerator.DefaultSeed + 1));
        Assert.Equal(first, second);
        Assert.Equal(target, repeatedTarget);
        Assert.Equal(100L, first.LongCount(item => item.Kind == PensionRecordKind.Member));
        Assert.Equal(300L, first.LongCount(item => item.Kind == PensionRecordKind.Employment));
        Assert.Equal(5_000L, first.LongCount(item => item.Kind == PensionRecordKind.Contribution));
        Assert.Equal(350L, first.LongCount(item => item.Kind == PensionRecordKind.ServiceCredit));
        Assert.Equal(160L, first.LongCount(item => item.Kind == PensionRecordKind.Beneficiary));
        Assert.Equal(35L, first.LongCount(item => item.Kind == PensionRecordKind.RetirementElection));
        Assert.Equal(2_000L, first.LongCount(item => item.Kind == PensionRecordKind.BenefitPayment));
        Assert.Equal(250L, first.LongCount(item => item.Kind == PensionRecordKind.Document));
        Assert.Equal(25L, first.LongCount(item => item.Kind == PensionRecordKind.HistoricalExport));
        Assert.Equal(100L, target.LongCount(item => item.Kind == PensionRecordKind.Member));
        Assert.Equal("ADA LOVELACE", ((StringValue)target.Single(item => item.Identity == "M00000001").Values["display_name"]).Value);
        Assert.Equal("ACTIVE", ((StringValue)target.Single(item => item.Identity == "M00000001").Values["status"]).Value);
        Assert.Equal(["JOINED", "TERMINATED", "REINSTATED"], target.Where(item => item.Kind == PensionRecordKind.Employment)
            .Take(3).Select(item => ((StringValue)item.Values["event_code"]).Value));
        Assert.Equal(100_000, PensionDatasetScale.Large.Members);
        Assert.Equal(5_000_000, PensionDatasetScale.Large.Contributions);
        Assert.Equal(2_000_000, PensionDatasetScale.Large.BenefitPayments);
    }

    [Fact]
    public void DefectInjectorAppliesTheExactVersionedCorpusWithoutMutatingCleanInput()
    {
        var clean = PensionTargetDataModel.Transform(PensionSyntheticDatasetGenerator.Generate()).ToArray();
        var defective = PensionDefectInjector.InjectTargetDefects(clean).ToArray();
        var repeatedDefective = PensionDefectInjector.InjectTargetDefects(clean).ToArray();
        var counts = PensionDefectCounts.V1;

        Assert.Equal("proofshift-pension-defects-v1", PensionDefectInjector.Version);
        Assert.Equal(PensionDefectInjector.Version, PensionDefectCounts.Version);
        Assert.Equal(defective, repeatedDefective);
        Assert.Equal(149, counts.Total);
        Assert.Equal(counts.MissingMembers, MissingNaturalKeys(clean, defective, PensionRecordKind.Member));
        Assert.Equal(counts.DuplicateMembers, DuplicateNaturalKeys(defective, PensionRecordKind.Member, "member_id"));
        Assert.Equal(counts.WrongMemberStatuses, defective.Count(item => item.Kind == PensionRecordKind.Member &&
            item.Sequence is >= 12 and <= 14 && Text(item, "status") == "DECEASED"));
        Assert.Equal(counts.MissingEmploymentPeriods, MissingNaturalKeys(clean, defective, PensionRecordKind.Employment));
        Assert.Equal(counts.IncorrectEmploymentDates, ChangedValues(clean, defective, PensionRecordKind.Employment, "event_date"));
        Assert.Equal(counts.IncorrectServiceCreditTotals, ChangedValues(clean, defective, PensionRecordKind.ServiceCredit, "service_credit"));
        Assert.Equal(counts.IncorrectCodeTransformations, defective.Count(item => item.Kind == PensionRecordKind.ServiceCredit &&
            item.Sequence <= 5 && Text(item, "credit_code") == "UNKNOWN_CODE"));
        Assert.Equal(counts.MissingContributions, MissingNaturalKeys(clean, defective, PensionRecordKind.Contribution));
        Assert.Equal(counts.DuplicateContributions, DuplicateNaturalKeys(defective, PensionRecordKind.Contribution, "contribution_id"));
        Assert.Equal(counts.IncorrectContributionAmounts, ChangedValues(clean, defective, PensionRecordKind.Contribution, "contribution_amount"));
        Assert.Equal(counts.BenefitPaymentDiscrepancies, ChangedValues(clean, defective, PensionRecordKind.BenefitPayment, "paid_amount"));
        Assert.Equal(counts.BrokenBeneficiaryRelationships, defective.Count(item => item.Kind == PensionRecordKind.Beneficiary &&
            Text(item, "participant_id").StartsWith("MISSING-", StringComparison.Ordinal)));
        Assert.Equal(counts.WrongMemberBeneficiaries, defective.Count(item => item.Kind == PensionRecordKind.Beneficiary &&
            item.Sequence is 7 or 8 && Text(item, "participant_id") != $"M{item.Sequence:D8}"));
        Assert.Equal(counts.IncorrectRetirementElectionMappings, defective.Count(item => item.Kind == PensionRecordKind.RetirementElection &&
            item.Sequence <= 3 && Text(item, "option_code") == "WRONG_OPTION"));
        Assert.Equal(counts.MissingDocuments, MissingNaturalKeys(clean, defective, PensionRecordKind.Document));
        Assert.Equal(counts.WrongMemberDocuments, defective.Count(item => item.Kind == PensionRecordKind.Document &&
            item.Sequence is >= 10 and <= 13 && Text(item, "participant_id") != $"M{item.Sequence:D8}"));
        Assert.Equal(counts.MissingHistoricalExports, MissingNaturalKeys(clean, defective, PensionRecordKind.HistoricalExport));
        Assert.Equal(2, counts.FalseReversibleTransformations);
        Assert.Equal(100, clean.Count(item => item.Kind == PensionRecordKind.Member));
    }

    private static int MissingNaturalKeys(IEnumerable<PensionSyntheticRecord> clean,
        IEnumerable<PensionSyntheticRecord> changed, PensionRecordKind kind)
    {
        var actual = changed.Where(item => item.Kind == kind).Select(item => item.Identity).ToHashSet(StringComparer.Ordinal);
        return clean.Count(item => item.Kind == kind && !actual.Contains(item.Identity));
    }

    private static int DuplicateNaturalKeys(IEnumerable<PensionSyntheticRecord> records, PensionRecordKind kind, string field) =>
        records.Where(item => item.Kind == kind).GroupBy(item => Text(item, field), StringComparer.Ordinal)
            .Sum(group => group.Count() - 1);

    private static int ChangedValues(IEnumerable<PensionSyntheticRecord> clean,
        IEnumerable<PensionSyntheticRecord> changed, PensionRecordKind kind, string field)
    {
        var originals = clean.Where(item => item.Kind == kind).ToDictionary(item => item.Identity, StringComparer.Ordinal);
        return changed.Where(item => item.Kind == kind && originals.ContainsKey(item.Identity))
            .Count(item => !Equals(originals[item.Identity].Values[field], item.Values[field]));
    }

    private static string Text(PensionSyntheticRecord record, string field) =>
        ((StringValue)record.Values[field]).Value;
}
