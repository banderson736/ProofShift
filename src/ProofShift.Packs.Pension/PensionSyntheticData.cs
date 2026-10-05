using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Collections.ObjectModel;
using ProofShift.Domain;

namespace ProofShift.Packs.Pension;

public enum PensionRecordKind
{
    Member,
    Employment,
    Contribution,
    ServiceCredit,
    Beneficiary,
    RetirementElection,
    BenefitPayment,
    Document,
    HistoricalExport
}

public sealed record PensionDatasetScale
{
    public long Members { get; }
    public long EmploymentPeriods { get; }
    public long Contributions { get; }
    public long ServicePeriods { get; }
    public long Beneficiaries { get; }
    public long RetirementElections { get; }
    public long BenefitPayments { get; }
    public long Documents { get; }
    public long HistoricalExports { get; }
    public long GeneratedRecordCount => Members + EmploymentPeriods + Contributions + ServicePeriods + Beneficiaries +
        RetirementElections + BenefitPayments + Documents + HistoricalExports;

    public PensionDatasetScale(long members, long employmentPeriods, long contributions, long servicePeriods,
        long beneficiaries, long retirementElections, long benefitPayments, long documents, long historicalExports)
    {
        Members = Positive(members, nameof(members));
        EmploymentPeriods = Positive(employmentPeriods, nameof(employmentPeriods));
        Contributions = Positive(contributions, nameof(contributions));
        ServicePeriods = Positive(servicePeriods, nameof(servicePeriods));
        Beneficiaries = Positive(beneficiaries, nameof(beneficiaries));
        RetirementElections = Positive(retirementElections, nameof(retirementElections));
        BenefitPayments = Positive(benefitPayments, nameof(benefitPayments));
        Documents = Positive(documents, nameof(documents));
        HistoricalExports = Positive(historicalExports, nameof(historicalExports));
    }

    public static PensionDatasetScale Fast { get; } = new(100, 300, 5_000, 350, 160, 35, 2_000, 250, 25);
    public static PensionDatasetScale Medium { get; } = new(5_000, 15_000, 250_000, 17_500, 8_000, 1_750,
        100_000, 12_500, 1_250);
    public static PensionDatasetScale Large { get; } = new(100_000, 300_000, 5_000_000, 350_000,
        160_000, 35_000, 2_000_000, 250_000, 25_000);

    private static long Positive(long value, string name)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value, name);
        return value;
    }
}

public sealed record PensionSyntheticRecord
{
    public PensionRecordKind Kind { get; }
    public long Sequence { get; }
    public string SemanticType { get; }
    public string Identity { get; }
    public DomainDictionary<ValueNode> Values { get; }

    public PensionSyntheticRecord(PensionRecordKind kind, long sequence, string semanticType, string identity,
        IEnumerable<KeyValuePair<string, ValueNode>> values)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sequence);
        Kind = kind;
        Sequence = sequence;
        SemanticType = string.IsNullOrWhiteSpace(semanticType) ? throw new ArgumentException("Semantic type is required.", nameof(semanticType)) : semanticType.Trim();
        Identity = string.IsNullOrWhiteSpace(identity) ? throw new ArgumentException("Identity is required.", nameof(identity)) : identity.Trim();
        Values = new DomainDictionary<ValueNode>(values);
    }

    public PensionSyntheticRecord With(string field, ValueNode value, string? identity = null) =>
        new(Kind, Sequence, SemanticType, identity ?? Identity,
            Values.Where(pair => !string.Equals(pair.Key, field, StringComparison.Ordinal))
                .Append(new KeyValuePair<string, ValueNode>(field, value)));
}

public static class PensionSyntheticDatasetGenerator
{
    public const string Version = "proofshift-pension-generator-v1";
    public const long DefaultSeed = 20261003;

    public static IEnumerable<PensionSyntheticRecord> Generate(long seed = DefaultSeed, PensionDatasetScale? scale = null)
    {
        var dataset = scale ?? PensionDatasetScale.Fast;
        for (long index = 1; index <= dataset.Members; index++)
        {
            var status = ((index + Math.Abs(seed % 3)) % 3) switch
            {
                0 => "A",
                1 => "I",
                _ => "R"
            };
            var name = index == 1 ? "  ada lovelace  " : $"Member {index.ToString("D6", CultureInfo.InvariantCulture)} {Math.Abs(seed % 997).ToString(CultureInfo.InvariantCulture)}";
            yield return Record(PensionRecordKind.Member, index, "Pension.Member", $"M{index:D8}",
                ("member_id", Text($"M{index:D8}")), ("first_name", Text(name)), ("status_code", Text(status)),
                ("birth_date", new DateValue(new DateOnly(1940, 1, 1).AddDays((int)(index % 18_000)))),
                ("enrollment_date", new DateValue(new DateOnly(2001, 1, 1).AddDays((int)(index % 3_000)))));
        }

        for (long index = 1; index <= dataset.EmploymentPeriods; index++)
        {
            var member = (index - 1) / 3 + 1;
            var period = (int)((index - 1) % 3);
            var (start, end, status) = period switch
            {
                0 => (new DateOnly(2001, 1, 1), new DateOnly(2018, 6, 30), "ACTIVE"),
                1 => (new DateOnly(2018, 7, 1), new DateOnly(2019, 3, 31), "INACTIVE"),
                _ => (new DateOnly(2019, 4, 1), (DateOnly?)null, "ACTIVE")
            };
            yield return Record(PensionRecordKind.Employment, index, "Pension.Employment", $"E{index:D9}",
                ("employment_id", Text($"E{index:D9}")), ("member_id", Text($"M{member:D8}")),
                ("employer_id", Text($"EMP{member % 11:D2}")),
                ("period_index", Text(period.ToString(CultureInfo.InvariantCulture))), ("effective_from", new DateValue(start)),
                ("effective_to", end is null ? new NullValue() : new DateValue(end.Value)), ("status", Text(status)));
        }

        for (long index = 1; index <= dataset.Contributions; index++)
        {
            var member = (index - 1) % dataset.Members + 1;
            var month = (int)((index - 1) % 60);
            var period = new DateOnly(2021, 1, 1).AddMonths(month);
            var amount = 25m + ((index + Math.Abs(seed % 997)) % 25_000) / 100m;
            yield return Record(PensionRecordKind.Contribution, index, "Pension.Contribution", $"C{index:D10}",
                ("transaction_id", Text($"C{index:D10}")), ("member_id", Text($"M{member:D8}")),
                ("period", Text(period.ToString("yyyy-MM", CultureInfo.InvariantCulture))),
                ("category", Text(index % 2 == 0 ? "EMPLOYEE" : "EMPLOYER")),
                ("amount", new DecimalValue(amount)));
        }

        for (long index = 1; index <= dataset.ServicePeriods; index++)
        {
            var member = (index - 1) % dataset.Members + 1;
            var period = new DateOnly(2001, 1, 1).AddMonths((int)((index - 1) % 360));
            yield return Record(PensionRecordKind.ServiceCredit, index, "Pension.ServiceCredit", $"S{index:D9}",
                ("service_id", Text($"S{index:D9}")), ("member_id", Text($"M{member:D8}")),
                ("period_from", new DateValue(period)), ("period_to", new DateValue(period.AddMonths(1).AddDays(-1))),
                ("credit", new DecimalValue(1m)), ("credit_type", Text(index % 2 == 0 ? "REGULAR" : "PURCHASED")));
        }

        for (long index = 1; index <= dataset.Beneficiaries; index++)
        {
            var member = dataset.Members > 7 ? 8 + (index - 1) % (dataset.Members - 7) : (index - 1) % dataset.Members + 1;
            yield return Record(PensionRecordKind.Beneficiary, index, "Pension.Beneficiary", $"B{index:D8}",
                ("beneficiary_id", Text($"B{index:D8}")), ("member_id", Text($"M{member:D8}")),
                ("relationship", Text(index % 2 == 0 ? "SPOUSE" : "CHILD")),
                ("allocation", new DecimalValue(index % 3 == 0 ? 0.25m : 0.5m)));
        }

        for (long index = 1; index <= dataset.RetirementElections; index++)
        {
            var member = (index - 1) % dataset.Members + 1;
            yield return Record(PensionRecordKind.RetirementElection, index, "Pension.RetirementElection", $"R{index:D7}",
                ("election_id", Text($"R{index:D7}")), ("member_id", Text($"M{member:D8}")),
                ("election_code", Text(index % 2 == 0 ? "J50" : "SINGLE")),
                ("effective_date", new DateValue(new DateOnly(2024, 1, 1).AddDays((int)index))));
        }

        for (long index = 1; index <= dataset.BenefitPayments; index++)
        {
            var member = (index - 1) % dataset.Members + 1;
            var period = new DateOnly(2022, 1, 1).AddMonths((int)((index - 1) % 48));
            yield return Record(PensionRecordKind.BenefitPayment, index, "Pension.BenefitPayment", $"P{index:D10}",
                ("payment_id", Text($"P{index:D10}")), ("member_id", Text($"M{member:D8}")),
                ("period", Text(period.ToString("yyyy-MM", CultureInfo.InvariantCulture))),
                ("payment_date", new DateValue(period.AddMonths(1).AddDays(4))),
                ("amount", new DecimalValue(700m + (index % 30_000) / 100m)));
        }

        for (long index = 1; index <= dataset.Documents; index++)
        {
            var member = (index - 1) % dataset.Members + 1;
            var identity = $"D{index:D8}";
            var contentHash = Hash($"{seed}|document|{index}");
            yield return Record(PensionRecordKind.Document, index, "Pension.Document", identity,
                ("document_id", Text(identity)), ("member_id", Text($"M{member:D8}")),
                ("category", Text(index % 2 == 0 ? "STATEMENT" : "SERVICE_RECORD")),
                ("relative_path", Text($"member/{member:D8}/{identity}.bin")), ("content_hash", Text(contentHash)));
        }

        for (long index = 1; index <= dataset.HistoricalExports; index++)
        {
            var member = (index - 1) % dataset.Members + 1;
            var identity = $"X{index:D8}";
            yield return Record(PensionRecordKind.HistoricalExport, index, "Pension.HistoricalExport", identity,
                ("export_id", Text(identity)), ("member_id", Text($"M{member:D8}")),
                ("relative_path", Text($"history/{member:D8}/{identity}.csv")),
                ("content_hash", Text(Hash($"{seed}|export|{index}"))));
        }
    }

    public static string Fingerprint(long seed = DefaultSeed, PensionDatasetScale? scale = null)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, Version);
        Append(hash, seed.ToString(CultureInfo.InvariantCulture));
        foreach (var record in Generate(seed, scale))
        {
            Append(hash, record.Kind.ToString());
            Append(hash, record.SemanticType);
            Append(hash, record.Identity);
            foreach (var pair in record.Values.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                Append(hash, pair.Key);
                Append(hash, CanonicalValue(pair.Value));
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static PensionSyntheticRecord Record(PensionRecordKind kind, long sequence, string semanticType,
        string identity, params (string Name, ValueNode Value)[] values) =>
        new(kind, sequence, semanticType, identity,
            values.Select(pair => new KeyValuePair<string, ValueNode>(pair.Name, pair.Value)));

    private static StringValue Text(string value) => new(value);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    internal static string CanonicalValue(ValueNode value) => value switch
    {
        NullValue => "null",
        StringValue item => $"s:{item.Value}",
        IntegerValue item => $"i:{item.Value.ToString(CultureInfo.InvariantCulture)}",
        DecimalValue item => $"d:{item.Value.ToString("G29", CultureInfo.InvariantCulture)}",
        BooleanValue item => item.Value ? "b:1" : "b:0",
        DateValue item => $"date:{item.Value:yyyy-MM-dd}",
        _ => value.GetType().Name
    };

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
}

public static class PensionTargetDataModel
{
    public const string Version = "proofshift-pension-target-model-v1";

    public static IEnumerable<PensionSyntheticRecord> Transform(IEnumerable<PensionSyntheticRecord> sourceRecords)
    {
        foreach (var source in sourceRecords)
        {
            IEnumerable<KeyValuePair<string, ValueNode>> values = source.Kind switch
            {
                PensionRecordKind.Member => Map(source,
                    ("member_id", "member_id"), ("first_name", "display_name"), ("status_code", "status"), ("birth_date", "birth_date"), ("enrollment_date", "joined_date")),
                PensionRecordKind.Employment => Employment(source),
                PensionRecordKind.Contribution => Map(source,
                    ("transaction_id", "contribution_id"), ("member_id", "participant_id"), ("period", "payroll_period"), ("category", "contribution_kind"), ("amount", "contribution_amount")),
                PensionRecordKind.ServiceCredit => Map(source,
                    ("service_id", "service_period_id"), ("member_id", "participant_id"), ("period_from", "service_from"), ("period_to", "service_to"), ("credit", "service_credit"), ("credit_type", "credit_code")),
                PensionRecordKind.Beneficiary => Map(source,
                    ("beneficiary_id", "beneficiary_id"), ("member_id", "participant_id"), ("relationship", "relationship_type"), ("allocation", "allocation_pct")),
                PensionRecordKind.RetirementElection => Map(source,
                    [("election_id", "election_id"), ("member_id", "participant_id"), ("election_code", "option_code"), ("effective_date", "effective_date")], codeMap: true),
                PensionRecordKind.BenefitPayment => Map(source,
                    ("payment_id", "payment_id"), ("member_id", "participant_id"), ("period", "payment_period"), ("payment_date", "paid_on"), ("amount", "paid_amount")),
                PensionRecordKind.Document => Map(source,
                    ("document_id", "document_id"), ("member_id", "participant_id"), ("category", "document_type"), ("relative_path", "object_key"), ("content_hash", "content_hash")),
                PensionRecordKind.HistoricalExport => Map(source,
                    ("export_id", "export_id"), ("member_id", "participant_id"), ("relative_path", "object_key"), ("content_hash", "content_hash")),
                _ => throw new ArgumentOutOfRangeException(nameof(sourceRecords))
            };
            yield return new PensionSyntheticRecord(source.Kind, source.Sequence, source.SemanticType, source.Identity, values);
        }
    }

    private static KeyValuePair<string, ValueNode>[] Employment(PensionSyntheticRecord source)
    {
        var index = int.Parse(((StringValue)source.Values["period_index"]).Value, CultureInfo.InvariantCulture);
        var status = ((StringValue)source.Values["status"]).Value;
        var eventCode = index switch { 0 => "JOINED", 1 => "TERMINATED", _ => "REINSTATED" };
        return [
            new("source_employment_ref", source.Values["employment_id"]),
            new("participant_id", source.Values["member_id"]),
            new("event_date", source.Values["effective_from"]),
            new("event_code", new StringValue(eventCode)),
            new("source_state", new StringValue(status))
        ];
    }

    private static List<KeyValuePair<string, ValueNode>> Map(PensionSyntheticRecord source,
        params (string Source, string Target)[] fields) => Map(source, fields, codeMap: false);

    private static List<KeyValuePair<string, ValueNode>> Map(PensionSyntheticRecord source,
        (string Source, string Target)[] fields, bool codeMap)
    {
        var values = fields.Select(field => new KeyValuePair<string, ValueNode>(field.Target, source.Values[field.Source])).ToList();
        if (source.Kind == PensionRecordKind.Member)
        {
            var rawName = ((StringValue)source.Values["first_name"]).Value;
            values[1] = new KeyValuePair<string, ValueNode>("display_name", new StringValue(rawName.Trim().ToUpperInvariant()));
            var sourceStatus = ((StringValue)source.Values["status_code"]).Value;
            values[2] = new KeyValuePair<string, ValueNode>("status", new StringValue(sourceStatus switch { "A" => "ACTIVE", "I" => "INACTIVE", "R" => "RETIRED", _ => "UNKNOWN" }));
        }
        if (codeMap)
        {
            var code = ((StringValue)source.Values["election_code"]).Value;
            var index = values.FindIndex(pair => pair.Key == "option_code");
            values[index] = new KeyValuePair<string, ValueNode>("option_code", new StringValue(code switch { "J50" => "JOINT_SURVIVOR_50", "SINGLE" => "SINGLE_LIFE", _ => "UNMAPPED" }));
        }
        return values;
    }
}

public sealed record PensionDefectCounts(
    int MissingMembers,
    int DuplicateMembers,
    int WrongMemberStatuses,
    int MissingEmploymentPeriods,
    int IncorrectEmploymentDates,
    int IncorrectServiceCreditTotals,
    int MissingContributions,
    int DuplicateContributions,
    int IncorrectContributionAmounts,
    int BenefitPaymentDiscrepancies,
    int BrokenBeneficiaryRelationships,
    int WrongMemberBeneficiaries,
    int IncorrectRetirementElectionMappings,
    int IncorrectCodeTransformations,
    int MissingDocuments,
    int WrongMemberDocuments,
    int MissingHistoricalExports,
    int FalseReversibleTransformations)
{
    public static string Version => "proofshift-pension-defects-v1";
    public static PensionDefectCounts V1 { get; } = new(7, 4, 3, 12, 8, 17, 39, 8, 6, 11, 6, 2, 3, 5, 9, 4, 3, 2);
    public int Total => MissingMembers + DuplicateMembers + WrongMemberStatuses + MissingEmploymentPeriods +
        IncorrectEmploymentDates + IncorrectServiceCreditTotals + MissingContributions + DuplicateContributions +
        IncorrectContributionAmounts + BenefitPaymentDiscrepancies + BrokenBeneficiaryRelationships +
        WrongMemberBeneficiaries + IncorrectRetirementElectionMappings + IncorrectCodeTransformations +
        MissingDocuments + WrongMemberDocuments + MissingHistoricalExports + FalseReversibleTransformations;
}

public static class PensionDefectInjector
{
    public const string Version = "proofshift-pension-defects-v1";
    public static IReadOnlyList<PensionFalseReverseDeclaration> FalseReverseDeclarations { get; } =
    [
        new("member-name-normalization", "trim and normalize member names"),
        new("member-status-code-map-pass-through", "unknown-code pass-through may collide with a configured target code")
    ];

    public static IEnumerable<PensionSyntheticRecord> InjectTargetDefects(IEnumerable<PensionSyntheticRecord> correctedTarget)
    {
        ArgumentNullException.ThrowIfNull(correctedTarget);
        foreach (var record in correctedTarget)
        {
            var number = record.Sequence;
            switch (record.Kind)
            {
                case PensionRecordKind.Member:
                    if (number <= 7) continue;
                    yield return number is >= 12 and <= 14
                        ? record.With("status", new StringValue("DECEASED"))
                        : record;
                    if (number is >= 8 and <= 11)
                        yield return record.With("fixture_duplicate", new StringValue("true"), $"{record.Identity}:duplicate");
                    break;
                case PensionRecordKind.Employment:
                    if (number <= 12) continue;
                    yield return number is >= 13 and <= 20 && record.Values.TryGetValue("event_date", out var eventDate) && eventDate is DateValue date
                        ? record.With("event_date", new DateValue(date.Value.AddDays(1)))
                        : record;
                    break;
                case PensionRecordKind.ServiceCredit:
                    var service = number <= 17 && record.Values.TryGetValue("service_credit", out var credit) && credit is DecimalValue amount
                        ? record.With("service_credit", new DecimalValue(amount.Value + 0.05m))
                        : record;
                    yield return number <= 5 ? service.With("credit_code", new StringValue("UNKNOWN_CODE")) : service;
                    break;
                case PensionRecordKind.Contribution:
                    if (number <= 39) continue;
                    var contribution = number is >= 48 and <= 53 && record.Values.TryGetValue("contribution_amount", out var contributionValue) && contributionValue is DecimalValue contributionAmount
                        ? record.With("contribution_amount", new DecimalValue(contributionAmount.Value + 0.02m))
                        : record;
                    yield return contribution;
                    if (number is >= 40 and <= 47)
                        yield return record.With("fixture_duplicate", new StringValue("true"), $"{record.Identity}:duplicate");
                    break;
                case PensionRecordKind.Beneficiary:
                    if (number <= 6)
                        yield return record.With("participant_id", new StringValue($"MISSING-{number:D4}"));
                    else if (number <= 8)
                        yield return record.With("participant_id", new StringValue($"M{number + 1:D8}"));
                    else
                        yield return record;
                    break;
                case PensionRecordKind.RetirementElection:
                    yield return number <= 3 ? record.With("option_code", new StringValue("WRONG_OPTION")) : record;
                    break;
                case PensionRecordKind.BenefitPayment:
                    yield return number <= 11 && record.Values.TryGetValue("paid_amount", out var paymentValue) && paymentValue is DecimalValue paymentAmount
                        ? record.With("paid_amount", new DecimalValue(paymentAmount.Value + 0.03m))
                        : record;
                    break;
                case PensionRecordKind.Document:
                    if (number <= 9) continue;
                    yield return number is >= 10 and <= 13
                        ? record.With("participant_id", new StringValue($"M{number + 10:D8}"))
                        : record;
                    break;
                case PensionRecordKind.HistoricalExport:
                    if (number > 3) yield return record;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(correctedTarget));
            }
        }
    }
}

public sealed record PensionFalseReverseDeclaration(string EdgeName, string LossDescription);