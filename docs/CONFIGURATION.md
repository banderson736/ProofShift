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
  version: "0.1"

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

  member-documents:
    connector: files
    root:
      env: PROOFSHIFT_LEGACY_DOCUMENTS
```

No secret values should be stored directly in committed configuration.

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
    system: legacy-pension
    storage: member-database
    type: source
    semanticType: Pension.Member
    source:
      table: dbo.MEMBER
      key:
        - MEMBER_ID

  participant:
    system: new-pension
    storage: pension-database
    type: target
    semanticType: Pension.Member
    target:
      table: participant
      key:
        - id

edges:
  member-migration:
    from:
      - legacy-member
    to:
      - participant

    operation:
      type: transform
      fields:
        legacyId:
          source: MEMBER_ID

        firstName:
          source: FIRST_NM
          pipeline:
            - trim
            - normalize-string

        lastName:
          source: LAST_NM
          pipeline:
            - trim
            - normalize-string

        status:
          source: MEMBER_STATUS
          pipeline:
            - type: code-map
              values:
                A: ACTIVE
                R: RETIRED
                D: DECEASED

    recovery:
      mode: restore
      requiresSnapshot: true
```

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

## Recovery policy

```yaml
requireRecoveryForDestructiveOperations: true

allowIrreversible:
  default: false

requiredSnapshots:
  - system: legacy-pension
    storage: member-database
  - system: legacy-pension
    storage: member-documents

approval:
  irreversibleTransformations:
    required: true
```

## Configuration implementation principles

- Configuration is external representation, not the domain model itself.
- Parse into strongly typed intermediate configuration objects, validate, then construct domain objects.
- Resolve references deterministically.
- Produce canonical serialization for hashing.
- Environment/secret substitution must occur without accidentally storing resolved secret values in hashes/logs/evidence.
- Diagnostics must provide file/path/location and actionable validation messages.
- Avoid premature configuration features not needed for the active milestone.
