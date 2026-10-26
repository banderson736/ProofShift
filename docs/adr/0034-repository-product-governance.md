# ADR-0034: Repository and Product Governance

- Status: Accepted under PS-0.11E (October 25, 2026)
- Date: 2026-10-25
- Decision owners: ProofShift repository owner

## Context

Repository rules have so far been maintained primarily through contributor
instructions. An externally visible repository needs enforceable PR/CI
controls, auditable candidate lineage, dependency and workflow supply-chain
rules, and accurate public-source/legal wording. The repository is public;
the existing proprietary license does not grant open-source rights.

## Decision proposed

1. Protect `main` with PR-only changes, zero required approvals for the
   solo-maintainer workflow, strict required checks, force-push/deletion
   prevention, and merge-commit lineage.
2. Require every current product-acceptance CI job and PR dependency review.
3. Pin GitHub Actions to verified full upstream commit SHAs, use least
   workflow permissions, and monitor NuGet and GitHub Actions weekly through
   Dependabot without auto-merge.
4. Preserve repository visibility as an explicit owner decision. State
   clearly that the public repository is proprietary and public access is not
   an open-source license.
5. Require synthetic-only repository fixtures, secret-safe workflows, and
   separate formal acceptance from implementation.

## Consequences

- Candidate commit/tree identity and merge lineage remain reviewable.
- The maintainer can merge without a required second reviewer, while CI and
  PR controls remain mandatory.
- Dependency and action updates require explicit locked-restore, CI, and
  review.
- GitHub settings must be read back after any administrative changes; policy
  documentation alone is not proof of enforcement.
- No assurance semantics, persisted product formats, package contents, or
  migration behavior are changed.

## Alternatives considered

- Keep protections only in documentation: rejected because they are not
  enforceable.
- Require an approving review or code-owner approval: rejected because the
  repository currently has a solo maintainer and this would block normal
  workflow.
- Change repository visibility to private: rejected because visibility is an
  owner/product/IP decision not authorized by this milestone.
- Allow mutable action tags: rejected because tags can move and do not provide
  immutable workflow dependency identity.

This ADR was formally accepted under PS-0.11E after the repository ruleset,
merge settings, and security-feature state were read back from GitHub and
recorded in the [acceptance matrix](../PS0_11E_ACCEPTANCE_MATRIX.md).
Governed-main commit `2d5744753309643d7451123d59bf44b338472d4b` passed all
eleven required jobs in GitHub Actions run `40244724080`; the run reported
359 passed, 0 failed, and 0 skipped across the test jobs.
