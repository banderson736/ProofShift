# ADR-0033: Product Release Identity and Reproducible Distribution

- Status: Accepted under PS-0.11C
- Date: 2026-10-25
- Supersedes: none

## Context

The evaluator package has historically exposed internal milestone labels as product identity. It also relies on a permissive repository SDK selection and unlocked NuGet restore, while package metadata does not identify the source tree or aggregate dependency graph. The next distribution slice needs a stable product version and artifacts that an evaluator can identify, reproduce, and inspect without weakening the framework-dependent runtime boundary.

## Decision

- Give the product an independent semantic version, `0.1.0-preview.1`; keep PS-0.11C as an internal development milestone.
- Define the product version once in central MSBuild configuration and derive CLI output, assembly identity, archive names/root, package manifest, provenance sidecars, and smoke expectations from it.
- Record both source commit revision and Git tree. The revision describes commit topology; the tree identifies exact source content.
- Pin .NET SDK 10.0.402 with roll-forward disabled and use committed NuGet lock files with locked-mode restore.
- Keep separate committed Windows and Linux lock files for projects whose dependency graphs vary by host OS; use one lock file for platform-invariant graphs.
- Compute a deterministic aggregate dependency-lock fingerprint from sorted relative lock paths and their SHA-256 hashes.
- Keep evaluator packages framework-dependent on the .NET 10 runtime. Repository SDK policy is not shipped.
- Make package metadata and archive construction deterministic; release packages require a clean source worktree.
- Emit deterministic, unsigned provenance containing source, build, dependency, content, and archive identity. This metadata is not a cryptographic signature or attestation.
- Require independent clean-runner, same-platform Windows and Linux builds to produce equal archive, content, and provenance values.

## Alternatives

- Continue using internal milestone names as product versions: rejected because development planning labels are not stable evaluator-facing releases.
- Probe Git or the host at runtime for version information: rejected because ordinary product execution must not depend on repository state or machine-specific identity.
- Use an SDK range or unlocked restore: rejected because it cannot identify a reproducible build input set.
- Publish self-contained binaries or add an installer: rejected as unnecessary distribution expansion; the accepted model is framework-dependent.
- Treat a checksum/provenance file as a digital signature: rejected because there is no signing authority or cryptographic authenticity claim in this scope.

## Consequences

- Product identity can remain stable while internal milestone names change.
- Package metadata distinguishes source revision from source tree and names the locked dependency graph and SDK used.
- Evaluators must install the compatible .NET 10 runtime; package construction and development require the pinned SDK.
- Independent reproducibility gates may expose archive-format or build nondeterminism; such differences must be investigated, not waived.
- No public release, publication, code signing, self-contained deployment, or installer is authorized by this decision.

## Validation

Accepted on exact source candidate `286ea550e559b360fd9c85c2a65e2b1394a95408`, tree `d32fab40faa4bfc08c28b0742c46f8f372ae26c7`, after [GitHub Actions run 40178365888](https://github.com/banderson736/ProofShift/actions/runs/40178365888) passed. The candidate was merged to `main` as `5a1a3ec064aca98266cf0abb3c7749f7416da67e` with the same tree. The [PS-0.11C acceptance matrix](../PS0_11C_ACCEPTANCE_MATRIX.md) records all 37 passing gates, exact test totals, runtime-only evidence, independent Windows/Linux archive and content hashes, and platform-specific dependency-lock fingerprints.
