using System.Globalization;
using ProofShift.Domain;
using ProofShift.Verification;
using Xunit;

namespace ProofShift.Verification.Tests;

public sealed class VerificationOrderingKeyCodecTests
{
    [Fact]
    public void NormalizePreservesEachSupportedValueKindAndItsOrderingRepresentation()
    {
        var offset = new DateTimeOffset(2024, 2, 3, 4, 5, 6, TimeSpan.FromHours(2));
        var values = new (ValueNode Value, NormalizedOrderingValue Expected)[]
        {
            (new NullValue(), new(0)),
            (new StringValue("text"), new(1, Text: "text")),
            (new IntegerValue(long.MinValue), new(2, Integer: long.MinValue)),
            (new DecimalValue(1.2300m), new(3, Decimal: "1.23")),
            (new DateValue(new DateOnly(2024, 2, 3)), new(4, Temporal: "2024-02-03")),
            (new InstantValue(offset), new(5, Temporal: "2024-02-03T02:05:06.0000000Z")),
            (new OffsetDateTimeValue(offset), new(6, Temporal: "2024-02-03T02:05:06.0000000Z")),
            (new LocalDateTimeValue(new DateTime(2024, 2, 3, 4, 5, 6, DateTimeKind.Unspecified)),
                new(7, Temporal: "2024-02-03T04:05:06.0000000")),
            (new BooleanValue(true), new(8, Boolean: 1)),
            (new BinaryReferenceValue("object", 10, new string('a', 64)), new(9, Text: new string('a', 64))),
            (new CollectionValue([]), new(10, Text: string.Empty)),
            (new ObjectValue([]), new(11, Text: string.Empty))
        };

        foreach (var (value, expected) in values)
            Assert.Equal(expected, VerificationOrderingKeyCodec.Normalize(value));
    }

    [Fact]
    public void DecimalComparisonIsNumericForNegativeLargeAndEqualValues()
    {
        Assert.True(VerificationOrderingKeyCodec.CompareDecimalStrings("-2", "-10") > 0);
        Assert.True(VerificationOrderingKeyCodec.CompareDecimalStrings("10", "2") > 0);
        Assert.True(VerificationOrderingKeyCodec.CompareDecimalStrings(decimal.MaxValue.ToString(CultureInfo.InvariantCulture),
            (decimal.MaxValue - 1m).ToString(CultureInfo.InvariantCulture)) > 0);
        Assert.Equal(0, VerificationOrderingKeyCodec.CompareDecimalStrings("1.0", "1.00"));
        Assert.Equal(0, VerificationOrderingKeyCodec.CompareDecimalStrings(null, null));
        Assert.True(VerificationOrderingKeyCodec.CompareDecimalStrings(null, "0") < 0);
    }

    [Fact]
    public void HashesAreDeterministicAndCultureIndependent()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");

            Assert.Equal("7bbc5aec7ac083903356d07261c49f37e35e17b026118fde0b6b1d8766654417",
                VerificationOrderingKeyCodec.FingerprintValue(new DecimalValue(12.5m)));
            Assert.Equal("427d2962f773cd51d93b3eb8afc3f9c4e6359a2115d131c48a5515a9a9efb7aa",
                VerificationOrderingKeyCodec.IdentityHash("proofshift-ordering-identity"));
            Assert.Equal(new NormalizedOrderingValue(3, Decimal: "12.5"),
                VerificationOrderingKeyCodec.Normalize(new DecimalValue(12.5m)));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }
}
