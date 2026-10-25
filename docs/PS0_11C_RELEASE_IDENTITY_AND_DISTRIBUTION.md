# PS-0.11C — Release Identity & Reproducible Distribution

## Status

**In Progress**, starting from accepted `main` source baseline `464ef14b235c93e08db58d575595a71eced0bafe`. PS-0.11B documentation closeout is a separate documentation-only commit on the local branch; the product implementation baseline remains the stated merge.

Product identity: **ProofShift 0.1.0-preview.1**. Development milestone: **PS-0.11C**. The milestone is not part of the product version.

## Objective

Produce deterministic, dependency-locked, provenance-bearing, framework-dependent evaluation artifacts. Evaluators must identify the product release, reproduce its package from the same source tree and locked inputs, inspect unsigned provenance, and run basic product operations using the compatible .NET runtime without requiring the SDK.

This is a release-engineering milestone, not an assurance-feature milestone. Preserve accepted configuration, graph, checkpoint, Projection, Verification, Evidence, Recovery, reporting, qualification, and five-domain scenario semantics exactly.

## Release contract

- `Directory.Build.props` is the one product-version authority. It drives assembly metadata, CLI version commands, package identity, manifests, sidecars, and validation.
- Use exact .NET SDK `10.0.402` with roll-forward disabled. Lock every participating project dependency graph in committed `packages.lock.json` files and require locked restores in CI.
- Packages are framework-dependent .NET 10 artifacts. Operators need the compatible .NET 10 runtime; package build and development still require the SDK. Do not ship `global.json`.
- Name archives and archive roots from the product version and target platform. Keep package format `proofshift-evaluation-package-v2` independent from product version.
- Record source revision and Git tree separately, along with SDK, framework, dependency-lock fingerprint, content digest, platform, entry point, and scenario runner. Sidecars also record archive name and archive SHA-256.
- Provenance is deterministic and unsigned. It is not a cryptographic signature or an attestation service.
- Build release packages only from clean source trees. Never place timestamps, runner names, workspace paths, random IDs, usernames, or absolute checkout paths in deterministic package metadata.
- Prove byte-identical archive, content, and deterministic provenance fields across independent clean runners for both `win-x64` and `linux-x64`.
- Prove basic extracted Linux package operations with a runtime-only environment containing no SDK. Keep the Windows claim package-only and the Ubuntu five-domain claim limited to packaged Docker evaluation.
- Keep test assemblies, test frameworks, source, repository state, and secrets out of evaluator artifacts. Third-party notices remain derived from the actual published runtime dependency graphs.

## Explicit exclusions

No public GitHub Release, tag, NuGet publication, public registry, Docker image publication, new signing credentials, self-contained or single-file publishing, NativeAOT, installer/MSI, production migration/rollback, new domains/connectors, UI/API, AI authoring, repository governance, or unrelated refactoring. Do not begin PS-0.11D.

## Acceptance evidence

All gates begin NOT YET in the [acceptance matrix](PS0_11C_ACCEPTANCE_MATRIX.md). The matrix is completed only from exact-candidate CI and independent-build evidence. PS-0.11C remains In Progress until formal review and acceptance; no self-acceptance is authorized.
