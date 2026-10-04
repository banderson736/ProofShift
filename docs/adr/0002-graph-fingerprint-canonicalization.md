# ADR 0002: Graph Fingerprint Canonicalization

- Status: Accepted
- Date: 2026-10-03

## Context

Migration plans and runtime fingerprints must identify the exact graph/configuration used by a run. A hash is only reproducible if its input has a stable canonical representation. Hashing object serialization directly would make identity depend on serializer defaults, property ordering, runtime behavior, or future model changes.

PS-0.1 needs to carry graph and configuration fingerprints but does not yet have a graph compiler or configuration parser. Defining a serialization now would prematurely bind the domain model to an encoding that cannot yet be exercised end to end.

## Decision

The PS-0.1 domain model treats graph, configuration, and plan fingerprints as required opaque, non-empty values. It does not compute them or infer canonical serialization.

The PS-0.2/PS-0.3 configuration and graph compiler work must define and test canonical encoding before producing these fingerprints. The encoding must be deterministic, versioned, independent of runtime serializer defaults, and must not include resolved secret values. A fingerprint must identify its encoding version.

## Alternatives considered

### Hash runtime JSON serialization

Rejected because property ordering, serializer configuration, and model evolution can silently change hashes.

### Define a canonical graph format in PS-0.1

Rejected because no graph compiler/configuration parser exists yet to validate the encoding against actual configuration inputs.

### Omit fingerprints from the domain model

Rejected because plans and runs need to preserve the identity of the graph and configuration that produced their results.

## Consequences

- Domain types require graph/configuration fingerprint values but do not claim to validate their content or format.
- PS-0.2/PS-0.3 must add golden-vector tests for canonical encoding and hashing before reporting reproducibility as implemented.
- Fingerprint values must never contain resolved secrets.
