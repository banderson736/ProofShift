# ADR 0001: Foundational Migration Assurance Architecture

- Status: Accepted
- Date: 2026-10-03

## Context

High-risk migrations span heterogeneous storage, historical information, transformations, relationships, and recovery concerns. A table-diff model cannot express the required semantics or evidence.

## Decision

ProofShift will use three distinct foundational models:

1. Migration Graph
2. Evidence Graph
3. Recovery Model

The verification core remains independent of production migration execution.

## Consequences

Positive:

- supports heterogeneous/distributed systems;
- supports semantic rather than structural verification;
- enables dry-run/shadow migration;
- supports lineage and auditability;
- supports future orchestration without coupling assurance to it;
- gives reusable primitives for future RampForge work.

Costs:

- more up-front domain modeling than a table comparator;
- graph/evidence lifecycle must be carefully versioned;
- requires strong configuration validation;
- careful persistence/scalability design will be necessary later.

## Rejected alternatives

### Generic database diff engine
Rejected because it is commercially crowded and cannot naturally express semantic history, heterogeneous stores, or recovery.

### ProofShift as ETL engine first
Rejected because independent verification is strategically valuable and entering the ETL market greatly broadens scope.

### Universal canonical business schema
Rejected because cross-domain semantics are too different. Use generic artifact/evidence concepts plus explicit domain packs.
