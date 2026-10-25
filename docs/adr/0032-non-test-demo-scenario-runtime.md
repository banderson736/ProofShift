# ADR-0032: Non-Test Demo Scenario Runtime

- Status: Proposed under PS-0.11B
- Date: 2026-10-27
- Supersedes: the test-module execution portions of [ADR-0029](0029-generic-demo-scenario-contract.md) for the five physical packaged scenarios

## Context

The accepted PS-0.10G demo manifest lifecycle uses the generic CLI for discovery, preflight, workspace ownership, reporting, and reset. Its physical scenario implementations, however, execute through EndToEnd test projects in source and a packaged MTP test module in the binary evaluation package. Product demo execution therefore depends on test discovery/runtime infrastructure and the EndToEnd assembly.

PS-0.11B requires the same accepted synthetic scenarios and output assertions to run without that test boundary. The generic CLI must remain domain-neutral, and the scenario implementation must not be duplicated between package runtime and tests.

## Decision

- Put the five reusable physical scenario implementations and shared fixture lifecycle support in `ProofShift.DemoScenarios`, a non-test runtime library.
- Host scenario execution in the standalone `ProofShift.DemoRunner`. The generic CLI passes the selected content root, scenario ID, workspace, and workspace-relative result path explicitly; the runner writes structured and human-readable results within the workspace.
- Keep the generic CLI responsible for manifest validation and lifecycle orchestration. It recognizes only explicitly supported runner types, validates runner arguments and result paths, and fails closed for unknown runner types.
- Make EndToEnd tests consume the same scenario library and independently assert exact accepted results. Keep all test-framework and test-project dependencies out of the DemoRunner/DemoScenarios runtime graph and evaluation package.
- Permit Testcontainers in the runtime graph only where required for the physical synthetic fixture lifecycle. Do not change domain scenario semantics, persisted reporting, report schemas, qualification, Evidence, or Recovery behavior.
- Preserve the PS-0.10G package identity and platform claim boundaries; PS-0.11B does not introduce a release-version change.

## Alternatives

- Keep the MTP fixture module as the packaged runner: rejected because product demos would continue to depend on a test assembly and test-platform runtime.
- Duplicate scenario logic into a new runtime implementation while keeping tests as separate fixtures: rejected because test and package behavior could drift and create two sources of truth.
- Put scenario-specific orchestration in `ProofShift.Cli`: rejected because it would make generic manifest handling depend on concrete packs and domains.
- Add a general-purpose workflow or plugin engine: rejected because the existing ordered manifest stages and one scenario runner satisfy the bounded evaluator need.

## Consequences

- The DemoScenarios assembly is an application-composition boundary that may reference the five concrete packs and required connector implementations. Core and generic CLI code remain pack-neutral.
- `DemoScenarioExecutionContext.ContentRootDirectory` identifies the root containing package-owned `scenarios/`, `docs/`, and related evaluator content. Repository execution supplies the repository root; extracted-package execution supplies the extracted package root. Scenario resource paths are relative to this contract, never inferred from the runner's assembly directory or a presumed repository layout.
- DemoRunner failures use a shared, strict sanitized diagnostic contract. The CLI forwards only a matching one-line diagnostic; arbitrary stderr and stack traces remain suppressed.
- EndToEnd assertions remain tests, but physical scenario lifecycle and result production are ordinary runtime code.
- Package notices must be generated from the published CLI, DemoRunner, and DemoScenarios dependency graphs; package denylist checks must exclude test assemblies, test-only dependencies, test source, and test-runner metadata.
- Local physical execution still requires Docker-compatible services. Windows acceptance remains package-only; Ubuntu acceptance is limited to the packaged Docker evaluation and is not a general Linux support claim.
- Runtime/package and scenario acceptance remain pending until exact-candidate CI and normal review pass. This proposal does not self-accept PS-0.11B.

## Validation

Pending. Evidence is tracked in the [PS-0.11B acceptance matrix](../PS0_11B_ACCEPTANCE_MATRIX.md).
