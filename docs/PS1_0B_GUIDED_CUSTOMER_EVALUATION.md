# PS-1.0B — Guided Customer Evaluation Workspace & Evidence Package

## Status

**In Progress.** Work starts from the PS-1.0A acceptance-closure merge
`7f6fbae410f81ad8f015cad380594e88d630468d` on branch
`ps-1.0b-guided-customer-evaluation`. PS-1.0A remains accepted and unchanged.
Workspace initialization/validation, the PS-1.0B.2 preflight/effective-plan
slice, PS-1.0B.3 guided execution, the PS-1.0B.4 portable evidence-package
slice, and the local PS-1.0B.5 summary/comparison/cleanup lifecycle are
implemented. A bounded PS-1.0B.6 extracted-package acceptance harness is
implemented but has not run in a Docker-capable environment. Provider-backed
successful execution/replay, packaged customer evaluation, and exact-candidate
milestone evidence remain outstanding. This is not a PS-1.0B acceptance
record.

## Goal and boundaries

Make a packaged customer evaluation repeatable from workspace initialization
through read-only discovery, explicit mapping review, readiness checks,
checkpoint-backed evaluation, and an integrity-verifiable portable evidence
package. The workflow should compose existing ProofShift capabilities and
make each step and conclusion inspectable.

This is an evaluation/proof milestone. It does not authorize production
migration, production rollback, automatic destructive target changes, hosted
service operation, web UI, new connectors/packs, AI-dependent mapping,
distributed execution, or PS-1.0C.

## Proposed architecture

### Workspace envelope

An evaluation workspace is a portable directory with a small
`proofshift.workspace.json` manifest. The manifest has a format version, a
workspace GUID, a relative reference to the existing `proofshift.yaml`, and
relative paths for customer data, discovery artifacts, generated drafts, run
records, evidence packages, and temporary workspace data.

The existing project configuration remains authoritative for systems,
connectors, graph, rules, Recovery, secrets references, and canonical
configuration identity. The workspace manifest must not copy those
definitions or contain resolved credentials. Project IDs, workspace IDs,
run IDs, and artifact fingerprints remain distinct identities.

The initialized layout uses:

| Path | Purpose |
|---|---|
| `data/` | Customer-approved input material; the existing initializer's header-only CSV starter can be replaced |
| `discovery/` | Read-only, versioned discovery artifacts |
| `generated/` | Scaffolds and drafts that remain unapproved until reviewed |
| `runs/` | Workspace run manifests and run-level references |
| `evidence/` | Retained, integrity-verifiable evidence packages |
| `.proofshift/tmp/` | Disposable intermediate workspace data |

Paths are canonical relative paths contained by the workspace. Role paths
must not overlap. The manifest contains references, not customer data,
credentials, volatile timestamps, or a parallel copy of project configuration.

### Validation, preflight, and plan distinction

`workspace validate` checks the manifest version/identity, portable path
shape, required directories, referenced project configuration, and static
configuration/graph/rule/Recovery validity. It does not connect to customer
systems, mutate source/target state, or claim runtime readiness.

`workspace preflight` is a separate readiness operation. Its default mode
checks the effective project configuration, provider-owned connector
capabilities and schemas, declared credential/environment references,
persisted discovery integrity/binding, review states, Recovery policy, rules,
and output-path containment without connecting to endpoints. `--connect`
explicitly requests read-only connector inspection and, when persisted
discovery exists, read-only drift comparison. It does not capture a
checkpoint, project records, run Verification, or write evaluation outputs.
Unsupported operations, invalid configuration, missing declared credentials,
and unapproved required mappings remain blockers. Unprobed connectivity,
provider credential validity, output ACL/write permissions, and absent
discovery are reported as `NOT_CHECKED`, not assumed valid.

`workspace plan` explains the effective graph, systems/endpoints and runtime
capabilities, scope predicates and bound scope fingerprints, mappings and
transformations, rules and rule-set identity, reconciliation, Recovery,
checkpoint/replay, shadow projection, output paths, cleanup boundaries,
expected-exception semantics, and intentionally omitted execution products.
Its fingerprint binds these safe plan inputs, effective packs, the resolved
rule-set fingerprint, and expected-exception identity without serializing
credential values or rule options. The plan remains available when readiness
is blocked, provided the workspace and project configuration are structurally
valid. Neither preflight nor plan executes the evaluation.

### Guided execution and evidence

The PS-1.0B.3 `workspace run` implementation composes the existing checkpoint,
shadow Projection, Verification, Recovery, reconciliation, and Evidence
services. It does not shell out to the CLI or implement parallel runtime
semantics. A fresh run captures a complete checkpoint; replay consumes the
selected complete checkpoint, skips live source inspection/credential
resolution, and passes its identity through the existing checkpoint-backed
services.

Each attempt receives a unique run ID and directory under the configured
`runs/` path. Its versioned `run.json` is atomically updated after readiness
and each execution stage. The manifest integrity hash covers the complete
document; its identity fingerprint binds stable build, workspace, plan,
configuration, graph, discovery, checkpoint, Projection, Verification,
reconciliation, Recovery, Evidence, and disposition identities while
excluding timestamps and progress presentation. Runtime artifacts are stored
inside that run directory and retain their existing formats/integrity checks.

A completed internal evaluation can claim `QUALIFIED` or `NOT QUALIFIED` only
after Verification, Recovery qualification, and Evidence persistence
complete. An incomplete or interrupted run has no disposition. External
Verification remains read-only and records its own Verification outcome, but
does not claim a business disposition or qualify Recovery. Expected-exception
annotations remain non-waiving.

`workspace evidence package` creates an inspectable ZIP representation of a
run; it does not create another execution record or mutate the run. The
package index binds the authoritative run manifest, a fingerprint-matched
effective plan, embedded Evidence, and available Projection/Recovery
artifacts. Checkpoint payloads, raw source data, and materialized target rows
are not copied. The index distinguishes embedded artifacts, intentionally
external references, and intentional omissions. Offline validation rejects
unknown package versions, broken integrity bindings, missing or undeclared
files, artifact roles or classifications outside the v1 policy,
duplicate/colliding/unsafe paths, and symlink entries without extracting the
archive.

The package identity is a deterministic SHA-256 over versioned run identity
and sorted artifact metadata; the ZIP archive SHA-256 is reported separately.
Packaging time, ZIP metadata, and compression are not part of logical
identity. Hashes detect accidental or uncoordinated modification; no
signature or signer identity is provided. Embedded Evidence and Recovery
artifacts can contain customer-sensitive values. Packages are not
automatically anonymized and must be reviewed and retained under the
customer's data-classification policy. Offline validation verifies embedded
content and recorded external-reference metadata; it does not test external
availability.

The B.5 lifecycle is implemented locally. `workspace summary` derives
deterministic Markdown from the authoritative run manifest, regenerated
effective plan, and integrity-verified Evidence. It is bound to the run,
plan, Evidence fingerprint, product version, and manifest identities; it
omits raw values, explanations, and artifact identifiers, and hashes target
references for explanation. Incomplete runs report diagnostics without
inventing a disposition. Expected-exception annotations are listed as
annotations and never waive failed findings. The summary is a required
run-bound artifact in B.4 packages and the offline validator verifies its
integrity and binding.

`workspace compare` compares two local runs from the same workspace; it does
not compare packages. It distinguishes exact persistent findings from
changed, added, and resolved findings using stable rule/code/severity,
target, and expected/actual-value identities. Fan-out targets remain
separate. It reports configuration/graph/plan/discovery/checkpoint/source/
projection/Verification differences, reconciliation failures,
expected-exception changes, and materialized-state changes. A run-ID-only
difference is not a semantic change, and equal dispositions do not establish
equivalence. Annotation identities and changed annotation content are
reported without exposing owner/reason text.

`workspace cleanup` is preview-only by default; `--execute` is explicit.
Cleanup can remove only the fixed `temporary` child of the selected run when
its ownership marker matches the validated workspace and run IDs and the
tree contains no reparse points. It preserves manifests, Evidence, Recovery,
summaries, packages, and checkpoints. It never deletes configured
PostgreSQL schemas, filesystem targets, customer data, or other target
resources because no connector-owned deletion contract exists. Already
absent scratch is an idempotent no-op. These local synthetic-fixture tests
are implementation evidence only; packaged workflows and exact-candidate
acceptance remain outstanding.

## Existing components to reuse

- `proofshift init` generic/Pension templates and the existing configuration
  loader, canonical configuration fingerprint, graph compiler, rule
  registry, and Recovery policy loader.
- Read-only connector discovery, timestamp-independent discovery
  fingerprints, manifest verification, drift comparison, and deterministic
  mapping scaffold/import with explicit review states.
- Existing strict configuration validation, connector-owned schemas and
  capabilities, and effective configuration/rule/mapping explanation.
- Materialized source checkpoints, checkpoint-only replay, isolated shadow
  Projection, the existing Verification and Recovery orchestrator, and
  per-run manifests.
- Persisted Evidence and report formats, integrity checks, persisted-run
  comparison, and the customer cleanup/runbook boundaries.

## New components in the first five slices

- A minimal versioned workspace manifest model/store in the CLI application
  layer; no new domain assembly or duplicate project model.
- `proofshift workspace init` and `proofshift workspace validate`.
- Path containment, overlap, and workspace-layout validation.
- `proofshift workspace preflight` with offline default behavior and an
  explicit read-only `--connect` option.
- `proofshift workspace plan` with deterministic effective-plan identity and
  machine-readable and human-readable output.
- `proofshift workspace run` with unique run directories, durable progress,
  checkpoint capture/replay, and run-scoped artifact references.
- A CLI application-level run manifest with integrity and stable identity
  fingerprints; no second qualification engine or Evidence format.
- `proofshift workspace evidence package` and
  `proofshift workspace evidence validate-package`; the package index is
  separate from, and cryptographically binds, the authoritative execution
  manifest without redefining run state.
- `proofshift workspace summary`, `workspace compare`, and
  `workspace cleanup`; these operate on local run records and do not add
  runtime behavior or a connector deletion API.

## CLI and schema change

```text
proofshift workspace init <directory> [--template generic|pension]
proofshift workspace validate <directory> [--json]
proofshift workspace preflight <directory> [--connect] [--json]
proofshift workspace plan <directory> [--json]
proofshift workspace run <directory> [--checkpoint <id-or-path>] [--observation <label>] [--json]
proofshift workspace evidence package <workspace> <run-id> [--output <zip>] [--diagnostic] [--json]
proofshift workspace evidence validate-package <zip> [--json]
proofshift workspace summary <workspace> <run-id> [--output <path>] [--json]
proofshift workspace compare <workspace> <before-run> <after-run> [--json]
proofshift workspace cleanup <workspace> <run-id> [--plan|--execute] [--json]
```

`workspace init` composes the existing generic/Pension project initializer,
adds only the workspace manifest and named directories, and refuses to
overwrite a non-empty destination. Existing `init`, authoring, and
command-level runtime workflows remain available.

## ADR assessment

ADR-0037 records the additive workspace-manifest boundary and the separation
between offline static validation, read-only preflight, and non-executing
planning. The implementation reuses the existing connector descriptors,
configuration schemas, graph compiler, rule registry, discovery, and runtime
context; it adds no connector contract, Evidence format, configuration
fingerprint, or migration/Recovery semantic change.
The B.4 package remains a portable representation of the run: the run
manifest is authoritative, while the versioned package index records
artifact classification and integrity. It uses existing run, Evidence,
Projection, and Recovery integrity bindings and does not introduce signing.
The deterministic B.5 summary is embedded as a required artifact bound to
that run, plan, and Evidence; package validation regenerates and checks the
summary rather than treating it as a new authority.

## Implementation sequence

1. **Workspace model and initialization** — implemented locally; manifest,
   safe layout, and package-safe initialization/validation. Extracted-package
   acceptance remains outstanding.
2. **Preflight and effective plan** — implemented locally; offline readiness,
   explicit read-only connectivity, and deterministic explanation of
   configured operations. Provider-backed packaged acceptance remains
   outstanding.
3. **Guided execution** — implemented locally; durable lifecycle and manifest,
   effective-plan binding, checkpoint capture/replay, shadow or external
   read-only Verification, Recovery/reconciliation, and Evidence references.
   Provider-backed and packaged acceptance remains outstanding.
4. **Evidence package and integrity validation** — implemented locally;
   ZIP manifest, explicit artifact inclusion/reference policy, deterministic
   logical identity, offline validation, and tamper/path regression coverage.
   Provider-backed/package acceptance remains outstanding.
5. **Customer summary, comparison, and cleanup** — implemented locally;
   provider-backed and packaged acceptance remains outstanding.
6. **Packaged customer workflow and acceptance** — extracted-package
   clean/defective scenarios, replay, interruption and tamper cases, platform
   evidence, and exact-candidate CI. The bounded Linux harness is implemented
   in `scripts/run-guided-workspace-evaluation.ps1` and is invoked by the
   packaged BYOM runner. It asserts exact synthetic fixture counts and
   aggregates, keeps out-of-band mutation within the run-isolated shadow
   schema, stops the SQL Server source before replay, and never treats
   configured targets as cleanup resources. The harness has not yet run in a
   Docker-capable environment; its implementation is not acceptance evidence.

## Current slice limitations

The first five slices establish workspace identity, static validation,
preflight, planning, guided run records, portable package integrity,
run-bound summaries, local run comparison, and bounded cleanup. Default
preflight performs no endpoint access; guided execution performs
read-only readiness inspection before capture or projection. The read-only
plan remains descriptive and assigns no run identity. PS-1.0B remains
unaccepted until all applicable gates in the
[acceptance matrix](PS1_0B_ACCEPTANCE_MATRIX.md) pass on an exact candidate.
