# ADR-0026: Parquet Logical-Type Mapping and Supported Subset

Status: Proposed (PS-0.10E; pending review, not self-accepted)

## Decision

`parquet` is a transport-independent format connector: the endpoint declares `transport` (`filesystem`, `s3`, `azure-blob`, `sftp`) plus that transport's properties, and the connector reads through `SeekableRemoteStream`. The reader processes one row group at a time and materializes its selected columns. The tested scale probe found no evidence of whole-input materialization at that scale; this is not a constant-memory or production-scale guarantee. Parquet.Net (MIT) is the reader. Checked-in fixtures are written by Apache Arrow (pyarrow), an independent implementation; the explicit UUID-annotation fixture is generated at test runtime with Parquet.Net's `DataField<Guid>` writer.

Supported (flat schemas only):

| Parquet | ProofShift value | Notes |
| --- | --- | --- |
| BOOLEAN | `BooleanValue` | |
| INT8/16/32/64, UINT8/16/32 | `IntegerValue` | UINT64 above `long.MaxValue` fails closed |
| DECIMAL(p<=28, s) | `DecimalValue` | exact; p>28 fails closed with `PSCONN031` |
| FLOAT / DOUBLE | `StringValue` (round-trippable "R") | matches existing relational mapping; never labelled exact |
| STRING / UTF8 | `StringValue` | no normalization; NULL distinct from empty |
| BYTE_ARRAY / FIXED_LEN_BYTE_ARRAY without UUID annotation | `BinaryReferenceValue` | bytes are streamed, hashed and materialized into checkpoints, never converted to text |
| DATE | `DateValue` | |
| TIMESTAMP(isAdjustedToUTC=true) | `InstantValue` | |
| TIMESTAMP(isAdjustedToUTC=false) | `LocalDateTimeValue` | no machine time zone is applied |
| UUID on 16-byte FIXED_LEN_BYTE_ARRAY | `StringValue` (D format) | RFC byte order; invariant lowercase GUID formatting |

Fail closed with `PSCONN031`: nested types (struct, list, map), repeated columns, legacy INT96 timestamps, nanosecond timestamps, TIME-of-day, UINT64 values above `long.MaxValue`, decimals above 28 digits, and unsupported logical/physical combinations. Corrupt, truncated or unsupported-encoding files fail with `PSCONN032` and emit no partial records. Dictionary encoding, compression codecs (snappy, gzip, none) and multiple row groups are transparent.

Identity is the configured semantic key (`identity: [id]`), never the physical row position; a NULL identity component is `PSCONN012` and a duplicate is `PSCONN005`. Row position and row group are provenance metadata only. Optional `columns` projects a subset. Discovery fingerprints the structural schema (names, native type incl. precision/scale/time unit/UTC flag, nullability), not the data or row count.

## Consequences

Binary cell references (`parquet-cell-v1:`) re-read one row group to materialize a cell during checkpoint capture; this is bounded by row-group size. Float and double values are not comparable as exact decimals by design.

## Test coverage and known gaps

Direct tests against Arrow-written files assert that an unannotated 16-byte fixed binary remains binary, plus fixed-length generic binary type, length and bytes; TIME, INT96, oversized UINT64, nested/repeated, over-wide decimal and corrupt-file diagnostics; and multi-row-group reads. The exact UUID test writes a standard UUID-annotated column with `DataField<Guid>` and asserts both known GUID values and invariant formatting. The 320,000-row uneven-group fixture contains 21 groups, with one 120,000-row group and twenty 10,000-row groups. Its recorded filesystem read was 14,409,454 bytes in 6.207 seconds; process RSS was 65,421,312 bytes at test start, 139,280,384 bytes at test end and 140,316,672 bytes process-lifetime peak. At the tested scale, no evidence of whole-input materialization was observed; this is not a constant-memory claim.

Cross-transport tests compare normalized records, semantic hashes, source hashes, identities and discovery fingerprints across filesystem, S3-compatible, Azure Blob and SFTP. The Parquet transport schema explicitly lists those four transports. The separate 24 MiB S3-compatible test asserts ranged reads, SHA-256, complete checkpoint materialization and exact offline replay after deleting the source bucket.
