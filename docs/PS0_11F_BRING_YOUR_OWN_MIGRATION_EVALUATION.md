# PS-0.11F — Bring-Your-Own-Migration Evaluation

## Status

**Accepted (October 26, 2026), by repository-owner direction.** PS-0.11F
started from governed `main` `2d5744753309643d7451123d59bf44b338472d4b`,
tree `d6c9e33ae8e2e4d48ebf0d278249f55ef76dce43`. Exact source candidate
`9748d22889dce85584915dfea06df037f925bf30`, tree
`1d96d7f44133789831cb17c3951b6dc5a3187daf`, passed push CI `40269503856`
and PR CI `40269505613` with all eleven required jobs green. PR #16 merged
to `main` as `e811c08729609afdd3a2cfdf4cb7634012871205` with the same tree.
All 45 acceptance gates pass; see the [acceptance matrix](PS0_11F_ACCEPTANCE_MATRIX.md).

## Objective

Prove that an evaluator can use the packaged ProofShift product and ordinary
authoring commands to configure and independently assure a realistic
customer-shaped migration that is not one of the built-in demos. The evaluator
checkpoints the source, runs a separate synthetic external migration fixture,
observes the populated target read-only, inspects persisted Evidence, corrects
the external load, and compares repeated attempts.

The primary sample is a deterministic synthetic municipal public-works
work-order migration. It is an evaluation fixture, not a domain pack, demo
scenario, migration engine, or customer dataset.

## Product boundary

PS-0.11F exposes the existing
`VerificationService.VerifyExternalTargetAsync` workflow. Its expected state
comes from the project graph and complete source checkpoint; its observed
state comes from generic target-observer capabilities resolved through the
connector catalog. It must not require Projection or execute the external
migration.

External Verification is read-only. Its persisted Evidence and Verification
ledger remain authoritative. It does not assess Recovery or make a dry-run
qualification claim. Graph-derived expected lineage is not observed vendor
execution lineage.

The shadow dry-run sequence remains separate:

```text
checkpoint → ProofShift Projection → shadow target → Verification
           → Recovery assessment/rehearsal → qualification
```

PS-0.11F concerns independent external assurance:

```text
checkpoint → external migration process → target
           → read-only ProofShift observation → Verification Evidence
```

## Scope

- Add `proofshift verify-external <config> --checkpoint <id-or-path>
  --observation <label> [--json]`, reusing the existing external Verification
  engine and generic observer catalog.
- Add opt-in `--target-role target|shadow-target` and
  `--verification-baseline generic` authoring flags to scaffold and mapping
  import. Existing defaults and reviewed-mapping requirements remain intact.
- Add persisted Verification-only reporting and explicit Verification-run
  comparison selectors. Keep existing dry-run report and comparison selectors
  unchanged.
- Package a deterministic public-works work-orders evaluation kit and run it
  from an extracted Linux package outside the repository checkout.
- Demonstrate source-offline replay after checkpoint, read-only target
  credentials, no target mutation, no Projection or Recovery artifacts,
  deterministic seeded defects, corrected repeat stability, and comparison.
- Preserve the product version, framework-dependent package contract,
  required CI check names, all existing domain demos, and all existing
  fingerprints.

## Out of scope

Production migration execution or rollback, external target mutation,
customer-specific domain packs, new connectors, dynamic plugins, user-supplied
code/rules, automatic semantic inference, UI/API/authentication, SaaS, release,
publication, tag, signing, and PS-0.11G are excluded.

## Acceptance

The evidence-driven gate list is maintained in the
[PS-0.11F acceptance matrix](PS0_11F_ACCEPTANCE_MATRIX.md). Gates start as
`NOT YET` and change only against recorded evidence. The repository owner
formally accepted PS-0.11F on October 26, 2026; the implementation agent did
not self-accept it. Formal acceptance of ADR-0035 remains separate.

## Validation and candidate policy

Run focused local tests first, then the documented locked restore, build, and
applicable test suites. Docker-backed target-mutation and package-only Linux
evaluation belong in the existing protected CI surface. Preserve all eleven
required check names and strict current-main policy. Record exact candidate
SHA/tree, push run, PR-context tested SHA/tree, and package evidence. PR #16
was merged before formal acceptance; the repository owner directed this
post-merge acceptance, and the sequence is recorded in the matrix. Future
milestone merges remain subject to formal review and repository governance.
