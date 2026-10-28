# PS-0.11G — Pilot Readiness Acceptance

## Status

**In Progress. Not self-accepted.** This is the final PS-0.11 milestone and
starts from accepted `main` `810f2f450176bde1eee0edcc200650d633b8f04b`,
tree `8ec082eb21683dec06ee48971f49e0d088cdea64`, on branch
`ps-0.11g-pilot-readiness-acceptance`.

PS-0.11F remains accepted (October 26, 2026), with 45 PASS, 0 FAIL, and 0 NOT
YET. See the [PS-0.11F acceptance matrix](PS0_11F_ACCEPTANCE_MATRIX.md).
ADR-0035 remains Proposed under PS-0.11F; this milestone does not change it.

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

The evidence-driven gates are in the [PS-0.11G acceptance matrix](PS0_11G_ACCEPTANCE_MATRIX.md);
all gates begin `NOT YET`. Protected GitHub Actions must provide exact
candidate push and PR-context evidence for all eleven required checks.
Windows remains package-only; physical demo/BYOM evidence remains limited to
the extracted Linux package in the protected Ubuntu Docker workflow. The
runtime-only job must prove the front door without an SDK. Independent
Windows/Linux A/B package builds must retain exact archive/content/provenance
equality for each platform.

The physical package rehearsal should emit `pilot-readiness-result.json`.
It is a CI/review artifact, not a ProofShift persistence format. It must
contain deterministic product/source/package/runtime and pass/fail facts,
including whether the source was offline, target observation was read-only,
target mutation was detected, and Projection/Recovery artifacts were absent.
It must not contain credentials, raw migration values, connection strings,
or temporary runner paths.

## Decision boundary

The implementation agent does not accept this milestone. Evidence should
allow an independent reviewer to choose `GO — controlled technical
pilot/evaluation ready` or `NO-GO — blocking issue remains`.

A GO decision would not mean production readiness, production migration or
rollback authority, public/commercial release, paid trial, redistribution,
security/compliance or domain certification, or an SLA. The package remains
proprietary and its identity remains `ProofShift 0.1.0-preview.1`.

## Pilot-facing limitations

The supported product remains a CLI-first, framework-dependent .NET 10 x64
package. Windows evidence is package-only; Ubuntu evidence is limited to the
packaged Docker evaluation and does not establish general Linux support.
There is no production migration execution, external-target rollback, web UI,
hosted service, authentication/RBAC, FHIR Bulk, generic REST connector,
automatic semantic inference, regulatory/domain certification, or SLA.

Do not merge before formal review. Do not begin PS-1.0. Production migration
execution and rollback remain out of scope.
