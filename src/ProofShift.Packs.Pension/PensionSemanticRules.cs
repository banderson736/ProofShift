using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ProofShift.Domain;
using ProofShift.Verification;

namespace ProofShift.Packs.Pension;

public abstract class PensionRuleBase(VerificationRuleDefinition definition) : VerificationRuleBase(definition)
{
    protected sealed record PensionRecordGroup(string Key, IReadOnlyList<VerificationArtifactRecord> Records);

    public override IReadOnlyCollection<VerificationOrderingKey> RequiredOrderingKeys => Definition.Options
        .Where(pair => pair.Key.EndsWith("Field", StringComparison.Ordinal) || pair.Key is "groupBy" or "businessKey")
        .SelectMany(pair => pair.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(orderingField => new VerificationOrderingKey(Option("semanticType"), orderingField,
                pair.Key is "groupBy" or "businessKey" ? VerificationOrderingRole.Grouping : VerificationOrderingRole.Ordering)))
        .Distinct().OrderBy(key => key.SemanticType, StringComparer.Ordinal).ThenBy(key => key.Field, StringComparer.Ordinal).ToArray();

    protected static async IAsyncEnumerable<PensionRecordGroup> ReadRecordGroupsAsync(VerificationExecutionContext context,
        VerificationArtifactRole role, string node, string semanticType, string[] groupFields,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? currentKey = null;
        var group = new List<VerificationArtifactRecord>();
        var orderingKeys = groupFields.Select(field => new VerificationOrderingKey(semanticType, field,
            VerificationOrderingRole.Grouping)).ToArray();
        await foreach (var record in context.Workspace.ReadArtifactRecordsByKeysAsync(role, node, semanticType,
            orderingKeys, cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = groupFields.Length == 0 ? record.Artifact.Identity : CompositeKey(record, groupFields);
            if (currentKey is not null && !string.Equals(currentKey, key, StringComparison.Ordinal))
            {
                yield return new PensionRecordGroup(currentKey, group);
                group = [];
            }
            currentKey = key;
            group.Add(record);
        }
        if (currentKey is not null) yield return new PensionRecordGroup(currentKey, group);
    }

    protected static async IAsyncEnumerable<(PensionRecordGroup? Expected, PensionRecordGroup? Actual)> MergeGroupsAsync(
        IAsyncEnumerable<PensionRecordGroup> expectedGroups, IAsyncEnumerable<PensionRecordGroup> actualGroups,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var expectedEnumerator = expectedGroups.GetAsyncEnumerator(cancellationToken);
        await using var actualEnumerator = actualGroups.GetAsyncEnumerator(cancellationToken);
        var hasExpected = await expectedEnumerator.MoveNextAsync().ConfigureAwait(false);
        var hasActual = await actualEnumerator.MoveNextAsync().ConfigureAwait(false);
        while (hasExpected || hasActual)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!hasActual || hasExpected && StringComparer.Ordinal.Compare(expectedEnumerator.Current.Key, actualEnumerator.Current.Key) < 0)
            {
                yield return (expectedEnumerator.Current, null);
                hasExpected = await expectedEnumerator.MoveNextAsync().ConfigureAwait(false);
            }
            else if (!hasExpected || StringComparer.Ordinal.Compare(expectedEnumerator.Current.Key, actualEnumerator.Current.Key) > 0)
            {
                yield return (null, actualEnumerator.Current);
                hasActual = await actualEnumerator.MoveNextAsync().ConfigureAwait(false);
            }
            else
            {
                yield return (expectedEnumerator.Current, actualEnumerator.Current);
                hasExpected = await expectedEnumerator.MoveNextAsync().ConfigureAwait(false);
                hasActual = await actualEnumerator.MoveNextAsync().ConfigureAwait(false);
            }
        }
    }

    protected string SourceNode => Option("sourceNode");
    protected string TargetNode => Option("targetNode");
    protected string SemanticType => Option("semanticType");

    protected static string FieldText(VerificationArtifactRecord record, string field) =>
        record.Values.TryGetValue(field, out var value) ? ValueText(value) : string.Empty;

    protected static string ValueText(ValueNode value) => value switch
    {
        NullValue => string.Empty,
        StringValue item => item.Value,
        IntegerValue item => item.Value.ToString(CultureInfo.InvariantCulture),
        DecimalValue item => item.Value.ToString("G29", CultureInfo.InvariantCulture),
        BooleanValue item => item.Value ? "true" : "false",
        DateValue item => item.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        InstantValue item => item.Value.ToString("O", CultureInfo.InvariantCulture),
        OffsetDateTimeValue item => item.Value.ToString("O", CultureInfo.InvariantCulture),
        LocalDateTimeValue item => item.Value.ToString("O", CultureInfo.InvariantCulture),
        BinaryReferenceValue item => item.Sha256,
        _ => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.GetType().Name))).ToLowerInvariant()
    };

    protected static decimal? DecimalValue(VerificationArtifactRecord record, string field)
    {
        if (!record.Values.TryGetValue(field, out var value)) return null;
        return value switch
        {
            DecimalValue item => item.Value,
            IntegerValue item => item.Value,
            StringValue item when decimal.TryParse(item.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };
    }

    protected static DateOnly? DateValue(VerificationArtifactRecord record, string field)
    {
        if (!record.Values.TryGetValue(field, out var value)) return null;
        return value switch
        {
            DateValue item => item.Value,
            LocalDateTimeValue item => DateOnly.FromDateTime(item.Value),
            InstantValue item => DateOnly.FromDateTime(item.Value.UtcDateTime),
            OffsetDateTimeValue item => DateOnly.FromDateTime(item.Value.DateTime),
            StringValue item when DateOnly.TryParseExact(item.Value, ["yyyy-MM-dd", "yyyyMMdd"], CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsed) => parsed,
            _ => null
        };
    }

    protected static string RecordKey(VerificationArtifactRecord record, string field) =>
        string.IsNullOrWhiteSpace(field) ? record.Artifact.Identity : FieldText(record, field);

    protected static string SafeKey(params string[] values) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\u001f', values)))).ToLowerInvariant();

    protected static List<EvidenceReference> RecordInputs(VerificationExecutionContext context,
        params VerificationArtifactRecord?[] records)
    {
        var references = new List<EvidenceReference>();
        foreach (var record in records.Where(record => record is not null))
            references.Add(ArtifactInput(context, record!.NodeKey, record.Artifact));
        return references;
    }

    protected VerificationFinding Fail(VerificationExecutionContext context, EvidenceType type, string code,
        string stableKey, string explanation, IEnumerable<EvidenceReference> inputs,
        EvidenceValue? expected = null, EvidenceValue? actual = null) =>
        Finding(context, type, EvidenceResult.Fail, code, $"{Id.Value}:{stableKey}", explanation, inputs, expected, actual);

    protected VerificationFinding Pass(VerificationExecutionContext context, EvidenceType type, string code,
        string explanation, IEnumerable<EvidenceReference>? inputs = null) =>
        Finding(context, type, EvidenceResult.Pass, code, $"{Id.Value}:pass", explanation, inputs);

    protected decimal Tolerance(string option = "tolerance")
    {
        var text = Option(option, "0.01");
        if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var tolerance) || tolerance < 0)
            throw new VerificationRuleException(VerificationIssueCodes.UnknownRuleType,
                $"Pension rule '{Id.Value}' requires a non-negative decimal {option}.");
        return tolerance;
    }

    protected static string[] Fields(string value, string[] defaults) => string.IsNullOrWhiteSpace(value)
        ? defaults
        : value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    protected static string CompositeKey(VerificationArtifactRecord record, IReadOnlyCollection<string> fields) =>
        string.Join('\u001f', fields.Select(field => FieldText(record, field)));

    protected static bool SameFields(VerificationArtifactRecord left, VerificationArtifactRecord right,
        IEnumerable<string> fields) => fields.All(field => string.Equals(FieldText(left, field), FieldText(right, field), StringComparison.Ordinal));

    protected static EvidenceValue FinancialValue(string groupingFingerprint, long count, decimal total,
        decimal tolerance, decimal? difference = null) => new(new ObjectValue(new Dictionary<string, ValueNode>
        {
            ["groupFingerprint"] = new StringValue($"sha256:{groupingFingerprint}"),
            ["count"] = new IntegerValue(count),
            ["total"] = new DecimalValue(total),
            ["tolerance"] = new DecimalValue(tolerance),
            ["difference"] = difference is null ? new NullValue() : new DecimalValue(difference.Value)
        }));
}

public sealed class MemberUniquenessRule(VerificationRuleDefinition definition) : PensionRuleBase(definition)
{
    public override VerificationScope Scope => VerificationScope.Entity;

    public override async IAsyncEnumerable<VerificationFinding> EvaluateAsync(VerificationExecutionContext context,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var keyField = Option("businessKey");
        var duplicateCode = keyField.Length == 0 ? "DuplicateTarget" : "DuplicateMember";
        var duplicates = 0;
        var groups = ReadRecordGroupsAsync(context, VerificationArtifactRole.ActualTarget, TargetNode,
            Option("semanticType", "Pension.Member"), keyField.Length == 0 ? [] : [keyField], cancellationToken);
        await foreach (var group in groups.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (group.Records.Count <= 1) continue;
            duplicates += group.Records.Count - 1;
            foreach (var duplicate in group.Records.Skip(1))
                yield return Fail(context, EvidenceType.Comparison, duplicateCode,
                    SafeKey(group.Key, duplicate.Artifact.Id.Value), "A target member business identity occurs more than once.",
                    RecordInputs(context, duplicate), new EvidenceValue(new IntegerValue(1)),
                    new EvidenceValue(new IntegerValue(group.Records.Count)));
        }

        if (duplicates == 0)
            yield return Pass(context, EvidenceType.Comparison, "MemberUniqueness", "Target member business identities are unique.");
    }
}

public sealed class PensionMemberPresenceRule(VerificationRuleDefinition definition) : PensionRuleBase(definition)
{
    public override VerificationScope Scope => VerificationScope.Entity;

    public override async IAsyncEnumerable<VerificationFinding> EvaluateAsync(VerificationExecutionContext context,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var semantic = Option("semanticType", "Pension.Member");
        var keyField = Option("businessKey", "member_id");
        var expected = ReadRecordGroupsAsync(context, VerificationArtifactRole.ExpectedTarget, TargetNode, semantic, [keyField], cancellationToken);
        var actual = ReadRecordGroupsAsync(context, VerificationArtifactRole.ActualTarget, TargetNode, semantic, [keyField], cancellationToken);
        var missing = 0;
        await foreach (var pair in MergeGroupsAsync(expected, actual, cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (pair.Expected is null || pair.Actual is not null) continue;
            missing++;
            yield return Fail(context, EvidenceType.Comparison, "MissingMember",
                SafeKey(pair.Expected.Key), "A source-derived pension member is absent from the target.",
                RecordInputs(context, pair.Expected.Records[0]), new EvidenceValue(new StringValue("present")),
                new EvidenceValue(new StringValue("missing")));
        }

        if (missing == 0)
            yield return Pass(context, EvidenceType.Comparison, "MemberPresence", "Every expected pension member is present in the target.");
    }
}

public sealed class PensionMemberStatusRule(VerificationRuleDefinition definition) : PensionRuleBase(definition)
{
    public override VerificationScope Scope => VerificationScope.Attribute;

    public override async IAsyncEnumerable<VerificationFinding> EvaluateAsync(VerificationExecutionContext context,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var semantic = Option("semanticType", "Pension.Member");
        var keyField = Option("businessKey", "member_id");
        var field = Option("attribute", "status");
        var expected = ReadRecordGroupsAsync(context, VerificationArtifactRole.ExpectedTarget, TargetNode, semantic, [keyField], cancellationToken);
        var actual = ReadRecordGroupsAsync(context, VerificationArtifactRole.ActualTarget, TargetNode, semantic, [keyField], cancellationToken);
        var mismatches = 0;
        await foreach (var pair in MergeGroupsAsync(expected, actual, cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (pair.Expected is null || pair.Actual is null || pair.Expected.Records.Count != 1 || pair.Actual.Records.Count != 1) continue;
            var key = pair.Expected.Key;
            var expectedRecord = pair.Expected.Records[0];
            var actualRecord = pair.Actual.Records[0];
            var expectedStatus = FieldText(expectedRecord, field);
            var actualStatus = FieldText(actualRecord, field);
            if (string.Equals(expectedStatus, actualStatus, StringComparison.Ordinal)) continue;
            mismatches++;
            yield return Fail(context, EvidenceType.Comparison, "WrongMemberStatus", SafeKey(key),
                "Target member status differs from the declared source-code mapping.",
                RecordInputs(context, expectedRecord, actualRecord),
                new EvidenceValue(new StringValue($"sha256:{SafeKey(expectedStatus)}")),
                new EvidenceValue(new StringValue($"sha256:{SafeKey(actualStatus)}")));
        }

        if (mismatches == 0)
            yield return Pass(context, EvidenceType.Comparison, "MemberStatus", "All mapped member statuses match their source-derived values.");
    }
}

public sealed class EmploymentTimelineRule(VerificationRuleDefinition definition) : PensionRuleBase(definition)
{
    public override VerificationScope Scope => VerificationScope.Timeline;

    public override async IAsyncEnumerable<VerificationFinding> EvaluateAsync(VerificationExecutionContext context,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var sourceNode = SourceNode;
        var targetNode = TargetNode;
        var sourceMemberField = Option("sourceMemberField", "member_id");
        var targetMemberField = Option("targetMemberField", "participant_id");
        var sourceGroups = ReadRecordGroupsAsync(context, VerificationArtifactRole.Source, sourceNode,
            Option("semanticType", "Pension.Employment"), [sourceMemberField], cancellationToken);
        var targetGroups = ReadRecordGroupsAsync(context, VerificationArtifactRole.ActualTarget, targetNode,
            Option("targetSemanticType", "Pension.Employment"), [targetMemberField], cancellationToken);
        var mismatches = 0;
        var allowGaps = string.Equals(Option("allowGaps", "false"), "true", StringComparison.OrdinalIgnoreCase);
        var sourceStartField = Option("sourceStartField", "effective_from");
        var sourceEndField = Option("sourceEndField", "effective_to");
        var sourceStatusField = Option("sourceStatusField", "status");
        var eventDateField = Option("targetDateField", "event_date");
        var eventCodeField = Option("targetEventField", "event_code");
        var joined = Option("joinedEvent", "JOINED");
        var terminated = Option("terminatedEvent", "TERMINATED");
        var reinstated = Option("reinstatedEvent", "REINSTATED");

        await foreach (var pair in MergeGroupsAsync(sourceGroups, targetGroups, cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var member = pair.Expected?.Key ?? pair.Actual!.Key;
            var periods = pair.Expected?.Records ?? [];
            if (pair.Expected is null)
            {
                foreach (var extra in pair.Actual!.Records)
                {
                    mismatches++;
                    yield return Fail(context, EvidenceType.Timeline, "UnexpectedEmploymentTransition",
                        SafeKey(member, extra.Artifact.Identity), "Target contains employment history for a member absent from source history.",
                        RecordInputs(context, extra));
                }
                continue;
            }
            if (member.Length == 0) continue;
            var ordered = periods.OrderBy(record => DateValue(record, sourceStartField)).ToArray();
            var previousEnd = (DateOnly?)null;
            var previousWasOpen = false;
            var expectedEvents = new List<(DateOnly Date, string Code, VerificationArtifactRecord Record)>();
            string? previousStatus = null;
            foreach (var period in ordered)
            {
                var start = DateValue(period, sourceStartField);
                var end = DateValue(period, sourceEndField);
                var status = FieldText(period, sourceStatusField);
                if (start is null || status.Length == 0)
                {
                    mismatches++;
                    yield return Fail(context, EvidenceType.Timeline, "InvalidEmploymentPeriod",
                        SafeKey(member, period.Artifact.Identity), "An employment period lacks a valid effective start or state.",
                        RecordInputs(context, period));
                    continue;
                }
                if (previousWasOpen || previousEnd is { } endValue && start.Value <= endValue)
                {
                    mismatches++;
                    yield return Fail(context, EvidenceType.Timeline, "OverlappingEmploymentPeriods",
                        SafeKey(member, start.Value.ToString("O", CultureInfo.InvariantCulture)),
                        "Employment periods overlap or follow an open-ended period.", RecordInputs(context, period));
                }
                else if (!allowGaps && previousEnd is { } priorEnd && start.Value > priorEnd.AddDays(1))
                {
                    mismatches++;
                    yield return Fail(context, EvidenceType.Timeline, "EmploymentTimelineGap",
                        SafeKey(member, priorEnd.ToString("O", CultureInfo.InvariantCulture), start.Value.ToString("O", CultureInfo.InvariantCulture)),
                        "An employment timeline contains an unexplained gap.", RecordInputs(context, period));
                }
                var state = string.Equals(status, "ACTIVE", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(status, "A", StringComparison.OrdinalIgnoreCase);
                var wasActive = previousStatus is not null &&
                    (string.Equals(previousStatus, "ACTIVE", StringComparison.OrdinalIgnoreCase) || string.Equals(previousStatus, "A", StringComparison.OrdinalIgnoreCase));
                var code = previousStatus is null ? (state ? joined : terminated) : state == wasActive ? null : state ? reinstated : terminated;
                if (code is not null) expectedEvents.Add((start.Value, code, period));
                previousStatus = status;
                previousEnd = end;
                previousWasOpen = end is null;
            }

            var actualEvents = (pair.Actual?.Records ?? []).Select(record => (Date: DateValue(record, eventDateField),
                    Code: FieldText(record, eventCodeField), Record: record))
                .OrderBy(item => item.Date).ToArray();
            for (var index = 0; index < Math.Max(expectedEvents.Count, actualEvents.Length); index++)
            {
                if (index >= expectedEvents.Count)
                {
                    mismatches++;
                    var extra = actualEvents[index].Record;
                    yield return Fail(context, EvidenceType.Timeline, "UnexpectedEmploymentTransition",
                        SafeKey(member, extra.Artifact.Identity), "Target contains an employment state transition absent from source history.",
                        RecordInputs(context, extra));
                    continue;
                }
                var expectedEvent = expectedEvents[index];
                if (index >= actualEvents.Length)
                {
                    mismatches++;
                    yield return Fail(context, EvidenceType.Timeline, "MissingEmploymentPeriod",
                        SafeKey(member, expectedEvent.Date.ToString("O", CultureInfo.InvariantCulture), expectedEvent.Code),
                        "A source employment state transition has no equivalent target event.",
                        RecordInputs(context, expectedEvent.Record), new EvidenceValue(new StringValue(expectedEvent.Code)),
                        new EvidenceValue(new StringValue("missing")));
                    continue;
                }
                var actualEvent = actualEvents[index];
                if (actualEvent.Date == expectedEvent.Date && string.Equals(actualEvent.Code, expectedEvent.Code, StringComparison.Ordinal)) continue;
                mismatches++;
                var code = actualEvent.Date != expectedEvent.Date ? "IncorrectEmploymentDate" : "EmploymentStateTransitionMismatch";
                yield return Fail(context, EvidenceType.Timeline, code,
                    SafeKey(member, expectedEvent.Date.ToString("O", CultureInfo.InvariantCulture), expectedEvent.Code),
                    "The target event timeline differs from the source-derived employment transition.",
                    RecordInputs(context, expectedEvent.Record, actualEvent.Record),
                    new EvidenceValue(new StringValue($"{expectedEvent.Date:yyyy-MM-dd}:{SafeKey(expectedEvent.Code)}")),
                    new EvidenceValue(new StringValue(actualEvent.Date is null ? "invalid-date" : $"{actualEvent.Date:yyyy-MM-dd}:{SafeKey(actualEvent.Code)}")));
            }
        }

        if (mismatches == 0)
            yield return Pass(context, EvidenceType.Timeline, "EmploymentTimelineEquivalent",
                "Source employment intervals and target state events describe equivalent, gap-free timelines.");
    }
}

public sealed class ContributionAccountingRule(VerificationRuleDefinition definition) : PensionRuleBase(definition)
{
    public override VerificationScope Scope => VerificationScope.Accounting;

    public override async IAsyncEnumerable<VerificationFinding> EvaluateAsync(VerificationExecutionContext context,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var semantic = Option("semanticType", "Pension.Contribution");
        var target = TargetNode;
        var keyField = Option("transactionField", "transaction_id");
        var amountField = Option("amountField", "amount");
        var comparisonFields = Fields(Option("compareFields"), [keyField, "member_id", "period", "category"]);
        var tolerance = Tolerance();
        var expected = ReadRecordGroupsAsync(context, VerificationArtifactRole.ExpectedTarget, target, semantic, [keyField], cancellationToken);
        var actual = ReadRecordGroupsAsync(context, VerificationArtifactRole.ActualTarget, target, semantic, [keyField], cancellationToken);
        var discrepancyCount = 0;
        await foreach (var pair in MergeGroupsAsync(expected, actual, cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (pair.Expected is null) continue;
            var key = pair.Expected.Key;
            var expectedRows = pair.Expected.Records;
            var actualRows = pair.Actual?.Records ?? [];
            if (actualRows.Count == 0)
            {
                discrepancyCount++;
                yield return Fail(context, EvidenceType.Accounting, "MissingContribution", SafeKey(key),
                    "A source-derived contribution transaction is absent from the target.", RecordInputs(context, expectedRows[0]),
                    new EvidenceValue(new IntegerValue(1)), new EvidenceValue(new IntegerValue(0)));
                continue;
            }
            if (actualRows.Count > 1)
            {
                discrepancyCount += actualRows.Count - 1;
                yield return Fail(context, EvidenceType.Accounting, "DuplicateContribution", SafeKey(key),
                    "A contribution business identity occurs multiple times in the target.",
                    RecordInputs(context, expectedRows[0], actualRows[0]),
                    new EvidenceValue(new IntegerValue(1)), new EvidenceValue(new IntegerValue(actualRows.Count)));
            }
            var expectedAmount = DecimalValue(expectedRows[0], amountField);
            var actualAmount = DecimalValue(actualRows[0], amountField);
            if (expectedAmount is null || actualAmount is null || decimal.Abs(expectedAmount.Value - actualAmount.Value) > tolerance)
            {
                discrepancyCount++;
                yield return Fail(context, EvidenceType.Aggregate, "IncorrectContributionAmount", SafeKey(key),
                    "Contribution amount differs from the source-derived amount beyond the configured currency tolerance.",
                    RecordInputs(context, expectedRows[0], actualRows[0]),
                    FinancialValue(SafeKey(key), 1, expectedAmount ?? 0m, tolerance, (expectedAmount ?? 0m) - (actualAmount ?? 0m)),
                    FinancialValue(SafeKey(key), 1, actualAmount ?? 0m, tolerance, (expectedAmount ?? 0m) - (actualAmount ?? 0m)));
                continue;
            }
            if (!SameFields(expectedRows[0], actualRows[0], comparisonFields))
            {
                discrepancyCount++;
                yield return Fail(context, EvidenceType.Comparison, "ContributionSemanticMismatch", SafeKey(key),
                    "Contribution member, period, or category differs from the graph-derived target semantics.",
                    RecordInputs(context, expectedRows[0], actualRows[0]));
            }
        }

        if (discrepancyCount == 0)
            yield return Pass(context, EvidenceType.Accounting, "ContributionAccounting",
                "Every expected contribution has one target transaction with an equivalent amount and business key.");
    }
}

public class ContributionTotalRule(VerificationRuleDefinition definition) : PensionRuleBase(definition)
{
    public override VerificationScope Scope => VerificationScope.Aggregate;

    public override async IAsyncEnumerable<VerificationFinding> EvaluateAsync(VerificationExecutionContext context,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var finding in CompareTotalsAsync(context, "Pension.Contribution", Option("amountField", "amount"),
            Fields(Option("groupBy"), ["member_id", "period", "category"]), "ContributionPeriodTotalMismatch", Tolerance(),
            compareCount: true, cancellationToken)
            .ConfigureAwait(false)) yield return finding;
    }

    internal async IAsyncEnumerable<VerificationFinding> CompareTotalsAsync(VerificationExecutionContext context,
        string semantic, string amountField, string[] groupFields, string failureCode, decimal tolerance,
        bool compareCount,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var expected = ReadRecordGroupsAsync(context, VerificationArtifactRole.ExpectedTarget, TargetNode, semantic,
            groupFields, cancellationToken);
        var actual = ReadRecordGroupsAsync(context, VerificationArtifactRole.ActualTarget, TargetNode, semantic,
            groupFields, cancellationToken);
        var mismatches = 0;
        await foreach (var pair in MergeGroupsAsync(expected, actual, cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = Totals(pair.Expected, amountField);
            var target = Totals(pair.Actual, amountField);
            if ((!compareCount || source.Count == target.Count) && decimal.Abs(source.Total - target.Total) <= tolerance) continue;
            mismatches++;
            var fingerprint = SafeKey(pair.Expected?.Key ?? pair.Actual!.Key);
            yield return Fail(context, EvidenceType.Aggregate, failureCode, fingerprint,
                compareCount
                    ? "Source-derived and target financial groupings differ in transaction count or decimal total."
                    : "Source-derived and target service-credit totals differ beyond the configured decimal tolerance.",
                RecordInputs(context, source.First, target.First),
                FinancialValue(fingerprint, source.Count, source.Total, tolerance, source.Total - target.Total),
                FinancialValue(fingerprint, target.Count, target.Total, tolerance, source.Total - target.Total));
        }
        if (mismatches == 0)
            yield return Pass(context, EvidenceType.Aggregate, failureCode.Replace("Mismatch", "Reconciled", StringComparison.Ordinal),
                "All configured pension financial groups reconcile within the explicit currency tolerance.");
    }

    private static (long Count, decimal Total, VerificationArtifactRecord? First) Totals(
        PensionRecordGroup? group, string amountField)
    {
        long count = 0;
        decimal total = 0;
        VerificationArtifactRecord? first = null;
        if (group is not null)
        {
            foreach (var record in group.Records)
            {
                count++;
                total += DecimalValue(record, amountField) ?? 0m;
                first ??= record;
            }
        }
        return (count, total, first);
    }
}

public sealed class ServiceCreditTotalRule(VerificationRuleDefinition definition) : ContributionTotalRule(definition)
{
    public override async IAsyncEnumerable<VerificationFinding> EvaluateAsync(VerificationExecutionContext context,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var finding in CompareTotalsAsync(context, "Pension.ServiceCredit", Option("amountField", "credit"),
            Fields(Option("groupBy"), ["member_id"]), "IncorrectServiceCreditTotal", Tolerance(), compareCount: false, cancellationToken)
            .ConfigureAwait(false)) yield return finding;
    }
}

public sealed class BeneficiaryRelationshipRule(VerificationRuleDefinition definition) : PensionRuleBase(definition)
{
    public override VerificationScope Scope => VerificationScope.Relationship;

    public override async IAsyncEnumerable<VerificationFinding> EvaluateAsync(VerificationExecutionContext context,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var semantic = Option("semanticType", "Pension.Beneficiary");
        var keyField = Option("beneficiaryField", "beneficiary_id");
        var memberField = Option("memberField", "member_id");
        var relationField = Option("relationshipField", "relationship");
        var allocationField = Option("allocationField", "allocation");
        var tolerance = Tolerance("allocationTolerance");
        var expected = ReadRecordGroupsAsync(context, VerificationArtifactRole.ExpectedTarget, TargetNode, semantic, [keyField], cancellationToken);
        var actual = ReadRecordGroupsAsync(context, VerificationArtifactRole.ActualTarget, TargetNode, semantic, [keyField], cancellationToken);
        var memberNode = Option("memberNode");
        var memberSemantic = Option("memberSemanticType", "Pension.Member");
        var memberBusinessKey = Option("memberBusinessKey", "member_id");
        var errors = 0;
        await foreach (var pair in MergeGroupsAsync(expected, actual, cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (pair.Expected is null) continue;
            var key = pair.Expected.Key;
            var expectedRows = pair.Expected.Records;
            var actualRows = pair.Actual?.Records ?? [];
            if (actualRows.Count != 1)
            {
                errors++;
                yield return Fail(context, EvidenceType.Relationship, actualRows is { Count: > 1 } ? "DuplicateBeneficiaryRelationship" : "BrokenBeneficiaryRelationship",
                    SafeKey(key), "Beneficiary relationship is missing or duplicated in the target.", RecordInputs(context, expectedRows[0]),
                    new EvidenceValue(new IntegerValue(1)), new EvidenceValue(new IntegerValue(actualRows?.Count ?? 0)));
                continue;
            }
            var expectedRelationship = expectedRows[0];
            var actualRelationship = actualRows[0];
            var expectedMember = FieldText(expectedRelationship, memberField);
            var actualMember = FieldText(actualRelationship, memberField);
            var wrongMember = !string.Equals(expectedMember, actualMember, StringComparison.Ordinal);
            var missingMember = memberNode.Length > 0 && !await context.Workspace.ContainsFieldValueAsync(
                VerificationArtifactRole.ActualTarget, memberNode, memberSemantic, memberBusinessKey,
                new StringValue(actualMember), cancellationToken).ConfigureAwait(false);
            var wrongType = relationField.Length > 0 && !string.Equals(FieldText(expectedRelationship, relationField), FieldText(actualRelationship, relationField), StringComparison.Ordinal);
            var expectedAllocation = DecimalValue(expectedRelationship, allocationField);
            var actualAllocation = DecimalValue(actualRelationship, allocationField);
            var wrongAllocation = expectedAllocation is not null &&
                (actualAllocation is null || decimal.Abs(expectedAllocation.Value - actualAllocation.Value) > tolerance);
            if (!wrongMember && !missingMember && !wrongType && !wrongAllocation) continue;
            errors++;
            var code = missingMember ? "BrokenBeneficiaryRelationship" : wrongMember ? "WrongMemberBeneficiary" : "BeneficiarySemanticsMismatch";
            yield return Fail(context, EvidenceType.Relationship, code, SafeKey(key),
                "Beneficiary identity, member association, relationship type, or allocation differs from the source-derived relationship.",
                RecordInputs(context, expectedRelationship, actualRelationship),
                new EvidenceValue(new StringValue($"member:{SafeKey(expectedMember)};relationship:{SafeKey(FieldText(expectedRelationship, relationField))}")),
                new EvidenceValue(new StringValue($"member:{SafeKey(actualMember)};relationship:{SafeKey(FieldText(actualRelationship, relationField))}")));
        }
        if (errors == 0)
            yield return Pass(context, EvidenceType.Relationship, "BeneficiaryRelationships", "All beneficiary relationships resolve to the expected member and semantics.");
    }
}

public sealed class RetirementElectionRule(VerificationRuleDefinition definition) : PensionRuleBase(definition)
{
    public override VerificationScope Scope => VerificationScope.Attribute;

    public override async IAsyncEnumerable<VerificationFinding> EvaluateAsync(VerificationExecutionContext context,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var semantic = Option("semanticType", "Pension.RetirementElection");
        var keyField = Option("electionField", "election_id");
        var compareFields = Fields(Option("compareFields"), ["member_id", "election_code", "effective_date"]);
        var expected = ReadRecordGroupsAsync(context, VerificationArtifactRole.ExpectedTarget, TargetNode, semantic, [keyField], cancellationToken);
        var actual = ReadRecordGroupsAsync(context, VerificationArtifactRole.ActualTarget, TargetNode, semantic, [keyField], cancellationToken);
        var errors = 0;
        await foreach (var pair in MergeGroupsAsync(expected, actual, cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (pair.Expected is null) continue;
            var key = pair.Expected.Key;
            var expectedRows = pair.Expected.Records;
            var actualRows = pair.Actual?.Records ?? [];
            if (actualRows.Count != 1 || !SameFields(expectedRows[0], actualRows[0], compareFields))
            {
                errors++;
                yield return Fail(context, EvidenceType.Comparison, "RetirementElectionMappingMismatch", SafeKey(key),
                    "Retirement-election identity, effective date, or declared target code differs from the graph-derived value.",
                    RecordInputs(context, expectedRows[0], actualRows.Count == 0 ? null : actualRows[0]));
            }
        }
        if (errors == 0)
            yield return Pass(context, EvidenceType.Comparison, "RetirementElectionMapping", "Retirement elections match declared semantic code mappings and effective dates.");
    }
}

public sealed class BenefitPaymentRule(VerificationRuleDefinition definition) : PensionRuleBase(definition)
{
    public override VerificationScope Scope => VerificationScope.Accounting;

    public override async IAsyncEnumerable<VerificationFinding> EvaluateAsync(VerificationExecutionContext context,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var semantic = Option("semanticType", "Pension.BenefitPayment");
        var keyField = Option("paymentField", "payment_id");
        var amountField = Option("amountField", "amount");
        var tolerance = Tolerance();
        var expected = ReadRecordGroupsAsync(context, VerificationArtifactRole.ExpectedTarget, TargetNode, semantic, [keyField], cancellationToken);
        var actual = ReadRecordGroupsAsync(context, VerificationArtifactRole.ActualTarget, TargetNode, semantic, [keyField], cancellationToken);
        var errors = 0;
        await foreach (var pair in MergeGroupsAsync(expected, actual, cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (pair.Expected is null) continue;
            var key = pair.Expected.Key;
            var expectedRows = pair.Expected.Records;
            var actualRows = pair.Actual?.Records ?? [];
            if (actualRows.Count == 0)
            {
                errors++;
                yield return Fail(context, EvidenceType.Accounting, "MissingBenefitPayment", SafeKey(key),
                    "A source-derived benefit payment is absent from the target.", RecordInputs(context, expectedRows[0]),
                    new EvidenceValue(new IntegerValue(1)), new EvidenceValue(new IntegerValue(0)));
                continue;
            }
            if (actualRows.Count > 1)
            {
                errors += actualRows.Count - 1;
                yield return Fail(context, EvidenceType.Accounting, "DuplicateBenefitPayment", SafeKey(key),
                    "A benefit-payment identity occurs multiple times in the target.", RecordInputs(context, expectedRows[0], actualRows[0]),
                    new EvidenceValue(new IntegerValue(1)), new EvidenceValue(new IntegerValue(actualRows.Count)));
            }
            var expectedAmount = DecimalValue(expectedRows[0], amountField);
            var actualAmount = DecimalValue(actualRows[0], amountField);
            if (expectedAmount is null || actualAmount is null || decimal.Abs(expectedAmount.Value - actualAmount.Value) > tolerance)
            {
                errors++;
                yield return Fail(context, EvidenceType.Aggregate, "BenefitPaymentAmountMismatch", SafeKey(key),
                    "Benefit-payment amount differs beyond the configured decimal tolerance.", RecordInputs(context, expectedRows[0], actualRows[0]),
                        FinancialValue(SafeKey(key), 1, expectedAmount ?? 0m, tolerance, (expectedAmount ?? 0m) - (actualAmount ?? 0m)),
                        FinancialValue(SafeKey(key), 1, actualAmount ?? 0m, tolerance, (expectedAmount ?? 0m) - (actualAmount ?? 0m)));
            }
                    var compareFields = Fields(Option("compareFields"), [keyField, "participant_id", "payment_period", "paid_on"]);
                    if (!SameFields(expectedRows[0], actualRows[0], compareFields))
                    {
                    errors++;
                    yield return Fail(context, EvidenceType.Comparison, "BenefitPaymentSemanticMismatch", SafeKey(key),
                        "Benefit-payment member, period, or date differs from the source-derived target semantics.",
                        RecordInputs(context, expectedRows[0], actualRows[0]));
                    }
        }
        if (errors == 0)
            yield return Pass(context, EvidenceType.Accounting, "BenefitPaymentAccounting", "All expected benefit payments are present exactly once with matching amounts.");
    }
}

public sealed class BenefitPaymentTotalRule(VerificationRuleDefinition definition) : ContributionTotalRule(definition)
{
    public override async IAsyncEnumerable<VerificationFinding> EvaluateAsync(VerificationExecutionContext context,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var finding in CompareTotalsAsync(context, "Pension.BenefitPayment", Option("amountField", "amount"),
            Fields(Option("groupBy"), ["member_id", "period"]), "BenefitPaymentTotalMismatch", Tolerance(),
            compareCount: true, cancellationToken)
            .ConfigureAwait(false)) yield return finding;
    }
}

public sealed class DocumentAccountingRule(VerificationRuleDefinition definition) : PensionRuleBase(definition)
{
    public override VerificationScope Scope => VerificationScope.Accounting;

    public override async IAsyncEnumerable<VerificationFinding> EvaluateAsync(VerificationExecutionContext context,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var semantic = Option("semanticType", "Pension.Document");
        var keyField = Option("documentField", "document_id");
        var hashField = Option("contentHashField", "content_hash");
        var missingCode = Option("missingCode", "MissingDocument");
        var expected = ReadRecordGroupsAsync(context, VerificationArtifactRole.ExpectedTarget, TargetNode, semantic, [keyField], cancellationToken);
        var actual = ReadRecordGroupsAsync(context, VerificationArtifactRole.ActualTarget, TargetNode, semantic, [keyField], cancellationToken);
        var errors = 0;
        await foreach (var pair in MergeGroupsAsync(expected, actual, cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (pair.Expected is null) continue;
            var key = pair.Expected.Key;
            var expectedRows = pair.Expected.Records;
            var actualRows = pair.Actual?.Records ?? [];
            if (actualRows.Count == 0)
            {
                errors++;
                yield return Fail(context, EvidenceType.Accounting, missingCode, SafeKey(key),
                    "A migration-relevant pension document is absent from its target or archive.", RecordInputs(context, expectedRows[0]),
                    new EvidenceValue(new StringValue("accounted")), new EvidenceValue(new StringValue("missing")));
                continue;
            }
            var expectedHash = FieldText(expectedRows[0], hashField);
            var actualHash = FieldText(actualRows[0], hashField);
            if (expectedHash.Length > 0 && string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase)) continue;
            errors++;
            yield return Fail(context, EvidenceType.Comparison, "DocumentContentMismatch", SafeKey(key),
                "Document content fingerprint differs from the graph-derived source content.", RecordInputs(context, expectedRows[0], actualRows[0]),
                new EvidenceValue(new StringValue($"sha256:{SafeKey(expectedHash)}")),
                new EvidenceValue(new StringValue($"sha256:{SafeKey(actualHash)}")));
        }
        if (errors == 0)
            yield return Pass(context, EvidenceType.Accounting, "DocumentAccounting", "All expected document identities are present with matching content fingerprints.");
    }
}

public sealed class DocumentRelationshipRule(VerificationRuleDefinition definition) : PensionRuleBase(definition)
{
    public override VerificationScope Scope => VerificationScope.Relationship;

    public override async IAsyncEnumerable<VerificationFinding> EvaluateAsync(VerificationExecutionContext context,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var semantic = Option("semanticType", "Pension.Document");
        var keyField = Option("documentField", "document_id");
        var memberField = Option("memberField", "member_id");
        var expected = ReadRecordGroupsAsync(context, VerificationArtifactRole.ExpectedTarget, TargetNode, semantic, [keyField], cancellationToken);
        var actual = ReadRecordGroupsAsync(context, VerificationArtifactRole.ActualTarget, TargetNode, semantic, [keyField], cancellationToken);
        var errors = 0;
        await foreach (var pair in MergeGroupsAsync(expected, actual, cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (pair.Expected is null || pair.Actual is null) continue;
            var key = pair.Expected.Key;
            var expectedRows = pair.Expected.Records;
            var actualRows = pair.Actual.Records;
            if (actualRows.Count == 0) continue;
            if (actualRows.Count != 1 ||
                !string.Equals(FieldText(expectedRows[0], memberField), FieldText(actualRows[0], memberField), StringComparison.Ordinal))
            {
                errors++;
                yield return Fail(context, EvidenceType.Relationship, "WrongMemberDocument", SafeKey(key),
                    "Document is missing, duplicated, or associated with a different member than its source lineage.",
                    RecordInputs(context, expectedRows[0], actualRows[0]),
                    new EvidenceValue(new StringValue($"sha256:{SafeKey(FieldText(expectedRows[0], memberField))}")),
                    actualRows is { Count: > 0 } ? new EvidenceValue(new StringValue($"sha256:{SafeKey(FieldText(actualRows[0], memberField))}")) : null);
            }
        }
        if (errors == 0)
            yield return Pass(context, EvidenceType.Relationship, "DocumentRelationships", "All documents retain their expected member relationship.");
    }
}

public sealed class PensionCodeTransformationRule(VerificationRuleDefinition definition) : PensionRuleBase(definition)
{
    public override VerificationScope Scope => VerificationScope.Attribute;

    public override async IAsyncEnumerable<VerificationFinding> EvaluateAsync(VerificationExecutionContext context,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var semantic = Option("semanticType");
        var field = Option("attribute", "code");
        var keyField = Option("businessKey", "id");
        var expected = ReadRecordGroupsAsync(context, VerificationArtifactRole.ExpectedTarget, TargetNode, semantic, [keyField], cancellationToken);
        var actual = ReadRecordGroupsAsync(context, VerificationArtifactRole.ActualTarget, TargetNode, semantic, [keyField], cancellationToken);
        var errors = 0;
        await foreach (var pair in MergeGroupsAsync(expected, actual, cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (pair.Expected is null) continue;
            var key = pair.Expected.Key;
            var expectedRows = pair.Expected.Records;
            var actualRows = pair.Actual?.Records ?? [];
            if (actualRows.Count != 1 ||
                !string.Equals(FieldText(expectedRows[0], field), FieldText(actualRows[0], field), StringComparison.Ordinal))
            {
                errors++;
                yield return Fail(context, EvidenceType.Transformation, "CodeTransformationMismatch", SafeKey(key),
                    "Target pension code differs from the declared migration-graph code mapping.",
                    RecordInputs(context, expectedRows[0], actualRows.Count == 0 ? null : actualRows[0]),
                    new EvidenceValue(new StringValue($"sha256:{SafeKey(FieldText(expectedRows[0], field))}")),
                    actualRows is { Count: > 0 } ? new EvidenceValue(new StringValue($"sha256:{SafeKey(FieldText(actualRows[0], field))}")) : null);
            }
        }
        if (errors == 0)
            yield return Pass(context, EvidenceType.Transformation, "CodeTransformations", "Configured pension code mappings match their graph-derived target values.");
    }
}