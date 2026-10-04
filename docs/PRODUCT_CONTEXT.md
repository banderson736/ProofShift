# ProofShift Product Context

## Why this exists

ProofShift began from a search for a software product that can be built by one founder or a very small team, sold to government contractors, regulated organizations, or niche enterprise buyers, and differentiated by deep engineering rather than consumer-scale distribution.

The strongest product direction identified was not generic SaaS, generic DevOps tooling, or generic database migration. It was **independent migration assurance for high-risk modernization**.

The product should help a customer answer:

> We moved a critical system from representation A to representation B. How can we independently prove that nothing was lost, corrupted, incorrectly transformed, mis-associated, or rendered unrecoverable?

## Market reasoning

Generic database reconciliation is already a mature category. Source-to-target count checks, field comparisons, and row-level validation are available in existing migration/data-quality products. ProofShift therefore should not compete on "compare PostgreSQL table A to PostgreSQL table B."

The commercial opportunity is stronger where:

- source and target representations differ substantially;
- data exists across multiple stores;
- historical state matters as much as current state;
- domain semantics matter more than physical schemas;
- financial or regulatory invariants must reconcile;
- the migration requires repeated trial conversions;
- acceptance requires auditable evidence;
- an independent team or IV&V contractor validates the implementation;
- rollback/recovery confidence matters before production cutover.

## Initial vertical ranking

Current working priority:

1. **Public pensions / retirement administration**
2. **Utility CIS / billing modernization**
3. **Justice / government case-management modernization**
4. **Healthcare / FHIR migration**
5. **ERP / HCM migration**
6. Generic database migration is a platform capability, not the market positioning.

### Why pensions first

Public pension modernization repeatedly includes:

- long-lived member history;
- employment/service-credit history;
- contribution history;
- beneficiary relationships;
- retirement elections;
- benefit calculations and payment histories;
- financial reconciliation;
- documents and audit history;
- repeated mock conversions;
- third-party conversion and/or IV&V functions;
- custom SQL and reconciliation scripts that are recreated project by project.

This creates a good first domain pack: recurring semantic concepts, high consequence of error, but enough schema variation that a configurable semantic assurance engine provides value.

## Product positioning

Short form:

> **ProofShift is a migration assurance and evidence platform for high-risk system modernization.**

More concrete customer language:

> ProofShift independently verifies complex system migrations using repeatable dry runs, semantic reconciliation, complete source-to-target lineage, historical preservation checks, financial and relationship invariants, and recovery-readiness evidence.

## What ProofShift is not

ProofShift is not initially:

- a generic ETL product;
- a general data observability platform;
- a generic database diff tool;
- an ERP migration suite;
- a FHIR server;
- a pension administration system;
- a cloud-only SaaS platform;
- a replacement for a system integrator or migration vendor.

Independence is strategically useful. A prime contractor, migration vendor, IV&V firm, or customer can continue using its existing ETL/migration tooling while ProofShift verifies the result.

## Long-term product shape

ProofShift can eventually support two usage modes:

### Independent verification mode

```text
External migration tooling
Source ───────────────────► Target
  │                          │
  └──────── ProofShift ──────┘
```

ProofShift observes snapshots, rules, target state, and evidence without performing production mutation.

### Orchestrated migration mode

```text
Source
  ↓
ProofShift migration graph / orchestrator
  ↓
Target
  ↓
ProofShift verification
```

The orchestration capability is optional and must not compromise the independence of the verification core.

## Primary long-term moat

The product's defensible value should accumulate in:

1. migration graph semantics;
2. evidence graph and reproducibility;
3. recovery analysis;
4. reusable domain packs;
5. reusable semantic verification rules;
6. migration-history/timeline equivalence;
7. domain-specific business invariants;
8. connectors and vendor adapters;
9. impact analysis across repeated dry runs;
10. audit-ready reporting.

The connector itself is not the moat. SQL Server access, PostgreSQL access, CSV parsing, and REST clients are commodities.
