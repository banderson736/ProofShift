# ADR-0027: Domain Pack Conformance and Genericity Guardrails

Status: Proposed (PS-0.10F1; pending review)

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
- The CLI still has legacy Pension-specific report command flow; pack-neutral report dispatch is required by the later cross-domain reporting gate and is not claimed complete by F1.
- Pension, Utility, Justice, and HCM packs are installed and checked by the same shared conformance/architecture test surface. Their generic runtime assemblies remain free of concrete pack references and semantic branches. FHIR and final five-pack cross-domain proof remain NOT YET.

## Validation

The shared conformance and architecture class passes 7/7 with four packs installed. F1 Pension semantic rules pass 4/4; report aggregation passes 1/1; Pension Fast passes exact 149/0 and corrected `QUALIFIED`. F2 Utility and F3 Justice physical gates remain locally validated. F4 HCM passes pack tests 3/3 and physical assurance 1/1 with 14 seeded defects, exactly 25 findings, a 5,000-worker physical fixture, generic effective-date interval reuse, exact decimal payroll/compensation, and corrected `QUALIFIED` Recovery. These are local slice checks, not full PS-0.10F or remote-CI acceptance.
