# PS-0.11G — Pilot Readiness Acceptance

## Status

**Accepted — October 28, 2026. GO — controlled technical pilot/evaluation
ready.** The exact accepted implementation candidate is
`b25a483dd5b728474e3e1f8e1b957885d42e309a`, tree
`5b65c63a819bf1db7a06c29d36a52bc1e5d2598b`. It passed push CI run
`40653602682` and PR-context CI run `40653605811`, each with all eleven
required jobs green. PR #18 merged as
`6eaac3f7de155d775032964739c832cca2317b6f`; its tree is identical to the
accepted candidate. The [acceptance matrix](PS0_11G_ACCEPTANCE_MATRIX.md)
records 40 PASS, 0 FAIL, 0 NOT YET.

The implementation PR merged before the independent PS-0.11G GO/NO-GO review
was issued. This historical sequence deviation is retained: exact-candidate
push CI passed, the PR-context tree was identical to the candidate tree, the
final merge tree was identical, and the independent review evaluated that
tree. Future implementation PRs should return to candidate → exact CI →
independent formal review → acceptance decision → merge unless the repository
owner explicitly directs otherwise.

PS-0.11F remains accepted (October 26, 2026), with 45 PASS, 0 FAIL, and 0 NOT
YET. See the [PS-0.11F acceptance matrix](PS0_11F_ACCEPTANCE_MATRIX.md).
ADR-0035 remains Proposed under PS-0.11F; this milestone does not change it.
PS-0.11 is complete. There is no active implementation assignment. PS-1.0
has not started and requires a new explicit repository-owner assignment.

## Objective

Determine whether an external evaluator can start from only the extracted
ProofShift evaluation archive and shipped documentation, identify and use
the product without repository/source/test knowledge, exercise the packaged
domain demonstrations and a customer-shaped external-migration workflow,
preserve and interpret persisted Evidence, and repeat the workflow within
the documented technical-pilot boundaries.

This is an acceptance-first product handoff, not an assurance-engine feature
tranche. Permitted changes are limited to narrow operator help, package
navigation, evaluator documentation, documentation/package consistency
checks, acceptance harnesses, safe presentation corrections, and status
reconciliation. Stop for formal direction if a substantive product capability
or correctness change appears necessary. Preserve all accepted domain
outcomes, read-only external target Verification, and the separation between
external Verification and shadow Projection/Recovery qualification.

## Acceptance evidence

The [PS-0.11G acceptance matrix](PS0_11G_ACCEPTANCE_MATRIX.md) records all
40 gates as `PASS`, with 0 `FAIL` and 0 `NOT YET`. Exact-candidate push and
PR-context GitHub Actions runs passed all eleven required checks. Windows
remains package-only; physical demo/BYOM evidence is limited to the extracted
Linux package in the protected Ubuntu Docker workflow. The dedicated
runtime-only job proved the front door without an SDK. Independent
Windows/Linux A/B package builds matched archive/content hashes and
deterministic provenance for each platform.

The physical package rehearsal should emit `pilot-readiness-result.json`.
It is a CI/review artifact, not a ProofShift persistence format. It must
contain deterministic product/source/package/runtime and pass/fail facts,
including whether the source was offline, target observation was read-only,
target mutation was detected, and Projection/Recovery artifacts were absent.
It must not contain credentials, raw migration values, connection strings,
or temporary runner paths.

## Formal decision and boundary

The independent formal decision is **GO — controlled technical
pilot/evaluation ready**. ProofShift may be used for a controlled,
owner-authorized technical pilot/evaluation within the documented support
boundaries.

GO does not mean production readiness, GA, public release, commercial
distribution authorization, paid-trial authorization, production migration
execution readiness, production rollback readiness, SLA-backed service,
regulatory certification, security/compliance certification, or domain
certification. The package remains proprietary and its identity remains
`ProofShift 0.1.0-preview.1`.

## Pilot-facing limitations

The supported product remains a CLI-first, framework-dependent .NET 10 x64
package. Windows evidence is package-only; Ubuntu evidence is limited to the
packaged Docker evaluation and does not establish general Linux support.
There is no production migration execution, external-target rollback, web UI,
hosted service, authentication/RBAC, FHIR Bulk, generic REST connector,
automatic semantic inference, regulatory/domain certification, or SLA.

Production migration execution and rollback remain out of scope. Do not begin
PS-1.0 without a new explicit repository-owner assignment.
