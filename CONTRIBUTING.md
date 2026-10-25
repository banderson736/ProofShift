# Contributing

ProofShift changes are scoped to an assigned milestone or explicitly approved
maintenance task. Keep pull requests focused, preserve existing assurance
semantics unless the assignment explicitly changes them, and update directly
related documentation.

## Branch and review workflow

- Start a scoped branch from current `main`; do not push feature work directly
  to `main`.
- Use a pull request for every change to `main`. CODEOWNERS records ownership;
  it does not imply a required reviewer approval.
- Record the candidate commit SHA and Git tree in the PR. CI must validate the
  exact candidate and, before merge, its PR context against current `main`.
- Do not self-accept a milestone. Formal acceptance is separate from
  implementation.
- Do not expand a milestone or begin later work without explicit assignment.

## Local validation

Use the repository's .NET 10 SDK and locked dependencies:

```powershell
dotnet restore ProofShift.sln --locked-mode
dotnet build ProofShift.sln --no-restore
```

Run the focused tests affected by the change, then the relevant full test
surface. Docker-backed connector and packaged-demo tests require a
Docker-compatible environment; local skips do not replace required CI.

Regenerate lockfiles only through normal NuGet restore after a manifest or
project-graph change. Preserve platform-specific lockfiles. CI uses
`--locked-mode`, so an out-of-date lockfile must fail rather than drift.

## Repository hygiene

- Stage explicit paths; do not use `git add .` or `git commit -a`.
- Do not commit generated `.proofshift`, `bin`, `obj`, `TestResults`,
  benchmark outputs, packages, local environment files, secrets, or production
  data.
- Keep examples and fixtures deterministic and synthetic.
- Pin GitHub Actions to full verified commit SHAs with the upstream release
  tag in a comment.
- Dependency changes must update manifests and the appropriate lockfiles,
  pass locked restore and CI, and receive normal review. Dependabot does not
  auto-merge.

## Candidate and acceptance

Before commit, inspect `git status`, `git diff --stat`, `git diff`, and
`git diff --check`. After commit, verify the commit SHA/tree and a clean
worktree. The PR must pass every required status check. A merge must preserve
the reviewed candidate lineage; routine work must not bypass repository
protection.
