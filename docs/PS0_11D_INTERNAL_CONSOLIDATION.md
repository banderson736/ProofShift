# PS-0.11D — Internal Consolidation & Maintainability

## Status

**Accepted (October 25, 2026).** Candidate
`6a23233670e5be04d22cdf7b6304fd8090ca493a`, tree
`077aaba4a5c0d9fe69e12627c18fe06557510b20`, passed push CI run
`40185263041` and PR CI run `40185376379`; it was merged to `main` as
`34056aaf7342e1426bda66b0d69ba6ae763c7938` with the same tree.

Product identity remains **ProofShift 0.1.0-preview.1**. This milestone is
internal maintainability work, not a release or performance milestone.

## Objective

Reduce specific responsibility and ownership concentrations while preserving
externally observable behavior, assurance semantics, persisted formats,
evaluator behavior, package contracts, and accepted domain outcomes.

Prefer mechanical extraction with clear ownership. Do not turn the milestone
into a broad rewrite or optimize algorithms as a side effect.

## Workstream A — Connector-internal shared ownership

Remove the CSV-owned source compile links currently used by the Parquet and
StructuredFiles connectors for `DiskBackedIdentityIndex`. Give the single
implementation a narrowly scoped connector-internal owner, preferably
`ProofShift.Connectors.Internal`.

The project may depend only on `ProofShift.Connectors.Abstractions` and
`Microsoft.Data.Sqlite`, as required by the implementation. It must not become
a generic utilities assembly, and Domain/Core/Packs/Graph/Engine/Verification/
Recovery must not depend on it. Keep the index internal and grant internals
access only to CSV, Parquet, and StructuredFiles.

Preserve SHA-256 identity hashing, duplicate detection and stable connector
error codes, cancellation, SQLite scratch behavior, bounded abandoned-file
cleanup, cleanup on failure/disposal, and diagnostic redaction. Add focused
tests for these behaviors and an architecture guard against production source
compile links between connector projects.

## Workstream B — CLI composition decomposition

Reduce `src/ProofShift.Cli/Program.cs` to process entry, top-level routing,
composition, usage, exit-code forwarding, and existing top-level error policy.
Move cohesive workflow implementations into internal command modules and
shared CLI-only output models/writers. Keep `DemoCommands` and
`DiscoveryCommands` separate.

Do not add a reflection framework, dependency-injection container, command bus,
mediator, or external CLI package. Preserve all command names, options,
required arguments, exit codes, stdout/stderr ownership, JSON property names,
human report text where asserted, error redaction, standalone/configured report
paths, and generic pack resolution.

Add focused CLI contract coverage across version, configuration, migration,
and assurance commands, including representative JSON/human output, malformed
arguments, exit codes, stderr destination, stable product version, and absence
of milestone leakage.

## Workstream C — Bounded Verification support extraction

Keep public Verification APIs and SQLite execution behavior unchanged. Extract
pure ordering normalization/comparison/fingerprinting helpers from
`SqliteVerificationWorkspace` into a cohesive internal
`VerificationOrderingKeyCodec` (or equivalent). Add exact tests for null,
string, integer, decimal, temporal and binary/hash values, canonical decimal
ordering including negative/large/equal values, deterministic fingerprints,
and culture independence. Existing normalization output must not change.

Extract diagnostic formatting/writing concerns, including query-plan capture,
workspace sizing, partition-distribution recording, and workset aggregation,
into a narrowly scoped internal diagnostics component. Preserve the diagnostic
schema and performance evidence fields.

Do not extract or change partition worker startup, channel scheduling,
backpressure, partition writer lifecycle, flush/transaction behavior,
expected-workset publication, or read routing. Do not split the large workspace
into partial files and call that consolidation.

## Architecture and dependency constraints

- Keep core and generic assemblies free of concrete domain-pack references.
- Keep concrete packs independent of one another.
- Keep the CLI as composition root for concrete connectors and packs.
- Preserve the DemoRunner/DemoScenarios non-test runtime boundary.
- Retain the direct Reporting → Verification reference.
- Add explicit references where a project consumes another project's types;
  do not rely accidentally on transitive references.
- Do not change product version, package semantics, NuGet versions, or
  dependency-lock contracts unless a required project reference changes the
  restore graph. Generate any necessary locks through normal restore.

## Non-goals and stop conditions

No assurance-semantic changes, persisted-format changes, fingerprint changes,
domain-rule changes, package-contract changes, command-contract changes,
qualification changes, or algorithm/performance work. Do not modify the
accepted five-domain expected results. Do not begin PS-0.11E, publish/tag/sign,
add domains/connectors, production execution/rollback, UI/API/auth, AI
authoring, installer/AOT/self-contained distribution, or broad
Verification/Recovery redesign.

If extraction changes a same-input fingerprint, accepted domain count,
qualification, package reproducibility, CLI output/exit behavior, dependency
direction, or materially affects existing test runtime/resource behavior,
stop and report the regression. Do not weaken expected values to make it pass.

Do not rerun PS-0.10D Large, any 1.5M physical benchmark, or Medium tuning.

## Validation and acceptance

The initial evidence requirements are enumerated in the
[PS-0.11D acceptance matrix](PS0_11D_ACCEPTANCE_MATRIX.md). At minimum, validate
the connector utility and its dependency guard; focused CLI contracts;
Verification ordering and diagnostics; pack conformance; Evidence, Recovery,
report/compare, and architecture tests; the strongest practical local
non-Docker suite; locked restore; and full exact-candidate CI.

Final CI must retain the accepted SDK 10.0.402 and locked restore, all
integration/package jobs, independent Windows/Linux reproducibility, runtime-
only acceptance, and all five packaged Docker demos. Required failures and
unexplained skips are not acceptable.

PS-0.11D remains In Progress until formal review. Do not self-accept or begin
PS-0.11E.
