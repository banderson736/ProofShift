# Customer Evaluation Runbook

This runbook describes a controlled ProofShift evaluation. External
Verification is read-only; any shadow Projection writes only to an approved
isolated destination. Keep customer data and resolved credentials inside the
customer's environment.
Use the packaged synthetic BYOM scenario to validate the shadow-projection,
generic-reconciliation, and external read-only Verification workflows before
connecting any customer source.
Check the [supported source and connector compatibility table](SOURCE_CONNECTOR_COMPATIBILITY.md)
before selecting endpoints or declaring evaluation capabilities.

## 1. Agree on the evaluation boundary

Record the project owner, source/target owners, read-only access approver,
evaluation purpose, time window, approved source selectors, and explicit
scope type. Define whether the selector represents the full declared source,
a customer-prepared subset, or a supported bounded relational predicate.
Do not describe a subset as the complete migration.

## 2. Prepare and discover

Follow [Customer Data Preparation](CUSTOMER_DATA_PREPARATION.md). Use ordinary
discovery and review commands:

```text
proofshift discover <config> --system source --endpoint <endpoint> --output <source-discovery>
proofshift discover <config> --system target --endpoint <endpoint> --output <target-discovery>
proofshift discovery diff <source-discovery> <target-discovery>
proofshift scaffold --source-discovery <source-discovery> --target-discovery <target-discovery> --output <draft>
proofshift mapping import --csv <reviewed-mappings.csv> --source-discovery <source-discovery> --target-discovery <target-discovery> --output <reviewed-project> --target-role target --verification-baseline generic
```

For a full declared-source or customer-prepared-subset authoring scope, pass
both `--evaluation-scope-id <stable-id>` and
`--evaluation-scope-type <full-declared-source|customer-prepared-subset>` to
the authoring command. Bounded relational predicates are authored on the
source selector in graph configuration and must be supported by that
connector. An unsupported predicate must fail; do not remove it merely to
make a run succeed.

Review the graph, mapping roles/pipelines, dispositions, and rules with the
customer's data owner. Suggestions are not approvals. Validate strictly
before capturing a checkpoint:

```text
proofshift validate <reviewed-project>/proofshift.yaml --strict
proofshift plan <reviewed-project>/proofshift.yaml
proofshift snapshot <reviewed-project>/proofshift.yaml --json
```

## 3. Observe an externally executed migration

Capture only the approved scope. Record the checkpoint ID, graph/configuration
identity, scope IDs/types/fingerprints, endpoint counts, and consistency
guarantees. A complete checkpoint is required; incomplete checkpoints cannot
be replayed.

The external migration is separately authorized and operated by its owner.
ProofShift external Verification only reads target state. Supply target
observer credentials with the minimum required read privileges:

```text
proofshift verify-external <reviewed-project>/proofshift.yaml --checkpoint <checkpoint-id> --observation <stable-label> --json
proofshift evidence <reviewed-project>/proofshift.yaml --run <verification-run-id> --json
proofshift report <reviewed-project>/proofshift.yaml --verification-run <verification-run-id> --json
```

Correct the external target through its own authorized process, then capture a
new observation and compare persisted reports. Do not treat ProofShift as an
execution tool.

## 4. Evaluate a customer-controlled shadow projection

When shadow Projection is the approved activity, configure a `shadow-target`
system and a connector-supported isolated destination. Capture a complete
checkpoint, then use the same checkpoint for `project` and `verify`:

```text
proofshift snapshot <shadow-project>/proofshift.yaml --json
proofshift project <shadow-project>/proofshift.yaml --checkpoint <checkpoint-id> --json
proofshift verify <shadow-project>/proofshift.yaml --checkpoint <checkpoint-id> --projection <projection-run-id> --json
proofshift evidence <shadow-project>/proofshift.yaml --run <verification-run-id> --json
```

Shadow Projection writes only to the configured isolated shadow destination.
This is not the external target observer flow, and it does not authorize
production migration or rollback. The synthetic package exercises a
qualified run and an intentionally defective run with a scope-bound,
non-waiving expected-exception annotation.

## 5. Interpret and retain evidence

Report source accounting, target lineage, rule outcomes, scope identity and
scope counts together. State that completeness is limited to the declared
scope and configured selectors. Retain the report/evidence fingerprints and
the exact reviewed configuration and mapping files under the customer's
retention policy.

External Verification states **Recovery NOT ASSESSED** and **Qualification
NOT AVAILABLE**. It does not establish vendor execution lineage, legal or
regulatory compliance, production readiness, or completeness outside the
declared scope.

## 6. Cleanup and retention

Plan cleanup before the first run. Put only generated evaluation outputs under
one new, dedicated evaluation directory (for example
`$HOME/ProofShift-Evaluation/case-20261028`). Keep source files,
customer-owned data, and customer source/target database locations outside
that directory. Never run recursive deletion against a source endpoint,
external target, customer data directory, or shared Docker resources.

Use the same dedicated root for generated discovery directories, scaffold,
reviewed project (including its `.proofshift` workspace), and any BYOM
`WorkRoot`. For example, name those children `source-discovery`,
`target-discovery`, `scaffold`, `reviewed-project`, and `byom-work`. If the
package archive or extracted package should be removed with the evaluation,
place them under this root too; otherwise keep them and remove only their
separately reviewed exact paths. From the extracted package root, the included
synthetic runner can be started as follows; select a new case and unused
`WorkRoot` for each run:

```powershell
$packageRoot = (Get-Location).Path
if (-not (Test-Path -LiteralPath (Join-Path $packageRoot 'package-manifest.json') -PathType Leaf)) {
    throw 'Run this command from the extracted package root.'
}
$evaluationBase = Join-Path $HOME 'ProofShift-Evaluation'
$caseId = 'case-20261028'
if ($caseId -notmatch '^case-[0-9]{8}$') { throw 'Enter a new dedicated case directory name.' }
$evaluationRoot = Join-Path $evaluationBase $caseId
$workRoot = Join-Path $evaluationRoot 'byom-work'
if (Test-Path -LiteralPath $workRoot) { throw 'Choose a new, unused BYOM work directory.' }
pwsh -File (Join-Path $packageRoot 'scripts/run-byom-evaluation.ps1') -PackageRoot $packageRoot -WorkRoot $workRoot
```

### What is removed automatically

- A normal CLI operation manages its own short-lived working files; a process
  interruption can leave partial temporary files for inspection.
- The packaged BYOM runner creates uniquely named synthetic SQL Server and
  PostgreSQL containers and removes those two containers with their attached
  volumes in its `finally` cleanup, including on failure. It also clears the
  fixture connection variables in the child PowerShell process. It does **not**
  remove the generated `-WorkRoot` directory or downloaded Docker images. If
  the process or Docker daemon is forcibly terminated, inspect for orphaned
  fixture containers as described below.
- A packaged demo's workspace remains until you explicitly reset it. From the
  extracted package root, reset only the synthetic demo you ran:

  ```powershell
  dotnet .\app\ProofShift.Cli.dll demo reset pension
  ```

  Replace `pension` with the exact demo ID. The reset command removes only its
  marker-owned demo workspace after its reset stages succeed.

Customer project checkpoints, persisted Verification Evidence, generated
discovery/mapping/project files, the BYOM runner's work directory, package
archives/extractions, and shadow-target outputs are retained unless you
explicitly remove or reset their exact paths.

### Optional evidence retention

Before deleting generated outputs, follow the customer's evidence-retention
and data-classification policy. Copy only the artifacts that policy permits to
an approved restricted location. For example, from PowerShell after setting
the exact generated project and approved retention paths:

```powershell
$projectRoot = Join-Path $HOME 'ProofShift-Evaluation/case-20261028/reviewed-project'
$retentionRoot = 'D:\Approved-Records\case-20261028'
New-Item -ItemType Directory -Path $retentionRoot -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $projectRoot 'proofshift.yaml') -Destination $retentionRoot
Copy-Item -LiteralPath (Join-Path $projectRoot '.proofshift\verifications') -Destination $retentionRoot -Recurse
```

Also retain the reviewed mappings and discovery artifacts if required. A
checkpoint contains captured source artifacts and can include customer data;
copy `.proofshift\checkpoints` only when explicitly approved for that data
classification. Verify the retained files and access controls before removal.
Do not copy resolved credentials, secret values, or raw data into an
unapproved location.

### Explicit cleanup of generated local output

After any required retention and verification, delete only the dedicated
evaluation directory that you created. This example uses a fixed case path;
change it to the exact path used for this evaluation and review the listed
contents first:

```powershell
$evaluationBase = Join-Path $HOME 'ProofShift-Evaluation'
$caseId = 'case-20261028'
if ($caseId -notmatch '^case-[0-9]{8}$') { throw 'Enter the exact reviewed case directory name.' }
$evaluationRoot = Join-Path $evaluationBase $caseId
Get-ChildItem -LiteralPath $evaluationRoot -Force
$confirmation = Read-Host "Type $caseId to delete this generated evaluation directory"
if ($confirmation -cne $caseId) { throw 'Cleanup cancelled; the evaluation directory was retained.' }
Remove-Item -LiteralPath $evaluationRoot -Recurse -Force
```

This removes the generated project/workspace, `.proofshift` checkpoint,
Verification and temporary stores, BYOM `WorkRoot` scratch, local package
extraction, and any package archive only if you deliberately placed those
items under this exact evaluation root. It does not remove outputs outside
that root; delete other generated paths only by their exact reviewed path.
Keep an obtained package archive or extraction if it is needed for audit or
reproducibility.

### Shadow destination cleanup

Shadow Projection outputs are not automatically deleted. For PostgreSQL, the
schema is `proofshift_shadow_<32-character-run-id>`. First confirm the exact
run ID from the Projection result and list that exact schema using the
customer's approved PostgreSQL admin authentication:

```text
psql -h <shadow-host> -d <dedicated-shadow-database> -U <shadow-admin> -v ON_ERROR_STOP=1 -c "SELECT nspname FROM pg_namespace WHERE nspname = 'proofshift_shadow_<32-lowercase-hex-run-id>';"
psql -h <shadow-host> -d <dedicated-shadow-database> -U <shadow-admin> -v ON_ERROR_STOP=1 -c "DROP SCHEMA \"proofshift_shadow_<32-lowercase-hex-run-id>\" CASCADE;"
```

Replace the placeholder with the exact verified run ID; execute the `DROP`
only after confirming that the schema is the disposable shadow run and that
required evidence is retained. Do not use a wildcard or drop any source or
external-target schema. The packaged synthetic fixture's PostgreSQL
containers are removed automatically, so this manual database command is for
separately provisioned shadow destinations.

For filesystem shadow Projection, each run is a 32-character run-ID directory
under the configured shadow root. Inspect the root and run ID first, then
remove only that exact child directory:

```powershell
$shadowRoot = Join-Path $HOME 'ProofShift-Shadow/byom-output'
$runId = '<32-lowercase-hex-run-id>'
if ($runId -notmatch '^[0-9a-f]{32}$') { throw 'Enter the exact run ID from the Projection result.' }
$runDirectory = Join-Path $shadowRoot $runId
Get-ChildItem -LiteralPath $shadowRoot -Force
if ((Test-Path -LiteralPath $runDirectory -PathType Container) -and
    (Split-Path -Leaf $runDirectory) -eq $runId) {
    $confirmation = Read-Host "Type $runId to delete this shadow run directory"
    if ($confirmation -cne $runId) { throw 'Cleanup cancelled; the shadow run directory was retained.' }
    Remove-Item -LiteralPath $runDirectory -Recurse -Force
}
```

Confirm that `$shadowRoot` is a dedicated shadow destination before running
the command. Never delete the configured source root.

### Orphaned synthetic Docker fixture containers

The packaged BYOM runner normally removes its own fixtures. If it was
forcibly interrupted, list candidate containers and inspect their exact names
before removing any. The generated names start with
`proofshift-byom-source-` or `proofshift-byom-target-`:

```powershell
docker ps -a --filter 'name=^/proofshift-byom-source-' --format '{{.ID}} {{.Names}}'
docker ps -a --filter 'name=^/proofshift-byom-target-' --format '{{.ID}} {{.Names}}'
$fixtureContainerId = '<verified-fixture-container-id>'
if ($fixtureContainerId -notmatch '^[0-9a-f]{12,64}$') { throw 'Replace the placeholder with the verified container ID.' }
docker rm --force --volumes $fixtureContainerId
```

Run the last command once per verified fixture container ID only. Do not use
`docker system prune`, `docker volume prune`, or remove shared images. Pulled
images remain available to Docker and are shared runtime assets, not
evaluation output.
