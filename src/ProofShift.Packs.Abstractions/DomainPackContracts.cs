using ProofShift.Verification;

namespace ProofShift.Packs.Abstractions;

public interface IDomainPack : IVerificationRuleProvider
{
	DomainPackMetadata Metadata { get; }
}
