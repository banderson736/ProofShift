# Generalized Mapping Guide

Mapping authoring turns read-only physical discovery into a reviewable graph.
It does not infer business meaning from similar names or types, and it does
not execute a production migration.

## Review workflow

1. Discover source and target structures.
2. Generate a scaffold and inspect all suggestions, ambiguity reasons, and
   unmapped required fields.
3. Review the CSV against the exact discovery artifacts.
4. Confirm identity mappings and field transformations explicitly.
5. Import to a new project directory, then run strict validation and inspect
   the compiled graph.

The CSV supports these optional columns in addition to the required source
and target field columns:

| Column | Meaning |
|---|---|
| `Role` | `identity` or `attribute`; primary-key correspondences are suggestions, not approval. |
| `Pipeline` | JSON array of registered deterministic versioned transform steps. |
| `Review Status` | Explicit review state; unresolved required mappings remain blockers. |

Identity-role mappings preserve identity values; value-changing identity
pipelines are rejected. Attribute mappings may use the supported copy/trim/
normalization/code-map pipeline steps. One source field may map to multiple
distinct target fields. A target field may not be populated by multiple
conflicting mappings.

## Scope and topology

The authoring command can attach a declared scope to source selectors using
the paired `--evaluation-scope-id` and `--evaluation-scope-type` options.
Current authoring options support `full-declared-source` and
`customer-prepared-subset`; relational predicate scopes are authored in the
graph configuration.

Explicit graph topology is required. Multiple configured edges do not define
implicit join keys or row grouping. Do not treat field fan-out as an
entity-level split. Merge execution remains unsupported until its grouping,
identity, disposition, lineage, scheduling, and Recovery semantics are
specified and validated. Preserve visible exclusions and unaccounted source
findings; do not hide defects by relabeling them as accepted exceptions.

## Review checklist

- Source and target objects resolve to the attached discovery snapshots.
- Every source has an intended, explicit disposition in the graph.
- Identity fields are stable and compatible on each side.
- Transformations are registered, versioned, deterministic, and reviewable.
- Split/fan-out behavior has explicit target mapping and exact lineage tests.
- No sensitive data or secret is embedded in the CSV or generated project.
- Strict validation succeeds only after required mappings are confirmed.
- Completeness claims match the declared scope, not assumptions about the
  undiscovered source population.
