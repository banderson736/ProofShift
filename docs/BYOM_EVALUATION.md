# Bring-Your-Own-Migration Evaluation

The packaged BYOM evaluation contains two deliberately separate workflows:
a synthetic customer-data shadow projection and generic reconciliation, plus
read-only Verification of a separately populated external target. Both are
ordinary projects using ProofShift's normal configuration and CLI; neither is
a canned domain demo, domain pack, migration executor, or source-code example
that must be built from the repository.

## Assurance boundary

The external-target workflow separates five activities:

1. **ProofShift source checkpoint** — captures immutable source artifacts and
   binds them to the project configuration, graph, selectors, and source
   fingerprint.
2. **External migration execution** — a separately invoked synthetic vendor
   fixture populates the target. ProofShift does not run it.
3. **ProofShift target observation** — `verify-external` reads target-role
   nodes through read-only observer capabilities and replays expected state
   from the checkpoint.
4. **Verification Evidence** — persisted Evidence and the Verification
   ledger record findings, source accounting, target lineage, and fingerprints.
5. **Recovery qualification** — remains a separate shadow dry-run workflow;
   this external Verification does not assess it.

> External Verification does not mutate the target and does not qualify Recovery.

The independent shadow workflow uses `snapshot`, `project`, `verify`, and
`evidence` against a synthetic PostgreSQL shadow target. Its qualified run
checks generic row-level attributes and an exact aggregate total. A second
run seeds one target defect and demonstrates that a matching expected
exception is an auditable annotation only: the attribute and aggregate
failures remain active. Since the defect is injected after projection, the
materialized-target fingerprint check also reports the out-of-band change.
The packaged fixture isolates this workflow in separate `byom_shadow`
databases so its budget table does not alter external-observation discovery or
the original 102-artifact read-only Verification scenario.

## Evaluator sequence

Run the sample from an extracted evaluation package. Provision the synthetic
SQL Server source and PostgreSQL target using the sample's setup assets and
separate credentials. Give ProofShift the source connection only for discovery
and checkpoint capture, and the read-only target connection for observation.
Keep the external fixture's migration credential separate.
For a manually invoked shadow project, set
`PROOFSHIFT_SHADOW_SOURCE_CONNECTION` and
`PROOFSHIFT_SHADOW_TARGET_CONNECTION` to the corresponding `byom_shadow`
database connections; the packaged runner provisions these isolated databases
and sets the references automatically.

The package smoke runner executes both workflows. The shadow project's
configuration and graph live under `scenarios/byom-public-works/shadow/`;
the target connector creates a run-isolated schema, and the fixture then
changes only the disposable defective run. That synthetic shadow-write path
is not the external observer path.

Use ordinary product commands to discover both endpoints, scaffold a review
draft, inspect the suggestions, import reviewed mappings, and choose the
explicit target role and generic Verification baseline:

```text
proofshift discover <config> --output <discovery-directory>
proofshift discovery diff <source-discovery> <target-discovery>
proofshift scaffold --source-discovery <source-discovery> --target-discovery <target-discovery> --output <project>
proofshift mapping import --csv <reviewed-mappings.csv> --source-discovery <source-discovery> --target-discovery <target-discovery> --output <reviewed-project> --target-role target --verification-baseline generic
proofshift validate <reviewed-project>/proofshift.yaml --strict
proofshift explain <reviewed-project>/proofshift.yaml
proofshift config show <reviewed-project>/proofshift.yaml --effective
proofshift snapshot <reviewed-project>/proofshift.yaml --json
```

Review is mandatory. Suggestions are not approvals; strict validation must
reject unresolved required mappings.

For a customer-prepared input subset, the mapping command may additionally
receive `--evaluation-scope-id <stable-id> --evaluation-scope-type
customer-prepared-subset`. Use `full-declared-source` when the declared
selector itself is the intended evaluation boundary. These classifications
are recorded in checkpoint/report metadata; they do not prove completeness
outside the declared boundary. Relational predicate scopes are authored
directly in the source selector configuration.

After checkpoint capture, stop or isolate the source. Run the sample's
defective external load separately, then use the printed checkpoint ID and a
stable observation label:

```text
proofshift verify-external <reviewed-project>/proofshift.yaml --checkpoint <checkpoint-id> --observation vendor-trial-001 --json
proofshift evidence <reviewed-project>/proofshift.yaml --run <verification-run-id> --json
proofshift report <reviewed-project>/proofshift.yaml --verification-run <verification-run-id> --json
```

The external report is read from persisted artifacts; it never re-runs
Verification. Correct the target by invoking the external fixture separately,
then repeat `verify-external` with a new observation label. Compare persisted
runs explicitly:

```text
proofshift report <reviewed-project>/proofshift.yaml --verification-run <corrected-verification-run-id>
proofshift compare <reviewed-project>/proofshift.yaml --before-verification <defective-verification-run-id> --after-verification <corrected-verification-run-id> --json
```

Verification-only reports and comparisons state that Recovery is not assessed
and qualification is unavailable. They do not claim ProofShift executed the
migration or observed vendor execution lineage.

## Limits of the evidence

Generic BYOM evaluation proves only the configured graph, selectors, reviewed
mappings, and Verification rules. It does not prove complete business-domain
correctness, regulatory certification, vendor certification, production
readiness, rollback readiness, data coverage beyond configured selectors, or
vendor execution lineage. Customer-specific business semantics require an
appropriate independently reviewed domain pack and rules.
