# PS-0.11C — Release Identity & Reproducible Distribution

## Status

**Accepted — October 25, 2026.** Candidate `286ea550e559b360fd9c85c2a65e2b1394a95408` with tree `d32fab40faa4bfc08c28b0742c46f8f372ae26c7` passed [GitHub Actions run 40178365888](https://github.com/banderson736/ProofShift/actions/runs/40178365888) and was merged to `main` as `5a1a3ec064aca98266cf0abb3c7749f7416da67e` with the same tree.

Product identity: **ProofShift 0.1.0-preview.1**. Development milestone: **PS-0.11C**. The milestone is not part of the product version.

## Objective

Produce deterministic, dependency-locked, provenance-bearing, framework-dependent evaluation artifacts. Evaluators must identify the product release, reproduce its package from the same source tree and locked inputs, inspect unsigned provenance, and run basic product operations using the compatible .NET runtime without requiring the SDK.

This is a release-engineering milestone, not an assurance-feature milestone. Preserve accepted configuration, graph, checkpoint, Projection, Verification, Evidence, Recovery, reporting, qualification, and five-domain scenario semantics exactly.

## Release contract

- `Directory.Build.props` is the one product-version authority. It drives assembly metadata, CLI version commands, package identity, manifests, sidecars, and validation.
- Use exact .NET SDK `10.0.402` with roll-forward disabled. Lock every participating project dependency graph and require locked restores in CI. Projects whose dependency graph varies by host OS use separate committed Windows and Linux lock files; common graphs use one committed `packages.lock.json`.
- Packages are framework-dependent .NET 10 artifacts. Operators need the compatible .NET 10 runtime; package build and development still require the SDK. Do not ship `global.json`.
- Name archives and archive roots from the product version and target platform. Keep package format `proofshift-evaluation-package-v2` independent from product version.
- Record source revision and Git tree separately, along with SDK, framework, dependency-lock fingerprint, content digest, platform, entry point, and scenario runner. Sidecars also record archive name and archive SHA-256.
- Provenance is deterministic and unsigned. It is not a cryptographic signature or an attestation service.
- Build release packages only from clean source trees. Never place timestamps, runner names, workspace paths, random IDs, usernames, or absolute checkout paths in deterministic package metadata.
- Prove byte-identical archive, content, and deterministic provenance fields across independent clean runners for both `win-x64` and `linux-x64`.
- Prove basic extracted Linux package operations with a runtime-only environment containing no SDK. Keep the Windows claim package-only and the Ubuntu five-domain claim limited to packaged Docker evaluation.
- Keep test assemblies, test frameworks, source, repository state, and secrets out of evaluator artifacts. Third-party notices remain derived from the actual published runtime dependency graphs.

## Explicit exclusions

No public GitHub Release, tag, NuGet publication, public registry, Docker image publication, new signing credentials, self-contained or single-file publishing, NativeAOT, installer/MSI, production migration/rollback, new domains/connectors, UI/API, AI authoring, repository governance, or unrelated refactoring was part of PS-0.11C. Those remain outside its accepted scope.

## Acceptance evidence

All 37 gates passed: 350 tests passed with 0 failures and 0 skips across build-and-test, RemoteStorage, Oracle, and Db2; Windows package-only smoke passed; all five Ubuntu packaged Docker demos passed; independent Windows and Linux builds matched archive, content, and deterministic provenance; and runtime-only evaluation passed on Microsoft.NETCore.App 10.0.13 with no SDK installed. See the [acceptance matrix](PS0_11C_ACCEPTANCE_MATRIX.md) for hashes and platform-specific lock fingerprints. PS-0.11C is accepted; no public release, tag, publication, or signing was performed.
