using System.Runtime.CompilerServices;
using ProofShift.Domain;
using ProofShift.Verification;

namespace ProofShift.Packs.Abstractions;

public sealed record OrderedReferenceMatch(VerificationArtifactRecord Source, string? ReferenceKey, bool Resolves);

public static class OrderedReferenceMatcher
{
    public static async IAsyncEnumerable<OrderedReferenceMatch> ScanAsync(VerificationExecutionContext context,
        string sourceNode, string sourceSemanticType, string sourceReferenceField,
        string referenceNode, string referenceSemanticType, string referenceKeyField,
        Func<VerificationArtifactRecord, string?> referenceKeySelector,
        Func<VerificationArtifactRecord, string?> targetKeySelector,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(referenceKeySelector);
        ArgumentNullException.ThrowIfNull(targetKeySelector);
        var sourceOrder = new VerificationOrderingKey(sourceSemanticType, sourceReferenceField, VerificationOrderingRole.Grouping);
        var referenceOrder = new VerificationOrderingKey(referenceSemanticType, referenceKeyField, VerificationOrderingRole.Lookup);
        await using var sources = context.Workspace.ReadArtifactRecordsByKeysAsync(VerificationArtifactRole.ActualTarget,
            sourceNode, sourceSemanticType, [sourceOrder], cancellationToken).GetAsyncEnumerator(cancellationToken);
        await using var references = context.Workspace.ReadArtifactRecordsByKeysAsync(VerificationArtifactRole.ActualTarget,
            referenceNode, referenceSemanticType, [referenceOrder], cancellationToken).GetAsyncEnumerator(cancellationToken);
        var hasReference = await references.MoveNextAsync().ConfigureAwait(false);
        while (await sources.MoveNextAsync().ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = sources.Current;
            source.RequireDeclaredField(sourceReferenceField);
            var key = referenceKeySelector(source);
            var resolves = !string.IsNullOrEmpty(key);
            while (resolves && hasReference)
            {
                var target = references.Current;
                target.RequireDeclaredField(referenceKeyField);
                var targetKey = targetKeySelector(target);
                var comparison = StringComparer.Ordinal.Compare(targetKey, key);
                if (comparison >= 0) break;
                hasReference = await references.MoveNextAsync().ConfigureAwait(false);
            }

            if (resolves && hasReference)
                resolves = StringComparer.Ordinal.Equals(targetKeySelector(references.Current), key);
            else
                resolves = false;

            yield return new OrderedReferenceMatch(source, key, resolves);
        }
    }
}
