# Security, Data Handling, and IP Boundaries

## Development-data rule

Never use real customer, employer, or government production data in the public/development repository.

PS-0 must use deterministic synthetic data only.

## Secret handling

- No passwords/tokens/connection strings in committed configuration.
- Use environment variables or future secret-provider abstractions.
- Redact resolved secrets from logs, evidence, reports, hashes, diagnostics, and exceptions.
- Do not serialize secrets into migration plans or run records.

## Sensitive evidence

Evidence may eventually contain regulated/sensitive values. The architecture should allow:

- references instead of payload duplication;
- value hashing;
- redaction;
- configurable evidence retention;
- encryption-at-rest/in-transit in enterprise deployment;
- locally hosted/customer-controlled storage.

Do not overbuild these enterprise features during PS-0, but avoid designs that make them impossible.

## IP separation

The product should be independently developed from public concepts, public standards, public procurement requirements, and original design work.

Do not use:

- proprietary employer/client code;
- non-public architecture documents;
- confidential schemas;
- customer secrets;
- internal implementation details that are not independently/publicly derived.

The initial pension vertical intentionally provides separation from current healthcare/FHIR employment work.

A future Healthcare/FHIR Pack should be based on public HL7/CMS/industry specifications and independently authored implementation.
