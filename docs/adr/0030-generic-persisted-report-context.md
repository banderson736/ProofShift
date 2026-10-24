# ADR-0030: Generic persisted report context

- Status: Proposed
- Date: 2026-10-24

## Context

Ordinary persisted `report` and `compare` commands need a pack-owned display name, exact pack version, and claim boundary without rereading or rerunning Verification. Existing persisted Recovery artifacts bind each dry run to its Verification run, while persisted Evidence context binds the run to configuration, graph, checkpoint/source, projection or observation, rule-set, and Verification-ledger identities. It did not preserve the exact provider versions used by that Verification run, and pack claim boundaries were duplicated in report implementations.

## Decision

- Keep persisted reporting in the domain-neutral Reporting assembly. The CLI resolves configured pack IDs and versions through `PackRegistry`; report code consumes pack metadata and never branches on a concrete pack type or ID.
- Make claim boundaries required pack metadata. Each installed pack owns its claim boundary, and reports copy the selected pack's exact metadata.
- Append optional provider-version metadata to `EvidenceVerificationContext` and populate it from the already-resolved Verification rule set. This records the exact providers used for both projection-backed and independently observed target Verification. It is context metadata and is deliberately excluded from Evidence graph canonicalization/fingerprints.
- A persisted report requires one exact domain-pack provider in the persisted context. Missing, unsupported, mismatched, or multi-pack context fails closed. Pack versions are never substituted.
- Expose already-persisted Recovery assessment outcome, Projection run/manifest identity, checkpoint identity, and Verification-ledger receipt through the read-only `RecoveryArtifactSummary` accessor. Do not create a second Recovery persistence model.
- Keep the checkpoint manifest hash authoritative for integrity and run-context binding: Evidence and Recovery must agree with the exact persisted checkpoint manifest. Do not use that hash itself as the operator-facing source-change signal because canonical manifest content includes capture and materialization identity.
- Read checkpoint manifest metadata without replaying source records. The accessor validates completion state, manifest integrity and identity, aggregate source-fingerprint binding, and contained/present segment references. Full segment and record integrity remains the responsibility of checkpoint replay/open validation.
- Compare normalized checkpoint semantics—including the aggregate and endpoint source fingerprints, source-node and endpoint identity, selector/identity fields, connector/provider and version, consistency strategies, cross-system atomicity, replayability, and artifact coverage—while excluding checkpoint IDs, capture timestamps, byte-storage details, and segment references/names. Source-fingerprint changes remain independently exposed and attributed.
- Compare projection and external observation target fingerprints separately; run/observation IDs are provenance and do not alone indicate changed target state.
- Single-run reporting additionally requires current configuration and graph hashes to match. Comparison validates both runs' stored integrity and project/pack context while allowing historical configuration and graph hashes to differ so those changes remain reportable.
- Rendered report and comparison objects are deterministic presentation output. They do not alter qualification, Evidence, Recovery, or semantic fingerprints and omit expected/actual payloads.

## Alternatives

- Infer pack identity from finding IDs or rule names: rejected because rule names are not a durable provider identity and can collide.
- Store a second report manifest: rejected because persisted Evidence and Recovery already provide the authority and the missing provider metadata is small context only.
- Include provider versions in Evidence graph canonicalization: rejected because metadata is not a material verification result and must not change accepted Evidence fingerprints.
- Keep a separate report implementation in each domain pack: rejected because it duplicates persisted-artifact validation and makes ordinary commands domain-dependent.

## Consequences

- Persisted Evidence context gains optional provider-version metadata; older records without it fail closed for the new generic persisted-report path.
- The five installed packs own their claim-boundary text, and reports derive that text through pack metadata.
- Exact project, pack, and artifact context can be validated without rerunning Verification. Historical configuration/graph differences can be compared without treating run IDs as semantic differences.
- Checkpoint identity remains visible as provenance in report output, while comparison uses its normalized semantic fingerprint so capture IDs/timestamps alone do not produce a difference.
- Manifest integrity/binding and operator-facing semantic comparison are separate contracts: exact hashes establish persisted context, while normalized source/target state fields explain meaningful changes.
- Existing accepted scenario semantics and semantic fingerprints remain unchanged.

## Validation

The full solution builds with zero warnings and errors. Focused local tests for persisted reporting/comparison, snapshots, domain-pack conformance, Evidence, Recovery, Configuration, Domain, Engine, Graph, Verification, and RemoteObjects passed (178 passed, 0 failed, 0 skipped). RemoteStorage, Oracle, Db2, and physical EndToEnd integration tests require Docker-backed fixtures and remain for candidate CI validation; Docker is unavailable in the local environment. PS-0.11A remains In Progress pending required candidate CI evidence and review.
