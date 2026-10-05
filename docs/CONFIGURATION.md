# ProofShift Configuration Contract

Configuration must be declarative, versioned, hashable, and suitable for source control.

Avoid one monolithic YAML file for real projects. Use a root manifest plus referenced files.

## Proposed layout

```text
proofshift.yaml
systems/
  source.yaml
  target.yaml
migration/
  graph.yaml
rules/
  pension.yaml
recovery/
  policy.yaml
```

## Root configuration

```yaml
proofshift: 1

project:
  id: pension-modernization
  name: Public Pension Modernization

pack:
  id: proofshift.pension
  version: "0.9.0"

systems:
  source:
    file: systems/source.yaml
  target:
    file: systems/target.yaml

migration:
  graph: migration/graph.yaml

verification:
  rules: rules/pension.yaml

recovery:
  policy: recovery/policy.yaml
```

## Source system

```yaml
id: legacy-pension
name: Legacy Pension System
role: source

storage:
  member-database:
    connector: sqlserver
    connection:
      secret: PROOFSHIFT_LEGACY_DB
    checkpoint:
      consistency: observed

  member-documents:
    connector: files
    root:
      env: PROOFSHIFT_LEGACY_DOCUMENTS

  supplemental-members:
    connector: csv
    root:
      env: PROOFSHIFT_LEGACY_EXPORTS
    delimiter: ";"
```

No secret values should be stored directly in committed configuration.

### Checkpoint consistency

Relational checkpoint policy is configured per source endpoint under `checkpoint`. The default is `observed`; ProofShift does not silently start a transaction or choose a stronger locking strategy.

SQL Server may explicitly request transaction consistency with a provider strategy:

```yaml
checkpoint:
  consistency: transaction-consistent
  isolation: snapshot
```

Supported SQL Server isolation values are `snapshot`, `serializable`, and `read-committed`. `snapshot` and `serializable` can satisfy the transaction-consistent request. `read-committed` is classified as `observed`; it is rejected for a transaction-consistent request unless `allowDowngrade: true` is also configured, in which case the checkpoint records the downgrade. Snapshot database options are never changed automatically. If the requested provider mode cannot be started, capture fails rather than silently changing isolation.

PostgreSQL checkpoints retain provider repeatable-read semantics. Endpoint transaction consistency is not a claim of cross-system atomicity. Checkpoint manifests record the requested strategy, effective strategy, guarantee, and any downgrade without recording connection details.

## Target system

```yaml
id: new-pension
name: New Pension Administration System
role: shadow-target

storage:
  pension-database:
    connector: postgres
    connection:
      secret: PROOFSHIFT_TARGET_DB

  pension-documents:
    connector: files
    root:
      env: PROOFSHIFT_TARGET_DOCUMENTS
```

## Graph example

```yaml
version: 1

nodes:
  legacy-member:
    type: source
    system: legacy-pension
    storage: member-database
    semanticType: Pension.Member
    selector:
      kind: table
      properties:
        name: dbo.MEMBER
      identity:
        - MEMBER_ID

  participant:
    type: target
    system: new-pension
    storage: pension-database
    semanticType: Pension.Member
    selector:
      kind: table
      properties:
        name: participant
      identity:
        - id

edges:
  member-migration:
    from:
      - legacy-member
    to:
      - participant

    operation:
      type: transform
      version: "1"
      fields:
        legacyId:
          source: MEMBER_ID

        firstName:
          source: FIRST_NM
          pipeline:
            - type: trim
            - type: normalize-string

        lastName:
          source: LAST_NM
          pipeline:
            - type: trim
            - type: normalize-string

        status:
          source: MEMBER_STATUS
          pipeline:
            - type: code-map
              version: "1"
              values:
                A: ACTIVE
                R: RETIRED
                D: DECEASED

    recovery:
      mode: restore
      requiresSnapshot: true
```

Graph `version` is independent of the root `proofshift` configuration version. Edges use `from` and `to` sequences; `exclude` may have an empty `to` sequence. A graph is compiled and canonically fingerprinted by PS-0.3, without loading a connector or domain pack.

## Historical mapping

```yaml
nodes:
  legacy-employment:
    system: legacy-pension
    storage: member-database
    type: source
    semanticType: Pension.Employment
    source:
      table: dbo.EMPLOYMENT_HISTORY

  target-employment:
    system: new-pension
    storage: pension-database
    type: target
    semanticType: Pension.Employment
    target:
      table: employment_period

edges:
  employment-history:
    from:
      - legacy-employment
    to:
      - target-employment
    operation:
      type: transform
      temporal:
        source:
          from: START_DATE
          to: END_DATE
        target:
          from: effective_from
          to: effective_to
```

## Relationship mapping

```yaml
beneficiary-member:
  type: relationship
  relationship:
    source: MEMBER_ID
    target: participant_id
  semantic:
    relation: HAS_BENEFICIARY
  verification:
    requireTargetRelationship: true
```

## Rules

```yaml
rules:
  source-accounting:
    type: source-artifact-accounting
    severity: critical
    require:
      unaccounted: 0

  member-uniqueness:
    type: entity-uniqueness
    semanticType: Pension.Member
    severity: critical

  contribution-total:
    type: aggregate-reconciliation
    semanticType: Pension.Contribution
    groupBy:
      - member
      - fiscalYear
    source:
      aggregate: sum
      field: amount
    target:
      aggregate: sum
      field: amount
    comparison:
      type: currency
      tolerance: 0.01
    severity: critical
```

## Timeline rule

```yaml
employment-continuity:
  type: timeline-equivalence
  semanticType: Pension.Employment
  groupBy:
    - member
  source:
    start: effectiveFrom
    end: effectiveTo
  target:
    start: effectiveFrom
    end: effectiveTo
  options:
    allowAdjacentPeriods: true
    allowOpenEndedFinalPeriod: true
  severity: high
```

  Pension financial rule definitions accept explicit decimal `tolerance` options; contribution and payment rules default to `0.01`. The option is included in the rule-set fingerprint. Pension timeline, entity-key, relationship, code-field, and document-hash assumptions are configured per rule; the pack does not infer plan/jurisdiction rules from physical schema names.

## Recovery policy

```yaml
requireRecoveryForDestructiveOperations: true

allowIrreversible: {default: false}

requireValidatedRestore: true
requireRecoveryRehearsal: true
maximumIrreversibleArtifacts: 0

requiredSnapshots:
  - system: legacy-pension
    storage: member-database
  - system: legacy-pension
    storage: member-documents

approval:
  irreversibleTransformations:
    required: true
```

`requiredSnapshots` identifies source checkpoint endpoints. A PS-0.6 source checkpoint is never treated as a target restore capability. PS-0.8 separately captures and integrity-validates isolated shadow target recovery checkpoints through the registered target connector. Irreversible work is denied by default; policy allowance and an edge justification are required, and the artifact limit still applies. The effective policy is independently versioned and fingerprinted.

## Configuration implementation principles

- Configuration is external representation, not the domain model itself.
- Parse into strongly typed intermediate configuration objects, validate, then construct domain objects.
- Resolve references deterministically.
- Produce canonical serialization for hashing.
- Environment/secret substitution must occur without accidentally storing resolved secret values in hashes/logs/evidence.
- Diagnostics must provide file/path/location and actionable validation messages.
- Avoid premature configuration features not needed for the active milestone.

## PS-0.4 Selector Access

The connector runtime resolves endpoint `env:` and `secret:` references again into a redacting runtime-only context. Relational connectors require a `connection` or `connectionString` setting containing a connection string reference. Filesystem and CSV connectors require a `root` setting; CSV may also set an endpoint `delimiter` (comma is the default). The resolved values are never copied into graph/domain data, inspection reports, hashes, or CLI output.

Relational table selectors accept `name: schema.table`, or separate `schema` and `table` properties, plus optional comma-separated `columns`. Identity fields are explicit or discovered from a primary key. `file-pattern` selectors use `pattern`; CSV selectors use relative `path`, optional `delimiter`, and required identity fields. Paths remain inside their endpoint root. These values describe access only: inspection is not a snapshot and does not change the source or target.

## PS-0.2 Implementation Boundary

Version 1 is parsed into immutable configuration DTOs, validated, and then used to construct domain system and storage-endpoint definitions. The project ID remains a validated string in the root DTO; `ProofShift.Domain.ProjectId` is a GUID and there is not yet an approved stable conversion rule. Migration graph, verification-rule, and recovery-policy files are loaded as generic immutable YAML document trees. PS-0.3 compiles the graph tree into separate graph DTOs and then domain graph objects.

Canonical form `proofshift-config-canonical-v1` sorts mapping entries ordinally, preserves sequence order, ignores comments and scalar presentation style, and encodes scalar categories explicitly. It includes normalized project-relative file paths and every referenced file. The lowercase SHA-256 fingerprint is computed over this canonical representation. Environment and secret references contribute their names, while resolved values are checked for availability but never retained in normalized configuration or included in the fingerprint.

## PS-0.4 Runtime Boundary

At runtime the CLI/Engine resolves endpoint reference identities into callback-only redacting settings and passes those values to the selected source connector. Concrete connectors receive a `ConnectorContext`, not the original loaded configuration. The graph continues to contain symbolic selectors; SQL Server/PostgreSQL interpret `table`, filesystem interprets `file-pattern`, and CSV interprets `csv`. Connector inspection is read-only and does not imply a source snapshot.

## PS-0.5 Shadow Destination Boundary

Every graph target/archive destination used by `proofshift project` must resolve to a system with `role: shadow-target`. `source`, `target`, and `archive` roles are rejected before any target connector is prepared. The PostgreSQL endpoint must name a pre-existing template table through a `table` selector; target `columns` is an optional comma-separated list and all field identifiers are validated/quoted. PostgreSQL writes go only to the run-generated schema. Filesystem target endpoints configure an existing `root`; each run creates its own GUID directory below it, and files are placed beneath a graph-node subdirectory. File selectors set `pathField` (default: first identity field) to a projected relative-path string. Paths are kept inside that run directory.

Projection also rejects a PostgreSQL source and shadow destination that resolve to the same connection string, and rejects a filesystem shadow root that overlaps (equals, contains, or is contained by) any filesystem/CSV source root. This avoids accidental writes into a live source store even when roles were misconfigured.

The runnable synthetic configuration and template SQL are in `scenarios/pension-modernization/ps05/`. Set `PS05_SQL_CONNECTION`, `PS05_POSTGRES_CONNECTION`, `PS05_SOURCE_FILES`, `PS05_SOURCE_CSV`, and `PS05_SHADOW_FILES` in the process environment. The source seed includes an unmapped status `X`, so the first projection intentionally fails closed after journaling/materializing preceding records. Correct the source code to a configured value and rerun; each run remains isolated. The configuration contains only secret/environment references, never resolved values.
