# ADR-0024: Remote Object Transport Abstraction

Status: Proposed (PS-0.10E; pending review, not self-accepted)

## Context

PS-0.10C connectors read local files or databases. Migration artifacts are increasingly delivered through object storage (S3 and compatible stores, Azure Blob Storage) and SFTP. Verification must work even when another system performs the migration, so ProofShift needs to observe, checkpoint, replay and verify those artifacts independently while preserving provenance, bounded memory and fail-closed security. Core must stay connector-neutral (ADR-0004) and read-only observation must stay separate from shadow writing (ADR-0019).

## Decision

1. A new project `ProofShift.Connectors.RemoteObjects` defines a small, **read-only** transport contract, `IRemoteObjectStore`: ordered streamed `ListAsync`, `StatAsync` (optionally pinned to a version), and `OpenReadAsync` (offset/length). It exposes no write, delete or rename operation. It depends only on `Connectors.Abstractions`/`Domain`; core never references it.
2. One shared `RemoteObjectSourceConnector` implements the selector kind `object-pattern` for every transport. Providers (`ProofShift.Connectors.S3`, `.AzureBlob`, `.Sftp`) contribute only an `IRemoteObjectStoreFactory` that supplies endpoint schema, capabilities and the store. Behavior that must be identical across providers (ordering, hashing, mutation detection, binary references, observation, capture, replay) therefore lives in exactly one place.
3. **Identity.** Artifact identity is the scope-relative key (`relativePath`). Provider ETags, version ids, modification times and lengths are recorded as provenance metadata and are **never** treated as content hashes. SHA-256 is computed by ProofShift over the bytes it actually read.
4. **Ordering.** Listings must be strictly ascending in UTF-8 byte order. Providers whose server order is not defined (SFTP) sort; a listing that arrives out of order fails closed with `PSCONN012` rather than producing non-deterministic output.
5. **Mutation detection.** A read stats the object, streams and hashes it (the byte count must equal the listed length), then stats again; any change in length, modification time, ETag or version fails with `PSCONN015`. Checkpoint capture additionally compares a digest of the whole matched inventory before and after the capture.
6. **Version pinning.** When a provider reports a version (S3/Azure versioning), the version id is stored in the binary reference so materialization re-reads the same bytes even if the object is later overwritten. Without versioning, conditional reads (`If-Match` / ETag) and the post-read stat bound the window; SFTP has no pinning and relies on stat comparison plus the checkpoint's recomputed hash.
7. **Range reads.** `SeekableRemoteStream` presents a seekable, read-only stream backed by bounded range requests (one block in memory) so footer-oriented formats such as Parquet can run unchanged over any transport that declares `RangeRead`.
8. **Failure model.** Providers map errors to `RemoteStoreException` with `RemoteFailureKind`, which maps to stable diagnostics `PSCONN025`–`PSCONN034`. Messages never include provider text, URLs with credentials, or secret values. `RemoteRetryPolicy` retries only `Transient`, boundedly, with capped exponential delay and cancellation awareness; it never retries authentication, authorization, host-key, scope, version or integrity failures.
9. **Secrets.** Credential properties (`accessKeyId`, `secretAccessKey`, `sessionToken`, `accountKey`, `sasToken`, `password`, `privateKey`, `privateKeyPassphrase`) must be supplied as `secret:` references. The runtime refuses a credential that was not resolved from a secret reference (`PSCONN034`), and configuration loading rejects inline values (`PSCFG012`). Plain-HTTP endpoints require an explicit `allowInsecureHttp` opt-in intended for isolated test fixtures.
10. **Capabilities.** `ConnectorCapabilityDescriptor` gains optional fields `Transport`, `StructuredRead`, `RangeRead`, `VersionPinning`, `OfflineReplay`; existing fields and JSON names are unchanged.

## Consequences

- Observation is read-only. No shadow-write capability exists for any remote transport; `shadowWrite` is false in `proofshift capabilities`.
- Checkpoints remain the offline replay boundary: replay never contacts the transport and never falls back to live reads. A mixed-transport checkpoint reports `CrossSystemAtomic=false` and `Observed` consistency per endpoint.
- Object stores are not snapshot-isolated. ProofShift detects, but cannot prevent, mutation during capture.

## Alternatives considered

- One bespoke connector per provider: rejected; the shared behavior (ordering, hashing, mutation, binary references) would diverge.
- Treating ETag as content hash: rejected; multipart ETags and provider-specific schemes are not content digests.
- Adding upload/delete for test convenience: rejected; fixtures write through provider SDKs outside the connector.
