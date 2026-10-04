# ADR-0009: Graph-Scoped Verification Artifact References

Status: Accepted

## Context

PS-0.7 must account for and explain every target artifact produced by a migration graph. The accepted PS-0.5 pension fixture has a `member-split` edge writing `participant` and `member-status` into the same PostgreSQL endpoint. Both selectors use table artifacts with the same `id` identity. The current `ArtifactId` formula is based on system, endpoint, artifact type, and identity, so a member row in those two distinct graph nodes can share one `ArtifactId`. `LineageLedger` and `ArtifactDispositionLedger` currently group only by `ArtifactId`, which would merge separate graph-node artifacts and make exact coverage/lineage ambiguous.

Changing projection artifact identity would alter accepted PS-0.5 journal/read-back behavior and is not needed to make verification correct.

## Decision

- Preserve `ArtifactReference` identity and the PS-0.5 projection fingerprint contract.
- Add a generic graph-scoped artifact key composed of graph node key plus `ArtifactId` for verification accounting and lineage.
- Allow `EvidenceReference` to attach a graph node key to an artifact reference; the artifact ID remains strongly typed, and the pair identifies a target/source occurrence in the graph.
- Extend disposition and lineage records with optional graph-node scope for compatibility. Add scoped coverage validation that keys by `(graph node, ArtifactId)`; verification must use the scoped API.
- Projection Journal's existing `TargetNode` is the authoritative target scope. Checkpoint source nodes provide the source scope.

## Alternatives Considered

- Change target `ArtifactId` generation to include graph node: rejected because it changes accepted Projection identities/fingerprints and mixes graph-node scope into connector artifact identity.
- Treat same-ID artifacts in different target nodes as one target and merge their lineage: rejected because a query for one physical target would return ambiguous ancestry and cannot explain one target node independently.
- Store graph-node scope only as an untyped display string in CLI output: rejected because coverage/evidence linkage must remain machine-identifiable.

## Consequences

- Generic artifact identity remains independent of a particular execution path.
- PS-0.7 can distinguish same-identity artifacts materialized in separate graph nodes without changing checkpoint, graph, connector, or Projection semantics.
- Existing unscoped Domain APIs remain source-compatible; new verification code uses graph-scoped references and validation.
