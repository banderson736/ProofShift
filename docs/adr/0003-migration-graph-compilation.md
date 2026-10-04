# ADR 0003: Migration Graph Compilation and Identity

- Status: Accepted
- Date: 2026-10-03

## Context

PS-0.3 must compile graph configuration keys such as `legacy-member` into the PS-0.1 domain model. The current domain uses GUID `MigrationNodeId` and `MigrationEdgeId`, but the configuration uses stable string keys. The domain edge also has a single `From` and `To`, which cannot express split or merge, and nodes do not carry a connector-neutral physical selector.

PS-0.2 loads the graph as a generic YAML document and canonical string. The graph compiler needs immutable structured values without reparsing YAML or adding a YAML dependency to the graph/domain layer.

## Decision

1. Migration edges contain immutable source and target ID collections. External edge keys are preserved as edge names. Nodes preserve their external keys in the existing node name.
2. Nodes carry a connector-neutral `ArtifactSelector`: a kind identifier, immutable string properties, and ordered identity fields. Concrete connectors interpret selector meaning later.
3. Configuration exposes an immutable generic YAML document tree. `ProofShift.Graph` references the Configuration DTO project and compiles its graph document into graph-specific DTOs and then domain objects. The graph project does not depend on YamlDotNet, concrete connectors, or domain packs.
4. Configuration graph keys remain external string keys. Internal graph/node/edge GUIDs are deterministic UUIDv8-style identifiers derived from SHA-256 over a versioned, unambiguously length-prefixed tuple of the configuration project key, normalized graph file path, entity kind, and external key. The external key remains available on the compiled model. No mapping to the internal `ProjectId` is introduced.
5. Graph canonicalization is versioned as `proofshift-graph-canonical-v1`. It sorts graph nodes and edges by external key, selector/property maps and field mappings by key, and source/target reference sets by external key. It preserves identity-field and transformation-pipeline order. Recovery values and all transformation parameters participate in the canonical form. The hash is lowercase SHA-256 of this representation.
6. Every compiled edge requires explicit recovery metadata. Restore requires a snapshot; compensate requires a strategy; irreversible recovery requires a justification.
7. Cycles consisting exclusively of relationship-operation edges are reported as warnings. Cycles containing execution/transform edges or destructive operations are errors. PS-0.3 does not otherwise infer scheduling semantics.

## Alternatives considered

### Change all domain graph IDs to strings

Rejected because PS-0.1 established strongly typed GUID identifiers and changing their representation would expand the contract change beyond what compilation requires.

### Generate random GUIDs during compilation

Rejected because repeated compilation would produce different graphs and hashes.

### Keep single-source/single-target edges

Rejected because split/merge operations would need lossy edge expansion and would no longer represent the configured operation faithfully.

### Reparse YAML inside ProofShift.Graph

Rejected because it duplicates PS-0.2 loading, diagnostic, path, and secret-handling behavior and introduces a parser dependency to the graph compiler.

### Treat every graph cycle identically

Rejected because relationship-only cycles may be legitimate while execution/destructive cycles must be visible as errors.

## Consequences

- The PS-0.1 domain edge API changes to collection cardinality, and graph nodes gain a generic selector.
- Graph identifiers are reproducible within the scope of a configuration project key and graph path; changing those identities changes generated internal GUIDs.
- External graph keys remain available for diagnostics, canonicalization, and configuration editing.
- Relationship-cycle warnings do not block compilation; execution/destructive-cycle errors do.
- Graph compilation validates identities and topology only. It does not instantiate connectors, inspect artifacts, execute transformations, or validate semantics against a loaded domain pack.
