# Public-Works Work-Order Evaluation

This is a deterministic, synthetic SQL Server-to-PostgreSQL evaluation fixture
for ProofShift's ordinary authoring, shadow-projection, reconciliation, and
read-only external Verification workflows. It is not a built-in demo, domain
pack, migration executor, or customer dataset.

## Fixture

The external-observation graph contains 12 assets, 30 work orders, and 60
work-order history rows: 102 source artifacts total. The separate shadow graph
also includes 12 synthetic asset-budget rows, for 114 source artifacts. The
defective external load seeds four independently reviewable defects:

1. asset `A010` is absent;
2. work order `W0007` has the wrong status;
3. work order `W0012` references the wrong asset;
4. history row `H0008` is absent.

The exact artifact counts and expected generic finding-code totals are frozen
in [expectations.json](expectations.json). The generic Verification baseline
should report two missing targets, two `UnaccountedArtifact` findings, and two
attribute mismatches. The corrected target contains 102 artifacts, has no
accounting or lineage gaps, and should pass with zero Verification failures.
Loading the corrected fixture again should preserve target and Evidence
fingerprints while creating new run identifiers.

## Packaged shadow projection and reconciliation

The `shadow/` project is independent of the external-observation project. It
captures a source checkpoint, projects all 114 synthetic records into an
isolated PostgreSQL shadow schema, and verifies source accounting, target
lineage and presence, mapped budget values, and the exact aggregate budget
total of 12,780.00. Its qualified projection is expected to pass.

The external-observation fixture remains in the `byom` databases and retains
its original three-table, 102-artifact mapping surface. The runner provisions
separate `byom_shadow` databases on both test containers for the four-table
shadow source and target; this keeps the added budget table out of external
discovery and preserves the external Verification fixture.

The packaged `scripts/run-byom-evaluation.ps1` runner then replays the same
checkpoint into a second isolated shadow schema and changes budget `B001`
through the fixture's PostgreSQL administrator connection. This controlled
defect must produce both an attribute mismatch and an aggregate-total failure.
The exact aggregate total must change to 12,781.00. Because the fixture
deliberately changes the physical target after projection, the materialized-
target fingerprint check also reports that the observed target no longer
matches the projection receipt. This is an independent integrity finding, not
a duplicate of either business comparison.
The exact finding receives a scope-bound expected-exception annotation, but
the Verification outcome remains failed; an annotation never waives a
finding. The fixture owner is `synthetic-fixture-owner`, not a customer.

The runner is invoked by package smoke validation and can be run directly
against an extracted package:

```powershell
pwsh -File .\scripts\run-byom-evaluation.ps1 `
  -PackageRoot <extracted-package-root> `
  -WorkRoot <new-work-directory>
```

It provisions disposable local SQL Server and PostgreSQL containers, uses
normal `snapshot`, `project`, `verify`, and `evidence` CLI commands for the
shadow branch, and keeps those artifacts separate from the external
read-only Verification branch below.

## Evaluator sequence

Provision a synthetic SQL Server source and PostgreSQL target, then initialize
them with `database/sqlserver-source.sql` and
`database/postgres-target-schema.sql`. Use the supplied
`discovery/proofshift.yaml` as the discovery configuration and set only the
connection-reference environment variables. The values below are placeholders;
use ephemeral local test credentials and do not commit them:

```text
PROOFSHIFT_SOURCE_CONNECTION=<SQL Server connection string>
PROOFSHIFT_TARGET_CONNECTION=<temporary target discovery connection string>
```

Discover both endpoints, compare them, scaffold a review draft, and import the
reviewed field mappings with the ordinary product commands:

```text
proofshift discover discovery/proofshift.yaml --system source --endpoint records --output <source-discovery>
proofshift discover discovery/proofshift.yaml --system target --endpoint records --output <target-discovery>
proofshift discovery diff <source-discovery> <target-discovery>
proofshift scaffold --source-discovery <source-discovery> --target-discovery <target-discovery> --output <scaffold>
proofshift mapping import --source-discovery <source-discovery> --target-discovery <target-discovery> --csv reviewed-mappings.csv --output <project> --target-role target --verification-baseline generic
proofshift validate <project>/proofshift.yaml --strict
```

For checkpoint capture, keep the source connection available and use the
temporary target discovery credential. After `snapshot` completes, stop or
isolate the source. Replace `PROOFSHIFT_TARGET_CONNECTION` with a SELECT-only
connection, then invoke the external fixture separately using the appropriate
database client:

```text
database/external-load-defective.sql
proofshift verify-external <project>/proofshift.yaml --checkpoint <checkpoint-id> --observation vendor-trial-001 --json
proofshift report <project>/proofshift.yaml --verification-run <verification-run-id> --json
```

Correct the target by running `database/external-load-corrected.sql`
independently of ProofShift, verify with a new observation label, and compare
the persisted runs:

```text
proofshift verify-external <project>/proofshift.yaml --checkpoint <checkpoint-id> --observation vendor-trial-002 --json
proofshift compare <project>/proofshift.yaml --before-verification <defective-run-id> --after-verification <corrected-run-id> --json
```

The external load scripts are separate fixture inputs; ProofShift never
invokes them. ProofShift observes target-role endpoints with read-only
credentials. This workflow does not create Projection or Recovery artifacts
and does not make a Recovery qualification or vendor-execution-lineage claim.
