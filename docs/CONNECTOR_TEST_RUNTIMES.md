# Connector Test Runtimes

These runtimes are used only for synthetic development, automated tests and integration tests. Vendor database images are built or pulled at test runtime. ProofShift does not bundle, publish, upload, or redistribute the images through the repository, NuGet, releases, installers, Docker images, or CI artifacts. Commercial packaging and redistribution review is deferred.

## Oracle

- Server image: `oracle/database:23.26.0-free`, built at test runtime by Oracle's official `oracle/docker-images` `buildContainerImage.sh` from pinned repository revision `f376ce7eb011e6adaacfa325f4e2aee99e9a0830` and the official Oracle Free download endpoint. It is not committed, published or uploaded.
- Provider: `Oracle.ManagedDataAccess.Core` 23.26.301; the package targets .NET 8 and is consumed by the .NET 10 connector.
- Test database service: `FREEPDB1`; fixture creates deterministic synthetic objects and credentials at runtime.
- Build inputs: the official source path downloads the Oracle Database Free 26ai 23.26.2-1 RPM directly from `download.oracle.com`; installation media is not stored in the repository. The required CI job does not depend on Oracle Container Registry credentials.
- Runtime purpose: source inspection, discovery, streaming reads, checkpoint capture/replay and read-only target observation. No Oracle projection/write capability is provided.
- Oracle Free has documented edition limits (including CPU, memory and user-data ceilings) and is not a substitute for a production support entitlement. Consult the current [Oracle Free FAQ](https://www.oracle.com/database/free/faq/) and [Oracle Free Use Terms](https://www.oracle.com/downloads/licenses/oracle-free-license.html). This repository makes no broader legal conclusion.

## IBM Db2

- Server image: `icr.io/db2_community/db2:11.5.9.0` (Db2 LUW Community Edition, Linux AMD64 in CI).
- Provider: `Net.IBM.Data.Db2-lnx` 10.0.0.300 on Linux AMD64 and `Net.IBM.Data.Db2` 10.0.0.300 on Windows x64; both target .NET 10.
- Test database: `PS010C`; the fixture's `LICENSE=accept` environment setting is explicit in the test container definition.
- Runtime limits/documented purpose: IBM describes this Community image as unsupported and non-production, with published 4-core/16-GB limits. See [IBM Db2 Community Edition for Docker](https://www.ibm.com/docs/en/db2/11.5.x?topic=deployments-db2-community-edition-docker).
- Runtime purpose: Db2 LUW source discovery, inspection, streamed reads, checkpoint capture/replay and read-only target observation. No Db2 shadow writing is provided. z/OS, IBM i and Db2 Connect-dependent targets are excluded.
- The CI test pulls the official image at runtime and visibly accepts the vendor-required setting. The image is not bundled or uploaded as an artifact.

## Remote storage and Parquet (PS-0.10E)

All fixtures run in Docker through Testcontainers and are pulled at test time; none is bundled, published or uploaded. Images and SDKs are pinned.

| Purpose | Fixture | Pin | SDK / library (license) |
| --- | --- | --- | --- |
| S3-compatible object storage | MinIO | `quay.io/minio/minio@sha256:14cea493d9a34af32f524e538b8346cf79f3321eff8e708c1e2960462bd8936e` | `AWSSDK.S3` 4.0.104.2 (Apache-2.0) |
| Azure Blob Storage | Azurite | `mcr.microsoft.com/azure-storage/azurite:3.35.0` | `Azure.Storage.Blobs` 12.30.1, `Azure.Identity` 1.21.0 (MIT) |
| SFTP | `atmoz/sftp:alpine` (OpenSSH) | tag (digest recorded in CI logs) | `SSH.NET` 2026.0.0 (MIT) |
| Parquet | none (files); fixtures written by Apache Arrow `pyarrow==18.1.0` (Apache-2.0) via `scripts/generate-parquet-fixtures.py` | committed fixtures under `tests/ProofShift.Connectors.RemoteStorage.Tests/fixtures/parquet` | `Parquet.Net` 6.1.0 (MIT) |

MinIO is AGPL-licensed software used only as a disposable test dependency; it is not linked, bundled or redistributed. Real Amazon S3, real Azure and a production SFTP server were **not** exercised: S3 behavior is verified against MinIO and Azure behavior against Azurite, which differ from the real services in documented ways (for example Azurite reports a bad shared key as an authorization failure and does not enforce every service limit). The remote-storage CI job is required and has no skip-on-unavailable path beyond the repository-wide Docker-unavailable skip convention.

Connector validation is against the repository's MinIO, Azurite 3.35.0 and OpenSSH-backed SFTP test environments. SFTP directory traversal streams entries into bounded chunks and externally merges sorted runs to preserve global UTF-8 ordering; the exercised integration forces multiple runs and checks entry/metadata-buffer limits. This is not a constant-memory or unlimited-directory-size claim, nor AWS-certified, Azure-certified or production-SFTP-certified validation. Azurite may classify invalid shared-key credentials as authorization rather than a distinct authentication failure; ProofShift retains redacted diagnostics and fail-closed handling for either classification.

## CI

`ci.yml` runs core tests separately from required `oracle-integration` and `db2-integration` jobs. Oracle is built at test time through the official Oracle source repository and official Free download endpoint. Db2 uses the pinned Community image and explicit test-time license acceptance. Neither vendor suite is part of a skip-on-unavailable path.

## Capability Matrix

Capabilities below reflect the CLI connector catalog. Target observation is performed through a reader and does not imply write authority. Only PostgreSQL and filesystem are registered with shadow writers.

| Connector | Discovery | Read | Checkpoint | Target observation | Shadow write |
| --- | --- | --- | --- | --- | --- |
| SQL Server (`sqlserver`) | Yes | Yes | Yes | Yes | No |
| PostgreSQL (`postgres`) | Yes | Yes | Yes | Yes | Yes |
| Oracle (`oracle`) | Yes | Yes | Yes | Yes | No |
| Db2 LUW (`db2`) | Yes | Yes | Yes | Yes | No |
| CSV (`csv`) | Yes | Yes | Yes | Yes | No |
| Filesystem (`files`) | Yes | Yes | Yes | Yes | Yes |
| Fixed-width (`fixed-width`) | Yes | Yes | Yes | Yes | No |
| JSON (`json`) | Yes | Yes | Yes | Yes | No |
| NDJSON (`ndjson`) | Yes | Yes | Yes | Yes | No |
| XML (`xml`) | Yes | Yes | Yes | Yes | No |
| S3 / S3-compatible (`s3`) | Yes | Yes | Yes | Yes | No |
| Azure Blob (`azure-blob`) | Yes | Yes | Yes | Yes | No |
| SFTP (`sftp`) | Yes | Yes | Yes | Yes | No |
| Parquet (`parquet`, over filesystem / S3 / Azure Blob / SFTP) | Yes | Yes | Yes | Yes | No |

Connector-owned JSON Schemas and strict CLI validation are covered for Oracle, Db2, fixed-width, JSON, NDJSON and XML. Authoring tests cover closed endpoint/selector properties, required settings and alternatives, selector kind, enum/range values, fixed-width boundaries and actionable configuration paths.

### Parquet Row-Group Scale Probe

The direct `Uneven_row_groups_are_read_at_scale_without_claiming_constant_memory` test read the Arrow-written `uneven-row-groups.parquet` fixture through the filesystem transport: 14,409,454 bytes, 320,000 rows, 21 row groups, largest group 120,000 rows (12 times each 10,000-row group), and 6.207 seconds elapsed in the recorded focused run. Process RSS was 65,421,312 bytes at test start, 139,280,384 bytes at test end, and 140,316,672 bytes process-lifetime peak. At the tested scale, no evidence of whole-input materialization was observed. RSS peak is a process high-water mark, not a continuous test-only sample; this observation is not a constant-memory or production-scale guarantee.

## Reader Measurements

These repeatable synthetic measurements are diagnostic snapshots, not SLAs. The read timer starts after database startup and fixture provisioning. Database `Fixture bytes` is `0` because rows are stored in the server; `Payload estimate` is the UTF-8 byte count of rendered ID and decimal values, excluding database page/row framing. File `Fixture bytes` is the exact generated file size and is also used as its payload-byte estimate. Peak RSS is for the ProofShift test process only, sampled every 10 ms; database-container memory is excluded. Managed heap values are start/end snapshots, not peak heap. No checkpoint capture is included in the timed read.

| Connector / format | Records | Fixture bytes | Payload estimate bytes | Read seconds | Records/sec | RSS start / end / peak MiB | Managed heap start / end MiB | Startup seconds | Fixture provision seconds |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Oracle Database Free 26ai 23.26.0 | 20,000 | 0 | 259,018 | 0.3473 | 57,595 | 127.1 / 141.6 / 141.6 | 23.4 / 24.2 | 219.57 | 6.11 |
| Db2 LUW Community 11.5.9.0 | 20,000 | 0 | 259,018 | 0.2749 | 72,759 | 124.9 / 127.0 / 127.0 | 13.0 / 14.7 | 239.91 | 91.06 |
| Fixed-width | 50,000 | 850,000 | 850,000 | 1.1681 | 42,805 | 78.5 / 90.2 / 90.9 | 13.6 / 6.6 | N/A | N/A |
| NDJSON | 20,000 | 1,768,894 | 1,768,894 | 0.5529 | 36,170 | 91.2 / 96.9 / 96.9 | 11.6 / 7.7 | N/A | N/A |
| XML | 5,000 | 243,906 | 243,906 | 0.1739 | 28,758 | 96.7 / 98.2 / 98.2 | 9.7 / 7.6 | N/A | N/A |

Oracle used `Oracle.ManagedDataAccess.Core` 23.26.301; Db2 used IBM provider 10.0.0.300 for the local Windows x64 run. These samples show no evidence of whole-input materialization at the measured file scales; a single run is not proof of constant memory. Database startup and provisioning durations are reported separately and never included in connector read throughput.

The final full local solution run completed with 183 passed, 0 failed and 2 Windows symlink-capability skips (185 total); Oracle and Db2 each passed 2 tests with 0 skips. Remote GitHub Actions [run 37416501703](https://github.com/banderson736/ProofShift/actions/runs/37416501703) passed all three required jobs with 185 passed, 0 failed and 0 skipped. The Db2 job sets `LD_LIBRARY_PATH` to IBM’s package-provided `clidriver/lib` directory so Linux can load `libdb2.so`.
