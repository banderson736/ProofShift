# PS-1.0B Acceptance Matrix

Status: **In Progress.** No PS-1.0B candidate has been accepted. Local
PS-1.0B.2 through PS-1.0B.5 evidence closes only the corresponding
implementation checks. The PS-1.0B.6 extracted-package harness is implemented,
but provider-backed packaged customer workflows and exact-candidate
acceptance gates remain outstanding.

| # | Acceptance gate | Status | Evidence / remaining work |
|---|---|---|---|
| 1 | Fresh workspace can be initialized from the packaged product | NOT YET | Local CLI initialization/validation is the first slice; extracted-package validation remains required. |
| 2 | Discovery artifacts can be incorporated without repository-only dependencies | NOT YET | Integrate packaged read-only discovery and existing fingerprints into workspace paths. |
| 3 | Mapping/configuration scaffold retains explicit review requirements | NOT YET | Integrate the accepted scaffold/import flow; prove unresolved required mappings block execution. |
| 4 | Preflight is read-only and blocks invalid or unsupported evaluations | NOT YET | PS-1.0B.2 implements offline-by-default machine/human diagnostics and explicit read-only `--connect`; packaged customer workflow and exact-candidate evidence remain required. |
| 5 | Effective plan accurately describes scope, graph, checkpoint, projection, rules, reconciliation, exceptions, evidence, cleanup and exclusions | NOT YET | PS-1.0B.2 implements deterministic JSON/human explanation and plan identity; packaged acceptance remains required. |
| 6 | Clean packaged guided evaluation completes as `QUALIFIED` | NOT YET | `scripts/run-guided-workspace-evaluation.ps1` now drives the extracted-package workflow and asserts the exact clean projection count/aggregate; Docker-backed execution remains outstanding. |
| 7 | Defective packaged guided evaluation completes as `NOT QUALIFIED` | NOT YET | The extracted-package runner replays the checkpoint, mutates only the run-isolated shadow schema after Projection, and asserts exact findings/provenance; provider-backed execution remains outstanding. |
| 8 | Expected exceptions remain non-waiving | NOT YET | Preserve annotations without suppressing failures or reconciliation results. |
| 9 | Applicable evaluation can replay from a complete checkpoint without a live source | NOT YET | The extracted-package runner stops SQL Server, verifies the source port is unreachable, clears the source connection reference, and then checks replay ancestry; Docker-backed execution remains outstanding. |
| 10 | Run manifest binds configuration, discovery, checkpoint, graph, projection, Verification, reconciliation, Evidence and disposition | NOT YET | Define and validate a minimal integrity-bound run manifest. |
| 11 | Portable evidence package is produced | LOCAL PASS | Synthetic completed-run tests create ZIP packages for both dispositions; the package carries the authoritative run, matched effective plan, Evidence, Projection lineage, and Recovery artifacts. Packaged customer workflow remains NOT YET. |
| 12 | Evidence-package validation detects missing, modified, mismatched and broken artifacts/references | LOCAL PASS | Offline validator and local tamper/path regression cases pass. The extracted-package runner adds real-package tampering for deleted/modified Evidence, modified summary/package manifest, and substituted run manifest; packaged execution and exact-candidate CI remain NOT YET. |
| 13 | Human-readable customer summary agrees with machine Evidence | LOCAL PASS | Synthetic-run tests verify deterministic run/plan/Evidence binding, disposition, exact failed rule IDs, annotation non-waiver, reconciliation/materialized-state diagnostics, and redaction. Packaged customer workflow remains NOT YET. |
| 14 | Run comparison identifies meaningful changes, findings and identity differences | LOCAL PASS | Same-workspace comparison tests cover persistent/changed/resolved findings, distinct fan-out target identity, expected-exception changes, reconciliation and materialized-state changes, and semantic equivalence despite distinct run IDs. Packaged customer workflow remains NOT YET. |
| 15 | Interrupted/infrastructure-failed execution cannot fabricate a business disposition | NOT YET | The extracted-package runner exercises unavailable-source preflight, checks the incomplete-run summary, and validates a diagnostic package with no disposition; Docker-backed execution remains outstanding. |
| 16 | Cleanup is safe, explicit, and run-scoped | LOCAL PASS | Preview and CLI `--execute` tests remove only marker-verified `runs/<id>/temporary`; unowned resources are refused; retained run/Evidence/package artifacts remain valid. The extracted-package runner also checks that the configured shadow schema survives cleanup. Configured targets and customer data are not cleanup resources. Packaged workflow remains NOT YET. |
| 17 | Credentials and secrets do not appear in packages, summaries, effective configuration output, or logs | NOT YET | The extracted-package runner scans CLI output, clean/defective summaries, and expanded clean/defective evidence packages for the generated fixture credentials; Docker-backed execution remains outstanding. |
| 18 | Connector capabilities reported by workspace tooling match provider-owned runtime descriptors | NOT YET | Share capability metadata; do not add a second connector capability list. |
| 19 | PS-1.0A invariants and exact clean/defective results remain green | NOT YET | Preserve predicate behavior, checkpoint compatibility, fan-out provenance, non-waiver, materialized-state detection, read-only Verification and shadow isolation. |
| 20 | Extracted Linux package demonstrates the full Docker-backed workflow | NOT YET | The existing packaged BYOM runner now invokes the bounded workspace lifecycle harness for offline validation, preflight/plan, clean and defective runs, package validation/tampering, checkpoint replay, comparison, and preview-first cleanup. A Docker-capable Linux run is still required. |
| 21 | Windows package smoke passes with claims limited to its actual coverage | NOT YET | Maintain package-only Windows claim unless additional scenario execution is explicitly validated. |
| 22 | Exact-candidate remote CI passes all protected checks | NOT YET | Required before acceptance; no self-acceptance or merge before owner acceptance. |

## PS-1.0B.2 local slice evidence

The following focused local checks validate only the workspace preflight and
effective-plan slice; they are not packaged customer-evaluation or
exact-candidate acceptance evidence:

- `dotnet test --project tests\ProofShift.EndToEnd.Tests\ProofShift.EndToEnd.Tests.csproj --filter-class ProofShift.EndToEnd.Tests.AuthoringCliTests` — 30 passed, 0 failed, 0 skipped.
- `dotnet test --project tests\ProofShift.Configuration.Tests\ProofShift.Configuration.Tests.csproj` — 12 passed, 0 failed, 0 skipped.
- `dotnet test --project tests\ProofShift.Graph.Tests\ProofShift.Graph.Tests.csproj` — 30 passed, 0 failed, 0 skipped.
- `dotnet test --project tests\ProofShift.Verification.Tests\ProofShift.Verification.Tests.csproj` — 53 passed, 0 failed, 0 skipped.
- `dotnet build src\ProofShift.Cli\ProofShift.Cli.csproj --no-restore` — passed with 0 warnings and 0 errors.

Docker-backed provider connectivity/package scenarios were not run because
the Docker Desktop engine was unavailable in the local environment. The
explicit `--connect` failure-path test uses a local unreachable endpoint and
asserts that no run/evidence or source/target data is written.

## PS-1.0B.4 local slice evidence

The local package implementation uses `workspace evidence package` and
`workspace evidence validate-package`. ZIP creation/validation tests use
integrity-valid synthetic run fixtures and do not claim a provider-backed
customer evaluation. The focused end-to-end CLI suite passed:

- `dotnet test --project tests\ProofShift.EndToEnd.Tests\ProofShift.EndToEnd.Tests.csproj --filter-class ProofShift.EndToEnd.Tests.AuthoringCliTests` — 37 passed, 0 failed, 0 skipped.
- `dotnet build src\ProofShift.Cli\ProofShift.Cli.csproj --no-restore` — passed with 0 warnings and 0 errors.
- `git diff --check` — passed.

Coverage includes final packages for `QUALIFIED` and `NOT QUALIFIED`,
independent integrity/disposition reporting, diagnostic-only packaging for
an incomplete run, offline CLI validation, stable logical identity on
repackaging, run/Evidence/Projection/Recovery identity binding, preservation
of fan-out lineage records and replay ancestry, and exclusion of workspace
raw input. Tamper tests reject modified Evidence, deleted required Evidence,
modified package metadata, substituted run manifests, source-data artifacts
misclassified as embedded, traversal paths, duplicate/case-colliding entries,
and symlink entries. Docker-backed
successful guided execution/replay, package scenarios from those real runs,
and package acceptance on an extracted product remain NOT YET.

## PS-1.0B.3 local slice evidence

The guided execution implementation composes the existing checkpoint,
Projection, Verification, Recovery, and Evidence services. Workspace runs
have unique directories and an atomically updated, integrity-bound manifest.
Replay readiness skips live source inspection and credential resolution.
The focused end-to-end CLI suite passed after the latest B.3 changes:

- `dotnet test --project tests\ProofShift.EndToEnd.Tests\ProofShift.EndToEnd.Tests.csproj --filter-class ProofShift.EndToEnd.Tests.AuthoringCliTests` — 33 passed, 0 failed, 0 skipped.

The suite verifies that blocked runs persist progress without a disposition
or runtime artifacts, retries receive distinct run IDs/directories, replay
readiness does not inspect or resolve the live source, and checkpoint-backed
Projection context creation does not require the source environment value.
The CLI build and `git diff --check` also passed. These focused checks do not
prove successful provider-backed evaluation, successful offline replay,
manifest tamper rejection, or packaged customer outcomes. Docker-backed
provider scenarios were unavailable locally; provider-backed and packaged
acceptance remains outstanding.

## PS-1.0B.5 local lifecycle evidence

The local B.5 slice adds deterministic human summaries bound to the
authoritative run manifest, effective plan, and integrity-verified Evidence;
same-workspace run comparisons; and preview-first cleanup restricted to
ownership-marked run temporary data. The B.4 package embeds the generated
summary as a required, integrity-checked artifact. These tests use synthetic
run fixtures and do not claim provider-backed execution or packaged customer
acceptance.

- `dotnet test --project tests\ProofShift.EndToEnd.Tests\ProofShift.EndToEnd.Tests.csproj --filter-class ProofShift.EndToEnd.Tests.AuthoringCliTests -v:minimal` — 45 passed, 0 failed, 0 skipped.
- `dotnet build src\ProofShift.Cli\ProofShift.Cli.csproj --no-restore -v:minimal` — passed with 0 warnings and 0 errors.
- `git diff --check` — passed.

Coverage includes summary identity and incomplete-run diagnostics, summary
tampering in package validation, comparison of finding and expected-exception
changes without exposing annotation reasons, and cleanup preview,
execution/idempotency while preserving retained Evidence and packages.
Provider-backed successful guided execution/replay, extracted-package
customer workflows, platform package evidence, and exact-candidate CI remain
NOT YET.

## PS-1.0B.6 local harness evidence

The extracted-package runner is included in the evaluator package and invoked
by the packaged BYOM runner. It checks clean and defective workspace runs,
source-offline checkpoint replay, exact expected findings and non-waiving
exception annotations, run comparison, evidence-package validation and
tampering, cleanup boundaries, incomplete-run diagnostics, and generated
credential redaction. Its provider-backed execution is NOT YET: Docker Desktop
is unavailable in the local environment, so this harness has not produced
packaged customer-evaluation acceptance evidence. The focused CLI tests and
local script checks are implementation evidence only; exact-candidate CI and
platform package gates also remain NOT YET.

## Required business scenarios

The clean packaged scenario must be `QUALIFIED`. The defective packaged
scenario must complete execution as `NOT QUALIFIED`, retain exact failed
rules/findings and provenance, and prove that expected-exception annotations
do not waive results. An integrity-tampering scenario must fail package
validation rather than alter the business disposition.

## Preserved boundaries

- PS-1.0B is evaluation/proof workflow only; production migration and
  production rollback remain unauthorized.
- External Verification remains read-only and does not qualify Recovery.
- Shadow writes remain isolated and must not reach ordinary customer targets.
- Do not begin PS-1.0C, add connectors/packs for breadth, or introduce a UI,
  SaaS, AI-dependent mapping, or distributed execution.
- Windows evidence remains package-only unless the validated workflow proves
  otherwise; Linux scenario evidence remains limited to packaged Docker
  evaluation.
