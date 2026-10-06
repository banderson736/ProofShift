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

Connector-owned JSON Schemas and strict CLI validation are covered for Oracle, Db2, fixed-width, JSON, NDJSON and XML. Authoring tests cover closed endpoint/selector properties, required settings and alternatives, selector kind, enum/range values, fixed-width boundaries and actionable configuration paths.

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

The final full local solution run completed with 183 passed, 0 failed and 2 Windows symlink-capability skips (185 total); Oracle and Db2 each passed 2 tests with 0 skips. The required remote CI run has not yet been recorded.