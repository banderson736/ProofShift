# ADR-0025: SFTP Host-Key Pinning and Symbolic-Link Policy

Status: Proposed (PS-0.10E; pending review, not self-accepted)

## Decision

- The SFTP endpoint **requires** `hostKeyFingerprint` in OpenSSH `SHA256:<base64>` form. There is no trust-on-first-use, no "accept any" mode and no downgrade. A mismatch aborts the handshake before authentication and yields `PSCONN027`. Authentication failure (`PSCONN025`) is reported separately so operators can tell a rotated host key from a bad credential. Fingerprints are public values, but diagnostics do not echo the observed fingerprint.
- Authentication is `password` or `private-key`; credentials and passphrases must be secret references. Algorithms are the SSH.NET defaults; no legacy algorithm is enabled.
- Symbolic links are **never followed**. Listing skips link entries; direct addressing of a key canonicalizes every path component and refuses any link (`PSCONN028`), so neither a link to a file outside the root nor a link to a directory can make out-of-scope content observable.
- Directory traversal streams file descriptors through bounded entry/metadata chunks, spills sorted UTF-8 runs to private operating-system temporary storage, and merges with bounded fan-in. Chunk/run details are operational and do not affect object identity or discovery fingerprints. This is a bounded-enumeration implementation, not a constant-memory or unlimited-directory-size guarantee.
- Keys are POSIX paths relative to `remoteRoot`. `.` / `..` segments, backslashes and absolute keys are refused.
- SFTP has no version pinning and no conditional read; mutation is detected by stat (length, modification time) before and after the read and by the checkpoint hash.

## Consequences

Operators must record the server's fingerprint out of band. Host-key rotation is a deliberate configuration change, which is the intended failure mode. Servers that expose only legacy algorithms are not supported.
