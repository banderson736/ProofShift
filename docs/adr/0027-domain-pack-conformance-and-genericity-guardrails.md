# ADR-0027: Domain Pack Conformance and Genericity Guardrails

Status: Accepted for PS-0.10F (October 11, 2026; merged as `77bceb325d925eb41c44b0eb7e3d3e5fb74542b9`)

## Context

PS-0.10F begins a cross-domain architecture proof. `IDomainPack` previously exposed only a verification rule provider, so the runtime had no deterministic contract for pack identity, concepts, configuration-schema contributions, capabilities, or authoring metadata. The current code also placed Pension member accounting in generic Verification and Pension defect/report sections in generic Reporting. Both made a new pack depend on implementation conventions rather than an explicit pack boundary.

## Decision

- Keep `IDomainPack` as the current rule-provider abstraction and require immutable `DomainPackMetadata`. Metadata binds pack ID/version and display name to sorted concepts, rule-provider IDs/versions/rule descriptors, JSON configuration-schema contributions and their rule-type coverage, capability metadata, and authoring metadata. It contains no timestamps or resolved secrets.
- `PackRegistry` validates metadata identity/version and rule-provider/rule-descriptor agreement against the installed pack. Configuration selection remains explicit and exact-version; unavailable IDs, mismatched versions, and duplicate selection continue to fail closed. The existing contract has one provider per pack; the metadata uses a collection so the conformance surface is explicit, but multi-provider composition is deferred until a concrete pack requires it.
- A reusable test-only conformance harness accepts a pack factory, synthesizes descriptor-driven options and graph nodes, and verifies pack resolution, unique/versioned rules, complete descriptors, resolvable declared fields, generated schema contributions, secret-safe options, fail-closed unknown rules/versions, and `VerificationExecutionPlan` compilation.
- Architecture tests inspect the compiled generic assemblies for references to concrete pack assemblies and for installed-pack semantic identifiers/pack-specific field names. Documentation and test assemblies are outside that runtime-coupling scan. Concrete packs are checked for references to each other.
- Pension-specific member accounting now lives in `ProofShift.Packs.Pension`; the Pension assurance report implementation is compiled in that pack. Generic Verification and Reporting no longer contain those Pension runtime semantics.

## Alternatives

- Leave Pension rule/report implementations in generic projects and rely on naming conventions: rejected because a second pack would inherit Pension assumptions or require string-based branching.
- Infer all metadata from `IVerificationRuleProvider`: rejected because the provider does not describe semantic concepts, capabilities, authoring support, or schema contribution ownership.
- Introduce a general plugin loader and multiple nested rule-provider composition now: deferred. The current pack is already its provider; no independent provider composition requirement has been demonstrated.

## Consequences

- Every pack can be checked through the same conformance harness before it is used in a physical scenario.
- Metadata and descriptor drift fail at registry construction instead of silently changing the authoring surface.
- Domain terminology and report sections can stay in their owning pack while generic Verification, Reporting, and connector abstractions remain pack-neutral.
- The CLI still has legacy Pension-specific persisted-report command flow. F6 adds and tests one generic metadata-driven assurance report model across five physical scenarios; this does not claim pack-neutral persisted-report dispatch.
- All five packs are checked by the same conformance/architecture test surface. Generic runtime projects may depend on the shared `Packs.Abstractions` contract but not concrete packs, and compiled generic binaries remain free of installed domain terms.

## Validation

The shared five-pack conformance/architecture group passes 10/10. The aggregate registry test verifies exact five-pack selection, global rule-type uniqueness, metadata/version binding, fail-closed unknown ID/version, schema isolation, and cross-pack contamination. The real CLI matrix passes all five pack/rule/effective-config surfaces. All five final physical scenarios pass with their frozen exact corpora and corrected `QUALIFIED`; their runtime pack/rule-version metadata is asserted in the generic report model. Full local and remote acceptance passed; see the [PS-0.10F acceptance matrix](../PS0_10F_ACCEPTANCE_MATRIX.md), including run 38120389177.
