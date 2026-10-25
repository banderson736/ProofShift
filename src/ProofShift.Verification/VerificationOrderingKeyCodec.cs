using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ProofShift.Domain;
using ProofShift.Engine;

namespace ProofShift.Verification;

internal static class VerificationOrderingKeyCodec
{
    internal static NormalizedOrderingValue Normalize(ValueNode value) => value switch
    {
        NullValue => new(0),
        StringValue item => new(1, Text: item.Value),
        IntegerValue item => new(2, Integer: item.Value),
        DecimalValue item => new(3, Decimal: item.Value.ToString("G29", CultureInfo.InvariantCulture)),
        DateValue item => new(4, Temporal: item.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
        InstantValue item => new(5, Temporal: item.Value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)),
        OffsetDateTimeValue item => new(6, Temporal: item.Value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)),
        LocalDateTimeValue item => new(7, Temporal: item.Value.ToString("O", CultureInfo.InvariantCulture)),
        BooleanValue item => new(8, Boolean: item.Value ? 1 : 0),
        BinaryReferenceValue item => new(9, Text: item.Sha256),
        CollectionValue => new(10, Text: string.Empty),
        ObjectValue => new(11, Text: string.Empty),
        _ => throw new ArgumentOutOfRangeException(nameof(value), "Unsupported normalized ordering-key value.")
    };

    internal static int CompareDecimalStrings(string? left, string? right)
    {
        if (left is null) return right is null ? 0 : -1;
        if (right is null) return 1;
        if (decimal.TryParse(left, NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out var leftValue) &&
            decimal.TryParse(right, NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out var rightValue))
            return leftValue.CompareTo(rightValue);
        return string.Compare(left, right, StringComparison.Ordinal);
    }

    internal static string IdentityHash(string identity) => Hash(Encoding.UTF8.GetBytes(identity));

    internal static string FingerprintValue(ValueNode value) =>
        Hash(Encoding.UTF8.GetBytes(GraphTargetIdentity.CanonicalValue(value)));

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

internal readonly record struct NormalizedOrderingValue(int Kind, string? Text = null, long? Integer = null,
    string? Decimal = null, string? Temporal = null, int? Boolean = null);
