# Justice Case-Management Pack

## Scope

The Justice pack demonstrates representative case-management migration assurance over configured graph transformations and invariants. It is not legal validation and does not establish jurisdictional or court-rule compliance, sentencing legality, charge validity, filing sufficiency, or records-retention compliance. All fixture data and document contents are synthetic.

## Concepts

The frozen concept set is `Justice.Person`, `Justice.Case`, `Justice.CaseParty`, `Justice.Charge`, `Justice.Filing`, `Justice.Hearing`, `Justice.Disposition`, `Justice.Sentence`, and `Justice.Document`.

## Rules

Pack-owned rules:

- `justice-reference-integrity` streams source-reference and lookup records using descriptor-declared target fields and typed lookup ordering. It is global because each independently configured relationship spans graph nodes.
- `justice-attribute-equality` compares a configured target attribute with the checkpoint-and-graph expected value by deterministic identity. The physical fixture uses it for status/code transformation, parent-case matching, and exact filing-date preservation.
- `justice-event-order` compares configured fixture timestamps in order. The scenario checks case-opened time before filing and filing before hearing; these are fixture invariants, not universal procedure.

The scenario also uses generic source accounting, target lineage, target presence, unexpected-target detection, and entity uniqueness. Reference checks use ordered workspace streams rather than a database lookup per artifact. No performance SLA is claimed.

Example configured rule options:

```yaml
- id: party-case-reference
  type: justice-reference-integrity
  version: "1"
  severity: error
  sourceNode: target-case-party
  semanticType: Justice.CaseParty
  referenceField: case_id
  referenceNode: target-case
  referenceSemanticType: Justice.Case
  referenceKeyField: case_id
```

The generic authoring surface is available without a Justice-specific command:
`packs list`, `packs describe justice`, `packs schema justice`, `rules list`,
`rules describe justice-reference-integrity`, `rules schema`, and
`config show scenarios/justice-case-management/proofshift.yaml --effective --json`.
The example configuration binds only the Justice pack and keeps endpoint
credentials as unresolved secret references.

## Physical Scenario

`JusticePhysicalAssuranceTests` seeds legacy-shaped PostgreSQL source tables and filesystem case documents. A separate PostgreSQL instance and filesystem shadow root are used for the target. The corrected external state is created directly from deterministic fixture rows, not Projection Journal output, and is independently read back through the target connectors. Document bytes are checked against their expected SHA-256 values.

The fixture contains 2,000 cases, persons, case parties, charges, filings, hearings, dispositions, and sentences, plus 100 document metadata records and 100 document payloads: 16,200 source artifacts and 16,200 expected target artifacts. Generic shadow projection, Verification, Evidence persistence, Recovery assessment, Recovery rehearsal, and qualification are also exercised. The final focused run, including repeat-observation determinism, completed in about 100 seconds in the local Docker environment; this timing is diagnostic, not an SLA.

## Defect Corpus

The deterministic manifest contains 15 injected defects and freezes 24 expected Verification findings. The exact finding count includes consequences of the missing case: affected case-party, charge, filing, and hearing references, source disposition and target lineage findings. A case party attached to a nonexistent case also fails both reference resolution and expected-parent equality. Those overlaps are asserted by exact per-rule counts.

Categories are missing and duplicate cases, duplicate person identity, broken and wrong case-party relationships, orphan charge, case-status and disposition-code mapping errors, filing and hearing chronology errors, invalid sentence-to-disposition reference, missing document, wrong-case document, and unexpected case target.

The corrected external observation has no Verification failures, 16,200 explainable source dispositions, 16,200 graph-derived target lineage records, and no unexplained target. Corrected shadow Recovery assessment and rehearsal pass, and qualification is `QUALIFIED`. The generated pack-owned JSON report includes Justice concepts and rule types and the non-legal claim boundary.

## Limitations

The fixture is a deterministic assurance probe, not a benchmark or production-scale claim. Timestamp ordering applies only to the explicitly configured synthetic case sequence. Codes are migration mappings, not legal classifications. The pack does not implement authorization or sealed-record access control, jurisdiction policies, legal decision validation, production writes, or rollback execution.
