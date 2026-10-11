# ADR-0029: Generic Demo Scenario Contract

- Status: Proposed for PS-0.10G review
- Date: 2026-10-11

## Context

The accepted assurance scenarios are exercised through a mixture of physical test fixtures and a Pension-oriented PowerShell operator script. PS-0.10G needs an evaluator-facing list/describe/preflight/run/reset workflow without creating duplicated per-pack orchestration or coupling demo presentation to Verification identity.

## Decision

- Describe evaluator scenarios with a strict, versioned JSON manifest discovered from `scenarios/demos/<id>/scenario.json`.
- Keep manifests in the CLI/package layer. They reference the ordinary ProofShift project configuration and the accepted `PS0_10F_SCENARIO_INDEX.json` for frozen scenario counts and claim boundaries; they do not configure Verification or alter graph, rule, Evidence, Recovery, or semantic fingerprints.
- Use one generic CLI catalog and lifecycle runner for every pack. Ordered stages invoke ProofShift CLI commands or a bounded, repository-relative `dotnet-test` fixture through `ProcessStartInfo.ArgumentList`, without a shell. The manifest declares expected exit/outcome, timeout, a captured output log, an optional structured result file, typed JSON Pointer assertions, and typed result bindings for later stages/presentation.
- For the binary-only evaluation package, `dotnet-test-module` executes a packaged MTP test assembly directly with a required bounded `-class` filter. The source checkout uses `dotnet-test` against the repository project; package assembly conversion replaces only that runner stage and updates its fixture requirement. Source code is not distributed because the repository is all-rights-reserved.
- The evaluation package is built by a deterministic PowerShell script from an explicit allowlist. It excludes source projects and debug/developer state, carries the published CLI and compiled fixture binaries, and derives third-party notices from the resolved CLI and fixture NuGet graphs. Package manifests are adapted to the binary runner without changing checked-in source manifests.
- `projectPath` is optional for fixture-owned scenarios whose accepted test constructs its project/configuration in code. If omitted, `{{project}}` is unavailable and any stage that references it is rejected. If present, the project is materialized into the owned workspace with the existing reparse-point/path checks.
- Distinguish an expected defective or unsafe-Recovery observation from a command/runtime error. Expected nonzero stages must persist structured output and pass assertions; process exit alone is insufficient.
- Preflight declared tools, disk, ports, and files before allocating a scenario workspace. Run output belongs under `.proofshift/demos/<scenario-id>` and is written with an ownership marker. Reset removes only that marker-verified workspace and refuses reparse points or uncertain paths.
- Preserve domain semantics and fixture behavior in the existing pack, project, and test abstractions. The manifest stores presentation/packaging metadata and stage references rather than introducing a second rule or Evidence model.

## Alternatives

- Keep one shell script per pack: rejected because lifecycle, exit-code handling, and cleanup would drift and duplicate orchestration.
- Put demo metadata into semantic project/rule configuration: rejected because display names, paths, and packaging must not change semantic identity.
- Add a general workflow engine: deferred; ordered CLI stages satisfy the current evaluator need without a scheduler, plugin system, or new runtime dependency.
- Let reset delete caller-selected paths: rejected because uncertain ownership could erase user data. Reset is fixed to the scenario-owned workspace and fails closed.

## Consequences

- New domain demos can be registered with declarative manifests and reuse the same discovery/preflight/stage/reset implementation.
- Accepted physical tests remain the assurance source of truth; the runner captures their transcript separately from result JSON and displays only declared structured facts. Fixture-owned kits do not borrow another domain's project configuration.
- The evaluation archive contains the published CLI and compiled MTP fixture binaries, not test sources, and records an allowlist, denylist, source revision, content digest, and third-party notices. Manifest runner conversion preserves the accepted fixture class and timeout while keeping result files inside the owned workspace.
- The package is framework-dependent and requires .NET 10 plus Docker-compatible services for physical demos. The PS-0.10G clean acceptance campaign separates Windows package-only validation on `windows-latest` from Docker-backed packaged-demo validation on `ubuntu-latest`. The latter validates the packaged evaluation workflow on Ubuntu; it does not claim general-purpose Linux product support. Ordinary Linux regression CI alone does not validate the evaluator path.
- Accepted 0.10F expectations remain the source of truth for demo assertion values, while demo-scale fixtures can declare separate expectations explicitly.
- Stage and report presentation is operational metadata, not Evidence truth. Reports consume persisted Evidence and do not create or rewrite semantic findings.
- The first G1 manifest contract is not itself a claim that Pension or cross-domain evaluator demos are complete. Those are G2/G3 gates in `PS0_10G_ACCEPTANCE_MATRIX.md`.
- The v1 runner is sequential and local. It is not a general workflow scheduler or production migration orchestrator.

## Validation

G1 process tests cover deterministic ordering/output, accepted-index binding, missing fixture and occupied-port preflight, expected nonzero verification recognition with result assertions, rejection of unasserted expected-defect stages, reset ownership refusal, unrelated workspace preservation, symlink rejection where supported, and reset/run repeatability. Full 0.10G acceptance remains pending in the 60-item matrix.
