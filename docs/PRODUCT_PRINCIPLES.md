# Product and Engineering Principles

These are constraints, not aspirational slogans.

## 1. Model systems, not databases

A migration can span multiple source and target stores. Storage technology is an adapter concern.

## 2. Semantics over physical shape

ProofShift verifies that meaning survived, not that rows or JSON shapes look identical.

## 3. History is first-class

Current state alone is insufficient for many regulated systems.

## 4. Dry run must create realistic output

Projection should write to isolated shadow targets where practical. A dry run is not merely a prediction report.

## 5. Verification must remain usable independently

ProofShift must verify externally executed migrations. Future orchestration cannot become a prerequisite for assurance.

## 6. Every source artifact is accounted for

No silent loss.

## 7. Every target artifact has provenance

No unexplained creation.

## 8. Every conclusion is evidence-backed

Pass/fail without traceable reason is insufficient.

## 9. Destructive actions require recovery analysis

Migration readiness includes rollback/recovery readiness.

## 10. Configuration is versioned and reproducible

A plan/run must identify exact rules, mappings, graph, connectors, domain packs, configuration hash, and runtime version.

## 11. Repeated dry runs are a primary workflow

The product should make iterative remediation obvious and comparable.

## 12. Domain packs contain reusable IP

Pension/utility/justice/healthcare semantics belong in packs layered over the generic engine.

## 13. Avoid a universal business schema

Use a small generic evidence/artifact model. Domain semantics remain explicit and distinct.

## 14. Customer-hosted operation should remain possible

Regulated customers may not want sensitive data leaving their environment. Product architecture should not require ProofShift-operated SaaS.

## 15. Minimize initial infrastructure

Do not introduce Kubernetes, Kafka, Redis, search clusters, etc. until measurable requirements justify them.

## 16. Determinism matters

Synthetic scenarios, config hashing, graph hashing, and exact defect assertions are central to confidence.

## 17. Explainability beats cleverness

Avoid opaque AI-based acceptance decisions. AI may later assist mapping/discovery, but verification conclusions must remain deterministic and auditable.

## 18. Protect independence and IP boundaries

Use public standards, public procurement knowledge, and independently written code. Never incorporate employer/client proprietary code, architecture, documents, or confidential implementation details.
