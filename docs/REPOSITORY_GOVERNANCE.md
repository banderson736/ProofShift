# Repository Governance

This document defines the expected enforceable repository state. It does not
grant product rights or substitute for GitHub's actual settings. Read back
remote settings with [`verify-repository-governance.ps1`](../scripts/verify-repository-governance.ps1)
after configuration changes.

## Repository identity

- Repository: `banderson736/ProofShift`
- Default branch: `main`
- Visibility: public
- License: proprietary, all rights reserved; no open-source license is
  granted. Public visibility is not a permission grant.
- Product identity: `ProofShift 0.1.0-preview.1`; it is not publicly
  released, tagged, signed, or published.

Repository visibility is owner-controlled and must not be changed by an
implementation task without explicit owner authorization.

## Main branch workflow

Changes to `main` must arrive through a pull request. Because the repository
has a solo maintainer, require zero approving reviews and do not require
CODEOWNER approval. The required status checks are:

```text
build-and-test
dependency-review
remote-storage-integration
oracle-integration
db2-integration
windows-package-reproducibility-b
linux-package-reproducibility-b
windows-package-smoke
linux-package-demos
package-reproducibility
linux-runtime-only-smoke
```

All upstream jobs and their aggregate consumers are required individually.
Require the PR branch to be current with `main` when GitHub supports strict
required-status-check enforcement. Deny force pushes and branch deletion on
`main`. Allow merge commits only, preserving candidate SHA/tree lineage, and
delete merged feature branches automatically.

Any bypass is reserved for repository recovery or emergency administration,
not normal feature merging. If configured, restrict it to repository
administrators only. Do not use bypass for routine milestone work.

## Ownership and contributions

[`CODEOWNERS`](../.github/CODEOWNERS) records repository ownership. The
[`CONTRIBUTING.md`](../CONTRIBUTING.md) and PR template describe scoped
branches, candidate identity, locked restore, tests, and independent formal
acceptance.

## Dependencies and workflow supply chain

NuGet and GitHub Actions are monitored weekly by Dependabot. There is no
automatic merge. Dependency changes require manifest review, generated
lockfiles, locked restore, all required CI, and review. Do not hand-edit
lockfiles or allow unlocked CI restore.

Every GitHub Action must use a full commit SHA verified against an official
upstream release, with the release name adjacent as a comment. Workflow
permissions are least-privilege; `pull_request_target`, untrusted artifact
execution across privilege boundaries, and secrets exposed to untrusted
pull-request code are prohibited.

## Data, secrets, and security

Only deterministic synthetic data belongs in the repository. Never add
customer, employer, government, production, PII, PHI, credential, confidential
schema, or proprietary employer/client material. Keep resolved secrets out of
logs, hashes, diagnostics, and evidence.

Use GitHub private vulnerability reporting when it is enabled. Do not include
vulnerability details or sensitive data in public issues. Do not claim
secret-scanning or push-protection coverage unless remote settings confirm
those features are enabled. See [SECURITY.md](../.github/SECURITY.md) and
[Security, Data Handling, and IP Boundaries](SECURITY_AND_IP_BOUNDARIES.md).

## Release and acceptance

Internal milestone completion is not a public release. Tags, GitHub Releases,
packages, and signatures require a separate explicit release authorization.
Acceptance records must identify the exact source candidate SHA and tree,
push CI, PR CI, all required job conclusions, test totals, distribution
results, and known limitations. The implementation author does not self-accept
the milestone.

## Remote settings readback

The expected settings above are acceptance requirements, not claims that
GitHub has enforced them. Record actual visibility, default branch, ruleset
enforcement/target/checks, merge methods, branch deletion, private
vulnerability reporting, and secret scanning in the milestone acceptance
matrix after reading them from GitHub.
