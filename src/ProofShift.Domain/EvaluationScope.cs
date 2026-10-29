using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ProofShift.Domain;

public enum EvaluationScopeType
{
    FullDeclaredSource,
    BoundedEvaluation,
    CustomerPreparedSubset
}

public enum EvaluationPredicateOperator
{
    Equals,
    NotEquals,
    In,
    Range,
    DateTimeRange,
    IsNull,
    IsNotNull
}

public sealed record EvaluationPredicate
{
    public string Field { get; }
    public EvaluationPredicateOperator Operator { get; }
    public DomainList<string> Values { get; }

    public EvaluationPredicate(string field, EvaluationPredicateOperator @operator, IEnumerable<string>? values = null)
    {
        Field = DomainGuard.Required(field, nameof(field));
        Operator = @operator;
        if (!Enum.IsDefined(@operator)) throw new ArgumentOutOfRangeException(nameof(@operator));
        var normalizedValues = (values ?? []).Select(value => DomainGuard.Required(value, nameof(values))).ToArray();
        var requiredValueCount = @operator switch
        {
            EvaluationPredicateOperator.Equals or EvaluationPredicateOperator.NotEquals => 1,
            EvaluationPredicateOperator.Range or EvaluationPredicateOperator.DateTimeRange => 2,
            EvaluationPredicateOperator.IsNull or EvaluationPredicateOperator.IsNotNull => 0,
            EvaluationPredicateOperator.In => -1,
            _ => throw new ArgumentOutOfRangeException(nameof(@operator))
        };
        if (requiredValueCount >= 0 && normalizedValues.Length != requiredValueCount ||
            requiredValueCount == -1 && normalizedValues.Length == 0)
            throw new ArgumentException("Evaluation predicate values do not match the operator.", nameof(values));
        if (@operator == EvaluationPredicateOperator.In &&
            normalizedValues.Distinct(StringComparer.Ordinal).Count() != normalizedValues.Length)
            throw new ArgumentException("Evaluation predicate IN values must be unique.", nameof(values));
        Values = new DomainList<string>(@operator == EvaluationPredicateOperator.In
            ? normalizedValues.Order(StringComparer.Ordinal)
            : normalizedValues);
    }

    public static string OperatorName(EvaluationPredicateOperator @operator) => @operator switch
    {
        EvaluationPredicateOperator.Equals => "equals",
        EvaluationPredicateOperator.NotEquals => "not-equals",
        EvaluationPredicateOperator.In => "in",
        EvaluationPredicateOperator.Range => "range",
        EvaluationPredicateOperator.DateTimeRange => "date-time-range",
        EvaluationPredicateOperator.IsNull => "is-null",
        EvaluationPredicateOperator.IsNotNull => "is-not-null",
        _ => throw new ArgumentOutOfRangeException(nameof(@operator))
    };
}

public sealed record EvaluationScope
{
    public string Id { get; }
    public EvaluationScopeType Type { get; }
    public string? Description { get; }
    public DomainList<EvaluationPredicate> Predicates { get; }
    public string Fingerprint { get; }

    public EvaluationScope(string id, EvaluationScopeType type, IEnumerable<EvaluationPredicate>? predicates = null,
        string? description = null)
    {
        Id = DomainGuard.Required(id, nameof(id));
        if (!Enum.IsDefined(type)) throw new ArgumentOutOfRangeException(nameof(type));
        Type = type;
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        Predicates = new DomainList<EvaluationPredicate>(predicates ?? []);
        if (type != EvaluationScopeType.BoundedEvaluation && Predicates.Count > 0)
            throw new ArgumentException("Only bounded evaluation scopes may declare relational predicates.", nameof(predicates));
        Fingerprint = CreateFingerprint();
    }

    private string CreateFingerprint()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("format", "proofshift-evaluation-scope-v1");
            writer.WriteString("id", Id);
            writer.WriteString("type", Type switch
            {
                EvaluationScopeType.FullDeclaredSource => "full-declared-source",
                EvaluationScopeType.BoundedEvaluation => "bounded-evaluation",
                EvaluationScopeType.CustomerPreparedSubset => "customer-prepared-subset",
                _ => throw new ArgumentOutOfRangeException(nameof(Type))
            });
            writer.WriteStartArray("predicates");
            foreach (var predicate in Predicates.OrderBy(CanonicalPredicateOrderKey, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("field", predicate.Field);
                writer.WriteString("operator", EvaluationPredicate.OperatorName(predicate.Operator));
                writer.WriteStartArray("values");
                foreach (var value in predicate.Values) writer.WriteStringValue(value);
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Convert.ToHexString(SHA256.HashData(buffer.ToArray())).ToLowerInvariant();
    }

    public static string CanonicalPredicateOrderKey(EvaluationPredicate predicate)
    {
        var values = string.Concat(predicate.Values.Select(value =>
            $"{value.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)}:{value}"));
        return $"{predicate.Field.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)}:{predicate.Field}" +
            $"{EvaluationPredicate.OperatorName(predicate.Operator).Length.ToString(System.Globalization.CultureInfo.InvariantCulture)}:{EvaluationPredicate.OperatorName(predicate.Operator)}{values}";
    }
}
