# PS-0.10E Acceptance Matrix

Status: In Progress and unaccepted. This reconciliation records evidence available in the current workspace; it does not accept PS-0.10E. A status value is exactly `PASS`, `FAIL`, or `NOT YET`.

## Local Behavior Matrix

| # | Acceptance item | Status | Evidence or gap |
|---:|---|---|---|
| 1 | RemoteStorage project is declared as a test project | PASS | `ProofShift.Connectors.RemoteStorage.Tests.csproj`: `IsTestProject=true`, `net10.0`, xUnit v3 and Test SDK. |
| 2 | Test SDK/framework versions resolve from normal repository configuration | PASS | `Directory.Build.props`, `Directory.Packages.props`, `global.json`; restore and build succeeded. |
| 3 | RemoteStorage project is included in the solution | PASS | `ProofShift.sln` includes the project; direct `dotnet test --project` builds it. |
| 4 | Normal unfiltered invocation discovers and executes tests | PASS | Final direct run discovered and executed 53 tests; no discovery defect reproduced. |
| 5 | Known-green comparison project discovers tests | PASS | Direct `ProofShift.Connectors.RemoteObjects.Tests` run: 17 passed, 0 failed, 0 skipped. |
| 6 | Entire RemoteStorage project passes with exact totals | PASS | Final unfiltered run: 53 total, 53 passed, 0 failed, 0 skipped, including the missing-object assertion. |
| 7 | UUID maps to the exact expected GUID value | PASS | `UUID_values_round_trip_as_invariant_guid_strings` asserts both exact fixture GUIDs. |
| 8 | UUID string formatting is invariant and deterministic | PASS | Same test asserts exact lower-case `D`-format output from a UUID-annotated field; unannotated 16-byte fixed binary remains binary. |
| 9 | Fixed-length generic binary retains semantic binary type | PASS | `Fixed_length_binary_columns_remain_binary_without_utf8_coercion` and `UUID_values_round_trip_as_invariant_guid_strings` assert `BinaryReferenceValue` for unannotated 4- and 16-byte fixed binary; the latter also verifies UUID-annotated fields map to GUIDs. |
| 10 | Fixed-length binary reports exact length | PASS | Tests assert lengths 4 and 16 for unannotated fixed binary. |
| 11 | Fixed-length binary preserves exact bytes | PASS | Tests reopen the fixed binary cells and assert their exact byte arrays. |
| 12 | TIME-of-day fails closed with stable schema diagnostic | PASS | `Time_of_day_columns_fail_closed_with_a_schema_diagnostic` asserts `UnsupportedColumnarSchema` in read and inspect. |
| 13 | Legacy INT96 fails closed with stable schema diagnostic | PASS | `Legacy_INT96_columns_fail_closed_by_schema_classification` asserts `UnsupportedColumnarSchema`. |
| 14 | Oversized UINT64 fails closed without wrapping | PASS | `Oversized_uint64_values_fail_closed_without_wrapping` asserts `UnsupportedPhysicalType`. |
| 15 | Nested and repeated schemas fail closed | PASS | `Unsupported_logical_types_fail_closed_with_a_schema_diagnostic` covers list and struct fixtures. |
| 16 | Over-wide decimal fails closed | PASS | Same parameterized test covers `DECIMAL(38,2)` and asserts `UnsupportedColumnarSchema`. |
| 17 | Corrupt Parquet fails closed without partial records | PASS | `Truncated_and_garbage_objects_fail_closed_without_partial_records` covers truncated, garbage and damaged-footer files. |
| 18 | Multi-row-group reading preserves record order | PASS | `Many_row_groups_stream_in_order_with_bounded_range_requests` reads 5,000 rows across 20 groups; uneven-scale test also checks sequential IDs. |
| 19 | Same Parquet records, hashes and identities across transports | PASS | `Same_parquet_delivered_over_every_transport_yields_identical_records_hashes_and_identities` exercises filesystem, S3-compatible, Azure Blob and SFTP. |
| 20 | Cross-transport semantic hashes match | PASS | Same test hashes the canonical normalized record representation and compares all four transports. |
| 21 | Cross-transport discovery fingerprints match | PASS | `Parquet_over_each_transport_discovers_the_same_structural_fingerprint` asserts one fingerprint across all four transports. |
| 22 | Uneven row-group fixture records file size, row count, group count and largest group | PASS | `uneven-row-groups.parquet`: 14,409,454 bytes; 320,000 rows; 21 groups; largest group 120,000 rows versus 10,000. |
| 23 | Row-group read time and RSS are recorded | PASS | Focused run: 6.207 s; start RSS 65,421,312 B; end RSS 139,280,384 B; process-lifetime peak 140,316,672 B. |
| 24 | Row-group conclusion is limited to tested scale | PASS | ADR-0026 and connector runtime doc state no evidence of whole-input materialization at tested scale, not constant memory. |
| 25 | S3-compatible pagination, ordering and prefix boundary are tested | PASS | MinIO `Pagination_special_names_prefix_boundaries_and_hashes_are_exact`: 250+ objects, page size 100, decoys outside scope excluded. |
| 26 | Azure pagination, ordering and prefix boundary are tested | PASS | Azurite `Pagination_special_names_prefix_boundaries_and_hashes_are_exact`: 130+ objects, page size 50, decoys excluded. |
| 27 | SFTP enumeration is bounded for many small objects | PASS | Unit `Shuffled_inputs_and_chunk_sizes_produce_identical_utf8_order_and_discovery_fingerprint` asserts peak buffer <=7 entries/512 metadata bytes, multiple runs and fan-in <=3, with cleanup. OpenSSH `Many_small_objects_use_bounded_spills_and_keep_global_order_across_chunk_sizes` enumerates 59 mixed-name files with chunk limit 8; it asserts all paths once in exact UTF-8 order, peak buffer <=8 entries/512 bytes, multiple spill runs, fan-in <=3, temp cleanup and equal discovery fingerprints at chunk limits 8 and 13. |
| 28 | Deterministic ordering and duplicate-free results are tested | PASS | S3/Azure tests compare exact expected key sequences; SFTP nested-name test compares exact UTF-8 order; shared contract rejects unordered listings. |
| 29 | Object mutation during checkpoint observation fails closed | PASS | RemoteObjects `Checkpoint_capture_detects_mutation_between_inventories`; Parquet `Mutation_during_capture_is_detected`. |
| 30 | Version/metadata changes cannot yield a mixed object observation | PASS | Generic mutation test observes the changed version and throws; S3/Azure version tests pin versions or reject changed ETags; SFTP inventory detects changes. |
| 31 | Transient remote failures are retried | PASS | RemoteObjects `Transient_failures_are_retried_within_bounds_and_permanent_failures_are_not`. |
| 32 | Retry count is bounded | PASS | Same test asserts exactly three attempts and two capped backoff delays. |
| 33 | Non-transient failures are not retried | PASS | Same test asserts one attempt for authorization failure. |
| 34 | Cancellation interrupts retry | PASS | RemoteObjects `Cancellation_is_not_retried` asserts cancellation and one attempt. |
| 35 | Cancellation cannot publish a complete checkpoint | PASS | EndToEnd `CancellationDuringCapturePersistsAnUnreplayableIncompleteCheckpoint` asserts Cancelled, no checkpoint and replay rejection. |
| 36 | Cancellation/finalization failure cannot publish a valid receipt or successful Evidence | PASS | Verification `LedgerFailureOrCancellationNeverPublishesCompleteEvidence` exercises cancellation and receipt rejection. |
| 37 | S3 missing bucket/object and denied access fail closed | PASS | Final MinIO run asserts missing bucket/object and bad credentials; `Provider_errors_map_to_classified_failures` checks denied/transient/version mappings. |
| 38 | S3 scope boundary is enforced | PASS | Same MinIO pagination test excludes `data-other/` and outside-prefix decoys. |
| 39 | Azure authorization failure is classified and redacted | PASS | Azurite bad-key test accepts authentication or authorization classification and asserts the key is absent; docs retain the emulator caveat. |
| 40 | Read-only Azure SAS cannot write | PASS | `Read_only_sas_cannot_be_used_to_write_and_connector_exposes_no_write_path` asserts provider denial and no upload/delete method. |
| 41 | Azure container/prefix scope is tested | PASS | Azurite SAS capture test is container-scoped; pagination test excludes prefix-boundary decoys. |
| 42 | SFTP wrong password and host fingerprint fail distinctly and redacted | PASS | `Wrong_host_key_and_wrong_password_fail_closed_with_distinct_redacted_diagnostics`. |
| 43 | SFTP root scope enforcement is tested | PASS | `Reads_nested_special_names_in_byte_order_with_exact_hashes_and_never_follows_symlinks` rejects escape paths. |
| 44 | SFTP symlinks are refused | PASS | Same test exercises file, parent-directory and linked-directory symlinks, including direct addressing. |
| 45 | Mixed-provider checkpoint truthfully reports non-atomicity | PASS | `One_checkpoint_can_mix_transports_and_formats_without_claiming_cross_system_atomicity` asserts `CrossSystemAtomic=false`. |
| 46 | Mixed checkpoint replays with all provider objects removed | PASS | Same test deletes the MinIO object/bucket, Azurite container and SFTP object before replay. |
| 47 | Mixed offline replay has no live-source fallback | PASS | Replay uses `CheckpointSourceArtifactStreamProvider` after provider resources are unavailable and asserts all three record counts. |
| 48 | The existing 24 MiB object test proves streaming and SHA-256 | PASS | MinIO `Ranged_reads_return_exact_slices_and_large_objects_stream_with_matching_hash` asserts exact range bytes and full-object SHA-256. |
| 49 | The 24 MiB object is materialized into a complete checkpoint | PASS | Same MinIO test captures the 24 MiB source and asserts `CheckpointStatus.Complete`. |
| 50 | The 24 MiB checkpoint replays offline after source deletion | PASS | Same test deletes the source object and bucket, then asserts the exact replayed byte array and SHA-256. |
| 51 | Generated schemas list S3, S3-compatible, Azure, SFTP and Parquet transports | PASS | CLI schema/capability tests plus `Schema_and_capabilities_describe_every_installed_transport` assert the four Parquet transport enum values. |
| 52 | Plaintext inline credentials are prohibited where required | PASS | `SchemasForTheSecretBearingEndpointsRequireSecretReferencesAndNeverEmbedValues`, remote inline-secret tests and strict validation assert rejection without echo. |
| 53 | Safe examples exist for all eight requested endpoint shapes | PASS | `docs/CONFIGURATION.md` now includes S3, S3-compatible, Azure, SFTP and Parquet over filesystem/S3/Azure/SFTP using placeholders and secret references. |
| 54 | Capabilities expose only truthful read/observation authority | PASS | `CapabilitiesDescribeTransportsRangeSupportAndNoShadowWrite` asserts discovery/read/checkpoint/observation and `shadowWrite=false`; connector implementations expose no delete/migration/rollback authority. |
| 55 | CLI discovery remains credential-free and deterministic | PASS | `InitCreatesCredentialFreeStrictlyValidProjectAndRefusesOverwrite` exercises `discover`; physical discovery fingerprints are validated by authoring tests. |
| 56 | CLI scaffold is deterministic, credential-free and does not invent domain semantics | PASS | `ScaffoldAndReviewedCsvImportCompileNormallyWhileStrictRejectsSuggestions`, enterprise selector scaffold tests and `ScaffoldIsDeterministicAndSuggestsStructureWithoutApprovingAmbiguousOrUnmappedFields`. |
| 57 | AWS access key, secret and session token are redacted | PASS | MinIO `Missing_bucket_and_bad_credentials_fail_closed_without_leaking_secrets` asserts synthetic key, secret and session token are absent. |
| 58 | Azure account key and SAS are redacted | PASS | Azurite bad-key/SAS diagnostics assert both synthetic values are absent. |
| 59 | SFTP password, private key and passphrase are redacted | PASS | Wrong-password test and malformed-private-key configuration test assert synthetic values are absent. |
| 60 | Resolved secret values do not enter canonical configuration or fingerprints | PASS | Configuration `RuntimeSecretsResolveSeparatelyAndNeverEnterLoadedConfigurationOrHash` asserts secret exclusion from canonical text, configuration hash and exposed model. |

## Completion Gates

| Gate | Status | Result / next action |
|---|---|---|
| Full local core suite | PASS | Final runtime-code run: restore/build succeeded; 239 total, 237 passed, 0 failed, 2 documented Windows symlink-capability skips. |
| Full RemoteStorage project | PASS | Final unfiltered run: 53 total, 53 passed, 0 failed, 0 skipped. |
| Oracle integration | PASS | Final run: 2 total, 2 passed, 0 failed, 0 skipped against the available pinned Oracle Free test image. |
| Db2 integration | PASS | Final run: 2 total, 2 passed, 0 failed, 0 skipped against Db2 LUW Community 11.5.9.0. |
| Pension Fast | PASS | Final run: 2 total, 2 passed, 0 failed, 0 skipped; exact defective 149/corrected 0, unaccounted 0, unexplained 0, Recovery `Passed`, corrected `QUALIFIED`. |
| Documentation/ADR closure | PASS | README, ROADMAP, AGENTS, Copilot instructions, connector runtime/capability docs, safe configuration examples and Proposed ADR-0026 are updated. |
| Dependency/license review | PASS | NuGet manifest declarations and PyArrow 18.1.0 metadata are verified; requested packages have permissive licenses and MinIO remains test-only. |
| Worktree/diff review | PASS | Candidate review completed; `git diff --check` passed and the code candidate was merged to `main` as `d5bcf8b528bd9d7370841fbfb92625a8954fa75d`. |
| Candidate commit and SHA | PASS | PS-0.10E code candidate: `a6e8b15528df5767dd5368f96c000d8dcfb0d1f1`. |
| Remote CI and push | PASS | GitHub Actions [run 38068572196](https://github.com/banderson736/ProofShift/actions/runs/38068572196) for `a6e8b15528df5767dd5368f96c000d8dcfb0d1f1` passed `build-and-test`, `remote-storage-integration`, `oracle-integration`, and `db2-integration`; all jobs succeeded. Candidate is merged to `main`. |
| Formal PS-0.10E acceptance | NOT YET | Reserved for formal review; the coding agent does not self-accept this milestone. |

## Dependency and License Review

License expressions below were read from the resolved NuGet package manifests; PyArrow is pinned in the fixture-generation script. Permissive commercial use is allowed subject to preserving the applicable license/notice terms. This is a package-level engineering review, not legal advice.

| Package | Version | License | Purpose | Commercial-use concern |
|---|---:|---|---|---|
| `AWSSDK.S3` | 4.0.104.2 | Apache-2.0 | S3 and S3-compatible object access | Permissive; retain license and any required NOTICE files. |
| `Azure.Storage.Blobs` | 12.30.1 | MIT | Azure Blob listing, metadata, range reads and downloads | Permissive; retain copyright/license notice. |
| `Azure.Identity` | 1.21.0 | MIT | Azure default credential chain | Permissive; retain copyright/license notice. |
| `SSH.NET` | 2026.0.0 | MIT | SFTP and pinned SSH host-key support | Permissive; retain copyright/license notice. |
| `Parquet.Net` | 6.1.0 | MIT | Parquet reader | Permissive; retain copyright/license notice. |
| Apache Arrow `pyarrow` | 18.1.0 | Apache-2.0 | Independent test-fixture generation only; not a runtime dependency | Permissive; retain license/NOTICE when distributing the fixture toolchain. |
| MinIO test image | pinned digest | AGPL-3.0 | Disposable S3-compatible integration service, isolated in a test container | Not linked, bundled or redistributed; keep test-only and do not package the image. |

No AWS certification, Azure certification or production-SFTP certification is claimed. Parquet remains flat-schema only; nested/repeated values, INT96, time-of-day, oversized unsigned values, over-wide decimals and unsupported logical/physical combinations fail closed. REST/FHIR Bulk is not implemented. All object connectors remain read-only; production write, delete, migration execution and rollback are out of scope.