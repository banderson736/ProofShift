using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using ProofShift.Domain;
using ProofShift.Engine;

namespace ProofShift.Verification;

public enum VerificationPartitionBasis { ArtifactIdentity, GroupingKey, TimelineOwner, RelationshipOwner }
public enum VerificationPartitionExecution { PartitionLocal, PartitionPartialWithGlobalMerge, Global }

public static class VerificationPartitioning
{
    public const string Version = "proofshift-verification-partition-sha256-v2";
    public const int StableBucketCount = 8;

    public static int AssignArtifactIdentity(string nodeKey, string identity, int partitionCount) =>
        AssignArtifactIdentity(VerificationPartitionBasis.ArtifactIdentity, nodeKey, identity, partitionCount);

    public static int AssignArtifactIdentity(VerificationPartitionBasis basis, string nodeKey, string identity, int partitionCount) =>
        AssignArtifactIdentityHash(basis, nodeKey,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant(), partitionCount);

    public static int AssignArtifactIdentityHash(string nodeKey, string identityHash, int partitionCount) =>
        AssignArtifactIdentityHash(VerificationPartitionBasis.ArtifactIdentity, nodeKey, identityHash, partitionCount);

    public static int AssignArtifactIdentityHash(VerificationPartitionBasis basis, string nodeKey, string identityHash,
        int partitionCount) => Assign(basis, nodeKey, [new StringValue(identityHash)], partitionCount);

    public static int Assign(VerificationPartitionBasis basis, string scope, IEnumerable<ValueNode> keyValues, int partitionCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(partitionCount, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentNullException.ThrowIfNull(keyValues);
        if (partitionCount == 1) return 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(Version);
        Append(basis.ToString());
        Append(scope);
        foreach (var value in keyValues) Append(GraphTargetIdentity.CanonicalValue(value));
        return (int)(BinaryPrimitives.ReadUInt64BigEndian(hash.GetHashAndReset()) % (ulong)partitionCount);

        void Append(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }
    }
}