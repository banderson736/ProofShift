using System.Security.Cryptography;
using System.Text;

namespace ProofShift.Graph;

internal static class StableGraphIdentifier
{
    public static Guid Derive(string projectKey, string graphPath, string entityKind, string externalKey)
    {
        var builder = new StringBuilder("proofshift-graph-id-v1\n");
        Append(builder, projectKey);
        Append(builder, graphPath);
        Append(builder, entityKind);
        Append(builder, externalKey);

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        digest[6] = (byte)((digest[6] & 0x0f) | 0x80);
        digest[8] = (byte)((digest[8] & 0x3f) | 0x80);
        return Guid.ParseExact(Convert.ToHexString(digest.AsSpan(0, 16)), "N");
    }

    private static void Append(StringBuilder builder, string value) =>
        builder.Append(Encoding.UTF8.GetByteCount(value).ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Append(':')
            .Append(value);
}
