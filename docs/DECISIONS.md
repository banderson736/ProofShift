# Architectural Decisions Summary

This file is a quick index. Detailed decisions may be added as ADRs under `docs/adr/`.

## Accepted decisions

### D-001 — ProofShift is migration assurance, not generic DB diff
Status: Accepted

ProofShift's core value is semantic, auditable, recoverable migration assurance across heterogeneous systems.

### D-002 — Public pensions are the first domain pack
Status: Accepted

The core remains domain-neutral. Pension is the initial demonstration/commercial wedge based on current market research.

### D-003 — Migration Graph, Evidence Graph, Recovery Model are foundational
Status: Accepted

Do not collapse these into one flat job/result structure.

### D-004 — History is first-class
Status: Accepted

Current state alone is insufficient.

### D-005 — Dry runs create shadow output
Status: Accepted

Projection should materialize realistic isolated target state, not only predict changes.

### D-006 — Verification can operate independently of execution
Status: Accepted

External migration tools/vendors remain supported. Future ProofShift orchestration is optional.

### D-007 — Every source artifact has a disposition
Status: Accepted

Unaccounted artifacts fail readiness.

### D-008 — Every target artifact has lineage
Status: Accepted

Targets without provenance fail readiness.

### D-009 — Recovery readiness is part of migration readiness
Status: Accepted

Destructive transformations require explicit recovery strategy. PS-0 rejects irreversible operations.

### D-010 — Domain packs, not a universal business schema
Status: Accepted

Generic core concepts remain small; industry semantics are pack-specific.

### D-011 — .NET 10
Status: Accepted for bootstrap

New repository targets current LTS .NET 10. Reconsider only for a concrete customer/platform constraint.

### D-012 — CLI first
Status: Accepted

PS-0 is usable/testable without web UI.

### D-013 — Minimal infrastructure
Status: Accepted

No Kafka/Redis/Kubernetes merely because the team knows them. Add only from measured requirements.

### D-014 — Canonical fingerprints are defined by the compiler
Status: Accepted

PS-0.1 stores graph and configuration fingerprints as opaque required values. PS-0.2/PS-0.3 must define and test versioned canonical encoding before computing them. See `docs/adr/0002-graph-fingerprint-canonicalization.md`.
