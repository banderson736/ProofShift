# ADR-0008: Verification Runtime and Evidence Binding

Status: Accepted

## Context

PS-0.7 must verify a materialized checkpoint-backed projection using actual shadow read-back, then persist deterministic evidence. The current domain `VerificationContext` is tied to `MigrationPlan` and `MigrationRun`, but the PS-0 workflow has no plan repository and configured project IDs are strings; D-015 explicitly rejects inventing a mapping to the domain GUID `ProjectId`. The current Projection result contains the checkpoint and read-back fingerprints only in memory, while its JSONL journal is the durable execution ancestry. The pure graph transformation implementation is currently nested in the Projection assembly, so a separate verification engine cannot derive expected values without depending on the Projection orchestrator or duplicating transformation semantics.

## Decision

- Keep immutable evidence records, evidence references, source dispositions, and target lineage in Domain/Evidence. Put execution-time `IVerificationRule`, rule registry, verification bindings, aggregation, and orchestration in `ProofShift.Verification`; rule execution is not a Domain responsibility.
- Verification binds to configuration hash, graph hash, checkpoint ID/manifest/source fingerprints, projection run ID/fingerprint, rule-set fingerprint, and runtime/connector/pack versions. The CLI invocation is explicit: `proofshift verify <config> --checkpoint <id-or-path> --projection <run-id>`.
- Persist a versioned projection run manifest next to the projection journal. Verification checks this binding and journal identity, then independently reads the checkpoint and physical shadow targets. It does not treat journal intent as proof of target existence and never reads live sources.
- Move the deterministic, side-effect-free graph transformation and projection-fingerprint primitives into `ProofShift.Engine`, which already sits below Projection and depends only on generic graph/domain/connector abstractions. Projection calls the same primitives it did before; Verification reuses them to derive expected target semantics. Projection orchestration remains unchanged.
- Keep concrete PostgreSQL/filesystem target connectors at the CLI composition root. Verification consumes `IShadowTargetConnector` abstractions and does not reference concrete connector or Pension projects.
- `ProofShift.Packs.Abstractions` defines versioned rule-provider/factory contracts over Verification abstractions. The Pension Pack supplies its initial member rule provider; generic Verification Core remains domain-neutral.
- Persist evidence through `IEvidenceStore`. PS-0's CLI composes the filesystem implementation; Verification Core depends only on the storage abstraction. Evidence files use a versioned canonical format and integrity fingerprints. Human/JSON output uses redacted display values; comparisons use typed values internally.
- Journal `pending` is interpreted as planned ancestry; only terminal `produced` entries from a successful projection establish materialized ancestry. `failed`, `excluded`, and metadata-only entries cannot establish a target observation.
- Initial overall status policy: any execution error or Critical/Error failure is `Failed`; warnings with no failures are `PassedWithWarnings`; otherwise `Passed`. `Unaccounted` sources and unexplained/missing/duplicate targets are failures.

## Alternatives Considered

- Construct synthetic `MigrationPlan`/`MigrationRun` values for the current CLI: rejected because it invents the prohibited project-ID conversion and misstates persisted runtime history.
- Put verification execution rules in Domain: rejected because the context is a run-time use case requiring checkpoint, journal, target-reader, and pack abstractions, not a domain value object.
- Have Verification reference Projection: rejected because it couples the assurance engine to the execution assembly.
- Reimplement graph transformations in Verification: rejected because duplicated semantics could diverge from Projection. Shared pure primitives in Engine provide one versioned implementation.
- Add a run database: rejected for PS-0; a versioned filesystem projection manifest and evidence store are sufficient.

## Consequences

- Domain's preliminary plan-bound rule hook is superseded by the Verification-owned execution contract; immutable Domain Evidence and `ArtifactDispositionLedger`/`LineageLedger` remain the shared models.
- Projection adds a small versioned run-manifest artifact; no target-write, journal ancestry, or checkpoint semantics change.
- Verification's temporary identity/value index is isolated and deleted on disposal. Persisted evidence contains safe display values and typed comparison fingerprints, not raw record dumps or resolved secrets.
- This decision implements only the first generic/Pension Member verification slice. Timeline, full financial/relationship suites, recovery readiness, signatures, and production execution remain deferred.
