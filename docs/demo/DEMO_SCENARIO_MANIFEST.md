# Demo Scenario Manifest v1

PS-0.10G uses `scenarios/demos/<id>/scenario.json` for evaluator-facing scenario metadata. This is packaging/orchestration metadata only: it is not loaded into `ConfigurationLoader`, `MigrationGraphCompiler`, Verification, Recovery, or any semantic fingerprint.

The CLI discovers manifests by ID in ordinal order. Unknown properties, unsupported versions, mismatched folder IDs, unsafe relative paths, invalid stage commands, and inconsistent expected outcomes fail closed. Manifest JSON uses camelCase and is deterministic source-controlled data.

## Contract

The root document describes:

- `manifestVersion`, `id`, `displayName`, `domain`, pack ID/version, description, and claim boundary;
- approximate runtime, source/target systems, artifact scale, and Docker/.NET/disk/port/file requirements;
- an optional path to the normal ProofShift project configuration when stages use one;
- fixture setup, defective-target preparation, corrected-target preparation, and report locations;
- an acceptance-index reference, which supplies the frozen defect counts and corrected outcomes rather than duplicating them in demo metadata;
- ordered run and reset stages, with an expected exit code and optional JSON Pointer assertions.

Example shape (illustrative only; it is not an installed scenario):

```json
{
  "manifestVersion": 1,
  "id": "pension",
  "displayName": "Pension modernization assurance",
  "domain": "Public pension",
  "pack": { "id": "proofshift.pension", "version": "0.9.0" },
  "description": "Independent verification of configured synthetic pension migration invariants.",
  "claimBoundary": "Configured synthetic pension migration invariants only; not regulatory, actuarial, or benefit-calculation certification.",
  "approximateRuntime": "About 7 minutes on the measured local Docker setup; diagnostic only.",
  "requirements": {
    "docker": true,
    "minimumDotNetSdk": "10.0.100",
    "minimumFreeDiskBytes": 0,
    "requiredPorts": [],
    "requiredFiles": []
  },
  "sourceSystems": ["Legacy pension source"],
  "targetSystems": ["Independently observed target", "Isolated shadow target"],
  "requiredServices": ["Docker-compatible runtime", "SQL Server", "PostgreSQL"],
  "artifactScale": {
    "sourceArtifacts": 8495,
    "targetArtifacts": 8595,
    "unit": "artifacts",
    "notes": "Accepted Pension Fast physical corpus; not the reduced demo acceptance expectation."
  },
  "acceptanceReference": {
    "indexFile": "docs/PS0_10F_SCENARIO_INDEX.json",
    "scenarioId": "pension-fast"
  },
  "projectPath": "scenarios/pension-modernization/ps010b/proofshift.yaml",
  "fixtureSetupSteps": ["Capture the source checkpoint", "Observe the defective target"],
  "defectiveTargetPreparation": "Load the deterministic defective synthetic target through the scenario fixture.",
  "correctedTargetPreparation": "Load the corrected synthetic target through the scenario fixture.",
  "runStages": [
    {
      "id": "inspect-defective",
      "name": "Verify defective target",
      "phase": "Verification",
      "workingDirectory": "repository",
      "arguments": ["verify", "{{project}}", "--checkpoint", "{{workspace}}/checkpoint", "--json"],
      "expectedOutcome": "expected-defective-verification",
      "expectedExitCode": 1,
      "timeoutSeconds": 3600,
      "resultFile": "results/defective.json",
      "assertions": [
        { "pointer": "/findingCount", "valueType": "number", "expected": "{{acceptance.expectedFindings}}" },
        { "pointer": "/qualification", "valueType": "string", "expected": "NOT QUALIFIED" }
      ],
      "environment": {}
    }
  ],
  "resetStages": [],
  "reportPaths": ["{{workspace}}/reports/defective.json", "{{workspace}}/reports/corrected.json"],
  "resetScope": "scenario-owned-only"
}
```

A stage uses an argument array and never invokes a shell. The default `proofshift-cli` runner invokes ProofShift; `dotnet-test` builds/runs a repository test project; `dotnet-test-module` runs a prebuilt MTP test assembly with a bounded `-class` filter for binary-only packages. Test runners capture `outputFile` separately from a structured `resultFile`. The stage's working directory is `repository`, `scenario`, or `workspace`. When a project is declared, it is copied into the marker-owned workspace (excluding `.proofshift`, `.git`, `bin`, and `obj`); symlinks/reparse points are rejected. The `{{project}}` token resolves to this copy, keeping CLI checkpoints, Evidence, and Recovery under the reset boundary without modifying source configuration. Paths are checked for escape. Result assertions use JSON Pointer and typed string/number/boolean comparisons. `resultBindings` extracts typed JSON values for later stages as `{{demo.<name>}}` tokens; unresolved bindings fail the dependent stage. Placeholder tokens also include `{{repository}}`, `{{scenarioDirectory}}`, `{{workspace}}`, `{{project}}`, and accepted-index values such as `{{acceptance.expectedFindings}}`.

The test runner's console log is not parsed as result JSON. A failed stage retains its diagnostic and workspace for inspection; unresolved report paths that depend on outputs from that failed stage are omitted from the run result.

`expectedOutcome` is one of `success`, `expected-defective-verification`, or `expected-unsafe-recovery`. A nonzero command exit is considered an expected state only when explicitly declared as one of the latter outcomes, structured output is persisted, and result assertions pass. A process/startup error or mismatched exit is a demo failure. Stage manifests cannot override reserved scenario identity/workspace environment variables.

## Preflight and Cleanup

`demo preflight <id>` checks the required .NET SDK, Docker when requested, free disk space, required ports, the project file, accepted scenario-index format/reference, and declared fixture paths including reparse-point safety. It emits stable diagnostic codes and has a nonzero exit for blocked readiness.

`demo run <id>` is non-interactive. It preflights before creating `.proofshift/demos/<id>`, writes an ownership marker binding the scenario ID and full workspace path, then materializes the referenced project under that workspace. Existing project `.proofshift`, `.git`, `bin`, and `obj` directories are not copied; symbolic links/reparse points are rejected. CLI checkpoints, Evidence, and Recovery generated relative to the isolated config remain inside the reset boundary. Existing workspaces are not overwritten; the operator is told to inspect and reset them first. Human-readable output prints declared result bindings as concise labeled facts and lists report paths; it does not print the underlying finding corpus.

`demo reset <id>` runs only the manifest's scenario cleanup stages and removes only `.proofshift/demos/<id>` after checking the matching ownership marker and refusing symbolic links/reparse points. The accepted 0.10F index is required only if cleanup stages use its expectation tokens; workspace-only cleanup can proceed without the index. It will not recursively remove a broader demo, repository, home, or user directory. A missing marker, mismatched path, or uncertain ownership fails closed.

Scenario display names, manifest paths, stage commands, report paths, workspace names, and runtime measurements never enter semantic Verification identity. Fixtures remain synthetic, and normal connector/secret redaction rules apply.