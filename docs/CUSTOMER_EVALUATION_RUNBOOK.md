# Customer Evaluation Runbook

This runbook describes a controlled ProofShift evaluation. External
Verification is read-only; any shadow Projection writes only to an approved
isolated destination. Keep customer data and resolved credentials inside the
customer's environment.
Use the packaged synthetic BYOM scenario to validate the shadow-projection,
generic-reconciliation, and external read-only Verification workflows before
connecting any customer source.

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
