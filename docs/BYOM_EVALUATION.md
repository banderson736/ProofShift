# Bring-Your-Own-Migration Evaluation

The packaged BYOM evaluation is an ordinary, synthetic project authored with
ProofShift's normal configuration and discovery tools. It is not a canned
domain demo, domain pack, migration executor, or source-code example that must
be built from the repository.

## Assurance boundary

The workflow separates five activities:

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

## Evaluator sequence

Run the sample from an extracted evaluation package. Provision the synthetic
SQL Server source and PostgreSQL target using the sample's setup assets and
separate credentials. Give ProofShift the source connection only for discovery
and checkpoint capture, and the read-only target connection for observation.
Keep the external fixture's migration credential separate.

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
