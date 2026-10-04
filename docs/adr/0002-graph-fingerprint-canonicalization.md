# ADR 0002: Graph Fingerprint Canonicalization

- Status: Accepted
- Date: 2026-10-03

## Context

Migration plans and runtime fingerprints must identify the exact graph/configuration used by a run. A hash is only reproducible if its input has a stable canonical representation. Hashing object serialization directly would make identity depend on serializer defaults, property ordering, runtime behavior, or future model changes.

PS-0.1 needs to carry graph and configuration fingerprints but does not yet have a graph compiler or configuration parser. Defining a serialization in the domain model would prematurely bind it to an encoding that cannot yet be exercised end to end.

## Decision

The PS-0.1 domain model treats graph, configuration, and plan fingerprints as required opaque, non-empty values. It does not compute them or infer canonical serialization.

PS-0.2 defines `proofshift-config-canonical-v1`: YAML mappings are sorted ordinally, sequence order is retained, comments and scalar presentation style are ignored, and scalar categories are represented explicitly. The root and referenced documents are included by normalized project-relative path, then hashed with SHA-256. Environment/secret reference names are included; resolved values are not retained or hashed.

PS-0.3 defines and tests `proofshift-graph-canonical-v1` before producing graph fingerprints. Graph nodes and edges are sorted by external key; property maps and field mappings are sorted; source/target references are treated as sets; identity fields and transformation pipelines retain order. Canonical encodings are deterministic, versioned, and independent of runtime serializer defaults. A fingerprint exposes its encoding version.

## Alternatives considered

### Hash runtime JSON serialization

Rejected because property ordering, serializer configuration, and model evolution can silently change hashes.

### Define a canonical graph format in PS-0.1

Rejected because no graph compiler/configuration parser exists yet to validate the encoding against actual configuration inputs.

### Omit fingerprints from the domain model

Rejected because plans and runs need to preserve the identity of the graph and configuration that produced their results.

## Consequences

- Domain types require graph/configuration fingerprint values but do not claim to validate their content or format.
- Configuration equivalence and semantic-change tests cover the PS-0.2 canonical encoding; PS-0.3 tests graph ordering independence and semantic hash changes.
- Fingerprint values must never contain resolved secrets.
