# ADR 0005: Source Temporal and Binary Value Fidelity

- Status: Accepted
- Date: 2026-10-03

## Context

Before hardening, PS-0.4 mapped both `DateTimeOffset` and `DateTime` through UTC normalization into one `DateTimeValue`, inventing an instant for SQL Server `datetime`/`datetime2` and PostgreSQL `timestamp without time zone`. The old binary value stored a reference/hash but not byte length, and no source API could reopen its content as a stream.

Projection must preserve source semantics without forcing large binary payloads into memory. The reference must be useful within a later projection/runtime while avoiding credentials and machine-specific absolute paths.

## Decision

1. Keep date-only values as `DateValue`.
2. Add explicit `InstantValue` (normalized to UTC), `OffsetDateTimeValue` (preserves its source offset), and `LocalDateTimeValue` (requires `DateTimeKind.Unspecified` and preserves wall-clock ticks). `DateTimeValue` remains obsolete for source compatibility but new connectors do not emit it.
3. Extend `BinaryReferenceValue` with required non-negative content length and lowercase SHA-256. A source binary resolver opens the content as a stream using the connector context, selector, artifact identity, and binary reference.
4. Filesystem binary references contain only a normalized endpoint-relative path. Relational references contain a versioned base64url-encoded locator with schema, table, binary column, and typed composite identity values; they contain no connection string or secret.
5. PostgreSQL timestamptz and SQL datetimeoffset retain instant/offset semantics respectively. PostgreSQL timestamp and SQL datetime/datetime2 remain local date-time values; `DateTimeKind.Local` provider results fail closed because machine-local timezone semantics are not source metadata.
6. Binary hashing and copying operate on streams. Connectors do not materialize large binary values in `byte[]` as part of record conversion.
7. CSV identity scratch data contains only SHA-256 fingerprints in a temporary SQLite database. Normal disposal rolls back the scratch transaction, closes the database, and removes its sidecar files. Each new index creation sweeps abandoned ProofShift identity-index files older than 24 hours. Abrupt power loss and OS-enforced temp-directory retention remain platform limitations; no raw source identities are written to the index.

## Alternatives considered

### Normalize every timestamp to UTC

Rejected because it fabricates timezone meaning for local timestamps and discards explicit offsets.

### Keep only a content hash for binary data

Rejected because later projection could not retrieve the original stream.

### Store database connection details in binary references

Rejected because references may appear in envelopes/journals and must never contain secrets.

### Keep CSV identity hashes in memory

Rejected because memory would grow with artifact count. A temporary SQLite index provides bounded managed memory and opaque fingerprints.

## Consequences

- The domain value hierarchy gains three explicit temporal variants and binary length.
- Connector abstraction gains a source binary stream resolver contract.
- PostgreSQL and SQL Server integration coverage must prove local/offset/instant mappings and binary stream round-trip once Docker is available.
- PS-0.5 remains blocked until database integration tests execute successfully in a Docker-capable CI environment.
