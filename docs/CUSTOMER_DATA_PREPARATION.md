# Customer Data Preparation

## Data handling boundary

ProofShift is evaluated in a customer-controlled environment. Do not send
customer data, extracts, schemas with confidential names, or resolved secrets
to repository issues, pull requests, logs, or public support channels. The
repository and packaged acceptance fixtures contain synthetic data only.

Resolve connection credentials through customer-managed secret references or
environment variables. Do not place credentials in configuration, mapping
CSV, shell history, screenshots, reports, fingerprints, or evidence.

## Prepare a bounded dataset

1. Select the smallest representative dataset that covers the agreed
   migration cases, including historical records and relevant relationships.
2. Obtain data-owner and security approval for every source and target
   endpoint and for the declared time window.
3. Prefer deterministic, customer-prepared subsets with stable identities.
   Record the extraction method and date outside ProofShift's evidence if
   those facts are sensitive.
4. If the source itself must be filtered, use only a supported declarative
   relational predicate. No arbitrary SQL is accepted as an evaluation
   predicate. Verify that the connector advertises the required operators.
5. Keep source and target roles distinct. Grant source read-only access for
   discovery/checkpoint capture and target read-only observer access for
   external Verification.
6. Confirm the subset's declared meaning: `full-declared-source`,
   `customer-prepared-subset`, or `bounded-evaluation`. Do not call a limited
   subset the full population.
7. Review identifiers and notes in discovery artifacts and mapping files for
   sensitive information before retaining or sharing them.

## Record provenance and limitations

Keep the approved selector, stable scope ID/type, extraction/review owner,
configuration hash, graph hash, checkpoint ID, and Verification run IDs
associated with the evaluation. Scope fingerprints are intended for
deterministic binding; they do not prove that the declared source is complete
or that the source owner supplied every expected record.

Use synthetic fixtures to diagnose product behavior. Never substitute
production data into a repository test or package scenario.
