# Reconciliation Rule Guide

Reconciliation compares graph-derived expected target state with independent
target observations. Generic rules are domain-neutral; they do not replace
customer or regulatory acceptance criteria.
The [source and connector compatibility table](SOURCE_CONNECTOR_COMPATIBILITY.md)
describes the registered read-only observation and shadow destination
capabilities.

## Generic rule families

- `source-artifact-accounting`: every in-scope checkpoint artifact has an
  explicit graph disposition.
- `target-lineage`: every observed in-scope target artifact has graph-derived
  provenance.
- `target-presence` and `unexpected-target`: expected targets are present and
  unexplained targets are absent.
- `attribute-comparison`: configured mapped attributes match expected
  transformed values; raw values remain redacted in normal findings.
- `entity-uniqueness`: configured target identities are unique.
- `aggregate-total-comparison`: exact numeric totals match between
  graph-derived expected records and observed records.
- `effective-dated-interval`: configured date intervals satisfy overlap and
  optional continuity rules.

## Aggregate total configuration

```yaml
rules:
  account-total:
    type: aggregate-total-comparison
    version: "1"
    severity: critical
    targetNode: target-account
    semanticType: Customer.Account
    measureField: balance
```

Totals use exact decimal/integer semantics. Null, non-numeric, and overflow
inputs fail the rule rather than being ignored. A total match is not proof
that each row, relationship, or business invariant is correct; combine it
with identity, lineage, presence, and attribute checks.

## Expected exceptions

Expected exceptions are **annotations, not waivers**. Declare an exact target
finding match at the root of the version-2 rule document, alongside `rules`:

```yaml
version: 2
rules:
  attributes:
    type: attribute-comparison
    version: "1"
    severity: error
    attribute: status

expectedExceptions:
  - id: approved-legacy-status
    ruleId: attributes
    findingCode: AttributeMismatch
    targetNode: target-records
    artifactIdSha256: 0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef
    evaluationScopeId: customer-cohort
    owner: data-owner
    reason: Approved legacy status difference retained for remediation.
```

`artifactIdSha256` is the lowercase SHA-256 of the UTF-8 target artifact ID.
The exact configured rule ID, finding code, target graph node, artifact-ID
hash, and checkpoint evaluation-scope ID must match. The scope must exist in
the bound source checkpoint, and a source artifact reference on the matching
finding must belong to a source node captured under that scope. The reason
and owner are included in evidence; keep them free of secrets and sensitive
business data.

Matched exceptions add a traceable annotation record linked to the original
finding. The original finding remains failed, is not removed from accounting,
and still contributes to the Verification outcome. An unmatched annotation
is recorded as not applicable; it never creates a pass. Duplicate IDs or
match keys and malformed hashes fail closed. Do not claim an exception has
made a failing evaluation pass.

## Scope and reporting

Every reported count and comparison is bounded by the configured source
selectors and declared evaluation scopes. State the scope ID/type and
artifact count alongside any result. No finding or aggregate proves
completeness outside those boundaries. External Verification remains
read-only and reports Recovery as not assessed.
