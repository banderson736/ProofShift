# ADR-0037: Evaluation Workspace Manifest

- Status: Accepted for workspace and guided-run architecture; PS-1.0B remains in progress
- Date: 2026-10-29

## Context

PS-1.0A established customer-data evaluation through the existing
`proofshift.yaml` project configuration and command-level workflows.
PS-1.0B needs one portable location for that configuration, customer inputs,
discovery, reviewed drafts, run records, retained Evidence, and temporary
files, without making customers assemble repository paths or duplicating the
project model.

The configuration loader already owns project references, canonical
fingerprints, and secret-reference semantics. Checkpoints, Projection,
Verification, Recovery, Evidence, reports, and comparisons already have
versioned artifact formats and integrity rules.

## Decision

Introduce a small, versioned `proofshift.workspace.json` manifest as an
application-level envelope. It has a workspace GUID, format version, a
workspace-relative reference to the existing `proofshift.yaml`, and named
relative paths for customer data, discovery artifacts, generated drafts, run
records, Evidence packages, and temporary data.

The manifest does not duplicate project configuration, connector settings,
rules, graph content, or secret values. It does not compute a second
configuration or evidence hash. Existing project and artifact stores remain
authoritative for their own identities and integrity. Runtime commands can
adopt workspace paths incrementally while the existing project commands
remain supported.

`proofshift workspace validate` checks local structure and static project
configuration without connecting to customer systems. A separate
`workspace preflight` reports readiness and remains offline unless the
operator explicitly requests read-only inspection with `--connect`.
`workspace plan` describes the effective configuration but does not execute
it, assign run identities, or create evaluation outputs. The preflight and
plan reuse existing connector capabilities/schemas, graph compilation, rule
resolution, and discovery contracts; they do not add a second runtime model.
`workspace run` assigns a unique run identity only after readiness, records
durable stage progress in a versioned integrity-bound manifest, and composes
the existing checkpoint, Projection, Verification, Recovery, and Evidence
services. Replay skips live source inspection and uses the existing complete
checkpoint identity. External Verification remains read-only and does not
qualify Recovery. Runtime failures and interruptions cannot claim a business
disposition.

Run-specific artifacts are contained under the run directory while immutable
checkpoint references continue to use the existing checkpoint store. The
portable evidence package is a ZIP representation of a completed or
explicitly diagnostic run. The run manifest remains authoritative; a separate
versioned package index describes packaging, hashes, artifact roles, and
embedded/external/omitted classifications without redefining run state.
Creation requires the current safe effective plan to match the run's plan
fingerprint. The package embeds the run, plan, deterministic run-bound
customer summary, Evidence, and run-bound Projection/Recovery artifacts when
applicable. The summary is generated from validated run/Evidence data and is
an inspectable projection, not a second authority. It intentionally excludes raw
checkpoint/source payloads, resolved configuration/secrets, and materialized
target rows. Checkpoint and Verification-ledger identities may be recorded as
external references whose availability is not required for offline
validation.

The logical package identity is SHA-256 over a versioned identity payload
containing the run identity and canonically ordered artifact roles, paths,
classifications, sizes, and hashes. The outer archive SHA-256 is distinct.
The ZIP is validated without extraction; unsupported versions, unknown or
undeclared content, unsafe/colliding paths, symlink entries, missing artifacts,
and mismatched run/artifact identities fail validation. Hashes provide
tamper detection, not signer identity; signing is out of scope.

The PS-1.0B.5 summary is deterministic Markdown bound to the authoritative run
manifest, effective plan, Evidence fingerprint, and product version. It
omits raw values, explanations, and artifact identifiers; target references
are hashed. Incomplete runs cannot gain a business disposition through
summary generation. Package validation verifies the embedded summary's
integrity and binding.

Workspace-run comparison is a local same-workspace operation over validated
run manifests and Evidence. It uses stable target/rule/value identities,
retains fan-out distinctions, and reports expected-exception and
reconciliation changes. It does not compare evidence packages or treat equal
dispositions as equivalence.

Cleanup is preview-first and can remove only a fixed run-local `temporary`
directory whose ownership marker binds the selected workspace/run. It refuses
unowned paths and reparse points, preserves retained Evidence and packages,
and does not delete configured targets. It adds no connector deletion
contract and is idempotent when scratch is already absent.

## Alternatives considered

1. **Treat `proofshift.yaml` as the workspace manifest.** Rejected because it
   already describes project semantics; adding runtime/output paths there
   would conflate project configuration and local workflow state.
2. **Infer identity and directories from the current working directory.**
   Rejected because it is ambiguous for portable runs and historical
   comparison, and cannot explicitly bind paths to a workspace identity.
3. **Create a new workspace runtime assembly and project/configuration
   abstraction.** Deferred because the initial vertical needs only an
   application-level manifest; a new runtime layer would duplicate existing
   configuration and orchestration before a concrete need is proven.

## Consequences

- Customers get a single workspace entry point while existing
  `proofshift.yaml` and direct commands remain usable.
- Workspace references use canonical relative paths and must remain within
  the workspace; role directories cannot overlap.
- Workspace identity is distinct from project, run, checkpoint, graph, and
  Evidence identities.
- The package identity is distinct from the authoritative run identity and
  from the ZIP archive hash. Repackaging does not mutate the run.
- Portable packages may contain customer-sensitive Evidence and are not
  automatically anonymized; source data minimization is explicit.
- The manifest contains no resolved secrets, customer data, or volatile
  values. Evidence authenticity continues to rely on existing
  integrity-bound artifacts rather than a new hashing system.
- Static validation remains offline. Read-only endpoint access is an explicit
  preflight option; neither preflight nor the effective plan captures,
  projects, verifies, or writes evaluation artifacts.
- Guided execution binds existing artifact identities explicitly and keeps
  external Verification read-only. Summary generation, run comparison,
  package embedding, and cleanup are separate commands, not run-orchestrator
  stages.
- The package includes only designated review artifacts; external references
  are integrity-bound records and are not dereferenced by offline validation.
- This ADR does not authorize production migration/rollback or define future
  package signing.
