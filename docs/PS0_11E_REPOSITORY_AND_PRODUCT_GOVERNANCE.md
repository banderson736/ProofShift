# PS-0.11E — Repository & Product Governance

## Status

**In Progress; acceptance gates appear complete and are ready for formal
review.** Implementation candidate `fc2411b2bd19e713af097e134388421db9d16fae`
was merged to `main` as `97dee40ff0db732fc83e035e276c10a5747801f0`; both
have tree `3d36bc4c054709bcfa4b996603cb4d43adeb1a60`. Do not self-accept or
begin PS-0.11F.

PS-0.11D is closed as Accepted on October 25, 2026. Its candidate
`6a23233670e5be04d22cdf7b6304fd8090ca493a`, tree
`077aaba4a5c0d9fe69e12627c18fe06557510b20`, passed exact-candidate push CI
`40185263041` and PR CI `40185376379`; all ten jobs passed. It merged to
`main` as `34056aaf7342e1426bda66b0d69ba6ae763c7938` with the same tree.

## Objective

Make repository, contribution, workflow, dependency, security, and
proprietary-source rules explicit and enforceable without changing ProofShift
assurance semantics, product version, package contents, persisted formats, or
demo behavior.

## Workstreams

1. Correct the public-repository/proprietary-license contradiction without
   granting an open-source license.
2. Establish CODEOWNERS, contribution guidance, a concise PR template,
   security policy, and repository-governance policy.
3. Pin all workflow actions to verified full commit SHAs, use least
   privilege, add dependency review on pull requests and candidate pushes,
   and monitor NuGet and GitHub Actions with weekly Dependabot updates.
4. Configure and verify an active `main` ruleset requiring PRs, current-base
   CI, every required CI job, no force-push/deletion, and merge-commit
   lineage. Keep approval requirements at zero for the solo-maintainer
   workflow.
5. Inspect and, where permissions permit, enable private vulnerability
   reporting and supported GitHub secret protection. Record actual remote
   state; never document an unverified setting as enabled.
6. Preserve all accepted tests, five-domain outputs, package reproducibility,
   and runtime-only evaluation. Governance-only files must remain outside the
   evaluator package.

## Remote state observed before configuration

On October 25, 2026, the unauthenticated GitHub API confirmed repository
visibility `public`, default branch `main`, and an empty repository-rulesets
list. The private-vulnerability-reporting endpoint returned
`{"enabled":false}`. The API refused branch-protection readback with HTTP 401;
the GitHub CLI is not installed and the browser settings page is unauthenticated.
The task-provided baseline reports merge commits, squash, and rebase enabled,
and automatic branch deletion disabled; those administrator-only settings
could not be independently read back in this environment.

No visibility change was made.

## Final remote settings readback

Authenticated GitHub API readback on October 25, 2026, confirmed:

- Repository visibility is `public`; default branch is `main`.
- The active `main-protection` repository ruleset targets only
  `refs/heads/main`.
- Pull requests are required; required approvals are `0`; CODEOWNER approval
  is not required.
- The exact eleven required checks are listed in
  [REPOSITORY_GOVERNANCE.md](REPOSITORY_GOVERNANCE.md). Strict/up-to-date
  status checks are enabled.
- Branch deletion and non-fast-forward updates are blocked. No bypass actors
  are configured.
- Pull requests allow the `merge` method only. Repository merge commits are
  enabled; squash and rebase are disabled; merged branches are deleted
  automatically.
- Private vulnerability reporting, secret scanning, and secret-scanning push
  protection are enabled.

The read-only governance verifier was run against authenticated GitHub
readback and reported all checks PASS. Exact evidence is recorded in the
[acceptance matrix](PS0_11E_ACCEPTANCE_MATRIX.md).

## Workflow dependency pins

The upstream release metadata and commit resolution were checked on October
25, 2026. The previous v4 actions had been superseded by Node 24-compatible
releases; the workflow pins the verified current releases without relying on
mutable tags:

| Action | Verified release | Commit SHA |
|---|---|---|
| `actions/checkout` | `v7.0.1` | `3d3c42e5aac5ba805825da76410c181273ba90b1` |
| `actions/setup-dotnet` | `v6.0.0` | `a98b56852c35b8e3190ac28c8c2271da59106c68` |
| `actions/upload-artifact` | `v7.0.2` | `cf430e030ddbb5b0abf93d22962f4752f3646cd9` |
| `actions/download-artifact` | `v8.0.2` | `9000827ccba6bdab643e8b6fd33ac0654aef8333` |
| `actions/dependency-review-action` | `v5.0.0` | `a1d282b36b6f3519aa1f3fc636f609c47dddb294` |

The dependency review job checks pull requests and pushes for newly introduced
high-severity vulnerabilities, does not enforce license policy, and has only
read permissions. Pull requests compare their exact base and head commits;
pushes compare `main` with the pushed commit. This avoids a skipped dependency
review check on candidate-push CI. No secrets are provided to pull-request
code.

## Non-goals and stop conditions

- Do not change repository visibility.
- Do not publish/tag/sign product artifacts or create a release.
- Do not change `src/`, tests, product version, package allowlist, or runtime
  semantics unless a narrowly scoped governance support need is approved.
- Do not weaken required checks, locked restore, or acceptance expectations to
  work around a failing dependency-review or CI job.
- If GitHub settings cannot be applied or read back, leave those gates NOT YET
  and report the exact permission/feature blocker.
- Do not begin PS-0.11F.

## Acceptance

Evidence is tracked in the [PS-0.11E acceptance matrix](PS0_11E_ACCEPTANCE_MATRIX.md).
Formal acceptance requires exact candidate/tree lineage, remote settings
readback, all required CI jobs, unchanged five-domain outcomes, reproducible
packages, and runtime-only smoke. This implementation may report readiness
only; it does not self-accept.
