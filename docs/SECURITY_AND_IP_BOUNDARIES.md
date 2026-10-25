# Security, Data Handling, and IP Boundaries

## Repository and product status

The ProofShift source repository is publicly visible but proprietary. Public
visibility is not an open-source license or permission to use or redistribute
the source. See the repository [LICENSE](../LICENSE). The current product
identity is `ProofShift 0.1.0-preview.1`; it has not been publicly released,
tagged, or published. There is no production-hosted ProofShift service.

Never post secrets, vulnerability details, or sensitive customer data in
public issues or pull requests. GitHub private vulnerability reporting was
enabled and verified on October 25, 2026. Use GitHub's private vulnerability
reporting feature to submit vulnerability details. No security email address
is published.

## Development-data rule

Never use customer, employer, government, or other production data in the
repository. Only deterministic synthetic fixtures are allowed.

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
- real PII or PHI;
- internal implementation details that are not independently/publicly derived.

The initial pension vertical intentionally provides separation from current healthcare/FHIR employment work.

A future Healthcare/FHIR Pack should be based on public HL7/CMS/industry specifications and independently authored implementation.
