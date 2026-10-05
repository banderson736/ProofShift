# ADR-0013: Generic performance stage records

- Status: Accepted for PS-0.10A
- Date: 2026-10-04

## Context

PS-0.9 has a reproducible integrated fast run, but its measurements are mostly coarse-grained and test-owned. PS-0.10A requires stage timings and counts across checkpointing, projection, verification, recovery, and reporting, plus human-readable and machine-readable benchmark output. Measurements must distinguish environment/fixture work from ProofShift runtime and must not add source identities or high-cardinality customer telemetry.

## Decision

- Represent benchmark output as immutable, versioned `PerformanceRun`, `PerformanceStage`, and `PerformanceMeasurement` records in Domain.
- Collect timings/counts with a run-scoped `PerformanceRecorder` in Engine. Services receive the recorder explicitly and record bounded stage identity such as connector, graph node, edge, or rule; individual artifact identities and source values are never recorded.
- Keep performance results separate from Evidence Graphs and assurance fingerprints. Batch size, writer buffering, execution scheduling, and process resource observations must not alter semantic fingerprints.
- Use recorded stage data for repeatable benchmark reports. Operational OpenTelemetry telemetry may later complement this result model but is not a substitute for the benchmark artifact.

## Alternatives considered

### OpenTelemetry metrics only

Rejected as the sole benchmark format: aggregate telemetry does not provide a self-contained, reproducible run artifact or the ordered stage/count report required by the benchmark workflow. High-cardinality labels also risk capturing customer identifiers.

### Pension-specific benchmark DTOs

Rejected because checkpoint, projection, verification, recovery, and reporting are generic runtime stages and must be measurable for other scenarios without adding domain dependencies to core.

### Persist timings in verification evidence

Rejected because performance observations are not assurance conclusions and must not change Evidence Graph fingerprints or acceptance semantics.

## Consequences

The run model is generic and can be serialized by benchmark/reporting tools. Stage instrumentation remains optional for ordinary calls. Environment startup and fixture loading are recorded as separate stage kinds from ProofShift processing. Memory/workspace values are explicitly labeled observations, not guarantees.