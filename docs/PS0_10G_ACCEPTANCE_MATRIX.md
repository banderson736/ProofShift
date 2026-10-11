# PS-0.10G Demo & Product Packaging Acceptance Matrix

Status: In Progress pending formal review; all 60 gates PASS (0 FAIL, 0 NOT YET). Accepted baseline: PS-0.10F merge `77bceb325d925eb41c44b0eb7e3d3e5fb74542b9`. Exact candidate `d3529ccabe04eb1e7b8b3cf2ab643ff8488cf1af` passed all six jobs in push-triggered GitHub Actions [run 38172351820](https://github.com/banderson736/ProofShift/actions/runs/38172351820), completed in 22m03s. The CI regression jobs passed 331 tests, 0 failed, 0 skipped; both package artifacts were uploaded. The PR-synchronization run 38172355964 also passed on GitHub's synthetic merge ref; candidate-specific package hashes below are from the exact-SHA push run. Windows claims remain package-only and Ubuntu claims remain limited to packaged Docker evaluation, not general Linux product support. Do not self-accept or merge before the normal review workflow. Do not begin PS-1.0 or out-of-scope product/production work.

| # | Acceptance item | Status | Evidence or gap |
|---:|---|---|---|
| 1 | PS-0.10F accepted status synchronized | PASS | README, roadmap, status instructions, 0.10F matrix/index, Decisions, and ADR-0027/0028 record acceptance on 2026-10-11 and merge `77bceb325d925eb41c44b0eb7e3d3e5fb74542b9`. |
| 2 | Generic demo scenario contract | PASS | Strict v1 contract covers pack/version, claim boundary, requirements, systems, accepted-index reference, ordered stages, typed result assertions, reports, and scoped cleanup; a display-name change is tested not to alter the project graph hash. See [Demo Scenario Manifest](demo/DEMO_SCENARIO_MANIFEST.md) and [ADR-0029](adr/0029-generic-demo-scenario-contract.md). |
| 3 | Deterministic scenario enumeration | PASS | CLI process test builds a temporary two-manifest catalog and verifies ordinal listing and byte-stable repeated JSON output. |
| 4 | Scenario describe | PASS | CLI process test verifies pack/domain metadata, claim boundary, and accepted defective/corrected expectations resolve from the 0.10F index. |
| 5 | Requirements preflight | PASS | CLI tests verify missing fixture and occupied-port diagnostics; implementation checks .NET SDK, Docker when required, disk, ports, project/index, and fixture paths. |
| 6 | Safe scenario reset | PASS | Marker-owned `.proofshift/demos/<id>` reset refuses unowned paths, preserves unrelated scenario data, rejects reparse points, and has host-guarded symlink coverage. That test skips here because Windows denies symlink creation. Project state is materialized inside the owned workspace. |
| 7 | Reset/repeat behavior | PASS | CLI process test runs, refuses overwrite before reset, resets, reruns, compares stable stage results, and proves the referenced project is copied/used inside the scenario workspace. |
| 8 | Non-interactive demo lifecycle | PASS | Ordered manifest stages launch ProofShift with argument lists, timeouts, and typed JSON assertions; no shell or prompts. |
| 9 | Expected defective result and exit codes | PASS | Expected nonzero stage must persist JSON and satisfy assertions; an undeclared nonzero exit fails. Stable CLI/runtime, blocked-preflight, expected-defect, and success outcomes use distinct exits. |
| 10 | Pension scenario discoverable | PASS | `demo list`, `describe pension`, and `preflight pension` resolve the manifest and accepted `pension-fast` reference. |
| 11 | Pension exact defective assertions | PASS | Full Docker-backed run asserts 8,495 checkpoint artifacts, 8,595 targets, and exactly 149 defective findings against the accepted index. |
| 12 | Pension NOT QUALIFIED presentation | PASS | Default output states 149 defective business discrepancies and `NOT QUALIFIED`; the persisted defective report asserts 149. |
| 13 | Pension Evidence output | PASS | Scenario reports are persisted in the owned workspace; Evidence is verified by the accepted physical fixture and its run-linked report. |
| 14 | Pension false-Reverse presentation | PASS | False-Reverse report asserts Verification `PASSED` with 0 Verification failures, two rejected Reverse edges, and Recovery qualification `NOT QUALIFIED`. The report taxonomy separately counts those two Recovery findings. |
| 15 | Pension corrected zero findings | PASS | Corrected report asserts 0 discrepancies, 0 Verification failures, 0 unaccounted sources, and 0 unexplained targets. |
| 16 | Pension Recovery PASS | PASS | Corrected report asserts Recovery rehearsal `Passed`; the accepted scenario index binds the PASS assessment. |
| 17 | Pension QUALIFIED result | PASS | Corrected fixture and persisted report both assert `QUALIFIED`. |
| 18 | Pension repeatability | PASS | Two complete Docker-backed runs succeeded with an ownership-scoped reset between them; the second default-text run reproduced the same semantic counts and all four report stages. |
| 19 | Utility demo kit | PASS | `demo run utility` executed the accepted physical fixture and completed its owned-workspace output stage. |
| 20 | Utility exact accepted assertions | PASS | Structured result asserts 14 seeded defects / 26 findings and accepted 10,200 source/target artifacts. |
| 21 | Utility corrected qualification | PASS | Structured result asserts 0 corrected failures/accounting/lineage gaps, Recovery/rehearsal `PASSED`, `QUALIFIED`. |
| 22 | Justice demo kit | PASS | `demo run justice` executed the accepted physical fixture and completed its owned-workspace output stage. |
| 23 | Justice exact accepted assertions | PASS | Structured result asserts 15 seeded defects / 24 findings and accepted 16,200 source/target artifacts. |
| 24 | Justice corrected qualification | PASS | Structured result asserts 0 corrected failures/accounting/lineage gaps, Recovery/rehearsal `PASSED`, `QUALIFIED`. |
| 25 | HCM demo kit | PASS | `demo run hcm` executed the accepted physical fixture and completed its owned-workspace output stage. |
| 26 | HCM exact accepted assertions | PASS | Structured result asserts 14 seeded defects / 25 findings and accepted 40,575 source/target artifacts. |
| 27 | HCM corrected qualification | PASS | Structured result asserts 0 corrected failures/accounting/lineage gaps, Recovery/rehearsal `PASSED`, `QUALIFIED`. |
| 28 | FHIR demo kit | PASS | `demo run fhir` executed the accepted physical fixture and completed its owned-workspace output stage. |
| 29 | FHIR exact accepted assertions | PASS | Structured result asserts 9 seeded defects / 15 findings and accepted 7,000 source/target artifacts. |
| 30 | FHIR corrected qualification | PASS | Structured result asserts 0 corrected failures/accounting/lineage gaps, Recovery/rehearsal `PASSED`, `QUALIFIED`. |
| 31 | Domain claim boundaries retained | PASS | All five manifests bind their accepted pack/version and claim boundary to the 0.10F index; descriptions retain each domain's stated exclusions. |
| 32 | External observation distinct from Projection | PASS | All compact kit descriptions and preparation metadata distinguish independently populated external targets from a separate corrected shadow run; ProofShift is not described as performing the external migration. |
| 33 | Source accounting presentation | PASS | Pension repeats exact 8,495 checkpoint sources; all four cross-domain human reports show accounted/checkpoint totals and zero corrected accounting gaps. |
| 34 | Target lineage presentation | PASS | Pension asserts 8,595 corrected targets and zero unexplained targets; all four cross-domain reports show lineaged/target totals and zero corrected lineage gaps. |
| 35 | Evidence navigation | PASS | Every cross-domain kit runs `evidence --run` on the defective external observation and asserts matching run ID/fingerprint; human reports list stable failed-record Evidence IDs/codes without values. Pension defective/corrected/false-Reverse reports and accepted fixture persist integrity-checked Evidence. |
| 36 | Generic human-readable report | PASS | Utility, Justice, HCM, and FHIR-style each generated `assurance-report.txt` through the generic report renderer and the clean physical demo path. |
| 37 | Defective report shows NOT QUALIFIED and why | PASS | Pension default output/report states 149 discrepancies and `NOT QUALIFIED`; all cross-domain reports separate the failed external observation from the corrected qualified shadow, with exact rule/code findings and Evidence references. |
| 38 | Corrected report shows zero gaps and QUALIFIED | PASS | All five demo paths assert corrected zero Verification/accounting/lineage gaps and corrected `QUALIFIED` Recovery. |
| 39 | Machine-readable report/output | PASS | All four cross-domain kits persist schema-versioned JSON assurance reports plus Evidence and Recovery inspection JSON; Pension persists the accepted summary, reports, and comparison JSON. |
| 40 | Pack-correct report terminology | PASS | Generic reports bind pack ID/version/display name and pack-specific claim boundary; the Pension report remains Pension-specific. |
| 41 | CLI help coverage | PASS | `DemoHelpListsLifecycleCommands` asserts list/describe/preflight/run/reset help; existing process tests cover JSON mode and invalid/blocked cases. |
| 42 | CLI version output | PASS | `--version` and `version` return `ProofShift PS-0.10G`; process coverage and the extracted package smoke verified the published CLI. |
| 43 | Actionable operator diagnostics | PASS | Preflight tests distinguish missing fixture/occupied port; failed stage preserves structured exit diagnostics and marker-owned workspace; default runs list named phases, facts, reports, and captured logs. |
| 44 | Secret redaction preserved | PASS | Focused configuration/runtime/remote-authoring regression: 26 passed, 0 failed. The exact-candidate regression result is recorded in row 57. |
| 45 | Sample projects validate with normal parser | PASS | All five packaged root project configurations (Pension PS-0.10B, Justice, HCM, Pension PS-0.5, and Pension false-Reverse) validate from the extracted CLI using only explicit synthetic placeholders for declared environment references. False-Reverse retains its existing `PSRULE012` warning. |
| 46 | Evaluator guide | PASS | [Evaluation guide](EVALUATION.md) documents package requirements, commands, scope, source-free package contents, provenance, and limitations. |
| 47 | Deterministic presenter runbook | PASS | [Demo runbook](DEMO_RUNBOOK.md) gives ordered presenter, reset/repeat, Evidence, and claim-boundary instructions. |
| 48 | Reproducible evaluation package build | PASS | On exact candidate `d3529ccabe04eb1e7b8b3cf2ab643ff8488cf1af`, two Windows builds matched at SHA-256 `c0052774a310724fe4ae224ffc9b7b66d294701872e11aa9527f2f49005d0987`; two Linux builds matched at `d4378cacedd104d60a93474d4b9d4c58cd2ca951bb026adc455af607a4e4c057`. Reproducibility is asserted within each platform; cross-platform hashes are not expected to match. |
| 49 | Package content allowlist/denylist | PASS | Extracted archive contains no `.cs`, `.csproj`, `.pdb`, local `.proofshift`, `.git`, or developer runtime config; only the compiled MTP fixture module is distributed. |
| 50 | Third-party notices | PASS | Generator reads the actual resolved CLI and fixture-module NuGet graphs; final package contains 28 upstream license/notice text files and no unresolved license metadata entries. |
| 51 | Fresh Windows package acceptance | PASS | Exact-candidate `windows-package-smoke` passed in run 38172351820, job 114569747896: two reproducible builds, extraction, provenance/platform/content checks, samples/notices, and package-only CLI smoke. Root `proofshift --help` usage is validated with its existing exit code 2; `demo --help` succeeds. This job does not run Docker-backed demos. |
| 52 | General Linux product support if claimed | PASS | General-purpose Linux product support remains unclaimed. The separate Ubuntu Docker-backed packaged-evaluation CI row does not broaden that product claim. |
| 53 | Fresh-runner Pension flagship | PASS | The exact-candidate Linux package smoke passed the Pension physical fixture, reports, Evidence/Recovery checks, exact 149 defective / 0 corrected semantics, and reset/repeat semantic comparison on a clean `ubuntu-latest` runner; see run 38172351820, job 114569747953. |
| 54 | Fresh-runner compact cross-domain demos | PASS | The same clean-runner smoke passed Utility, Justice, HCM, and FHIR-style packages, including exact defective/corrected outcomes, human/JSON reports, Evidence, Recovery, and pack-specific report boundaries. All five packaged scenarios passed; see run 38172351820, job 114569747953. |
| 55 | No hidden developer state | PASS | Fresh-runner smoke ran from the extracted package outside the checkout with `DOTNET_CLI_HOME` and `NUGET_PACKAGES` isolated under the extraction root. Package denylist checks reject `.git`, `.proofshift`, developer state, source/project files, and local runtime data; both platform jobs passed. |
| 56 | Measured resource guidance | PASS | Exact-runner OS/CPU/memory/disk, Docker capacity/storage, package sizes, and per-fixture elapsed/peak-working-set observations were captured. The snapshots and fixture measurements are listed under Fresh-Runner Measurements below. They are run-specific observations, not an SLA, general product-support claim, or whole-Docker-stack memory guarantee. |
| 57 | Full regression | PASS | Exact candidate run 38172351820 passed `build-and-test` (274/274), Oracle (2/2), Db2 (2/2), and RemoteStorage (53/53): 331 passed, 0 failed, 0 skipped across the four regression jobs. |
| 58 | Split package/demo CI | PASS | Exact candidate run 38172351820 passed `windows-package-smoke` and `linux-package-demos`; Ubuntu verified Linux Docker preflight, reproducible package builds, all five extracted-package scenarios, post-demo Docker/disk state, and artifact upload. All six workflow jobs succeeded. The FHIR fix only corrected the smoke harness's comparison between report text and structured report boundary; the manifest boundary is independently checked through packaged `demo describe`. |
| 59 | Final candidate SHA and package artifacts | PASS | Exact tested candidate: `d3529ccabe04eb1e7b8b3cf2ab643ff8488cf1af`. Run 38172351820 uploaded Windows package (159,948,253 bytes; reproducible archive SHA-256 `c0052774a310724fe4ae224ffc9b7b66d294701872e11aa9527f2f49005d0987`; artifact digest `5aa53873cdec624532c31b61429d555f1a2ece314ed6dcf1b4a3c02232dd35ea`) and Linux package (154,554,261 bytes; reproducible archive SHA-256 `d4378cacedd104d60a93474d4b9d4c58cd2ca951bb026adc455af607a4e4c057`; artifact digest `79704b4a1dca7d5d6e1f6de717c73e8a6e9b79837b86324ea9e7ecd37feb1c28`). Artifact names include the exact candidate SHA. The PR-sync run used GitHub's synthetic merge ref and is supplementary, not the source of these candidate artifacts. |
| 60 | PS-1.0 and production migration/rollback remain unstarted | PASS | Current assignment is PS-0.10G only; no production migration execution or rollback is authorized. |

## Fresh-Runner Measurements

Captured by exact-candidate run [38172351820](https://github.com/banderson736/ProofShift/actions/runs/38172351820). Runner memory and disk values are system snapshots, not workload peak measurements. Fixture working sets are measured fixture-process peaks, not total Docker or runner memory.

| Platform | Clean-runner and Docker snapshot | Package |
|---|---|---|
| Windows package-only | Windows Server 2025 Datacenter 10.0.26100; AMD EPYC 7763, 4 logical CPUs; visible memory 17,174,360,064 bytes; free D: disk 157,822,287,872 bytes before packaging and 156,513,628,160 after smoke. | 159,948,253 bytes; archive SHA-256 `c0052774a310724fe4ae224ffc9b7b66d294701872e11aa9527f2f49005d0987`. |
| Ubuntu packaged demos | `ubuntu-latest`, Ubuntu 24.04 runner image `20261004.327`; AMD EPYC 7763, 4 CPUs; system/Docker memory capacity 16,766,410,752 bytes; free disk 92,289,646,592 bytes before and 87,889,448,960 after demos. Docker 28.0.4 reported Linux; post-demo image store 3.735 GB total / 3.733 GB reclaimable, one container, no local volumes or build cache. | 154,554,261 bytes; archive SHA-256 `d4378cacedd104d60a93474d4b9d4c58cd2ca951bb026adc455af607a4e4c057`. |

The extracted Linux smoke JSON recorded these physical fixture stage measurements:

| Scenario | Fixture elapsed milliseconds | Fixture-process peak working set bytes |
|---|---:|---:|
| FHIR | 49,925 | 235,307,008 |
| HCM | 228,528 | 270,729,216 |
| Justice | 58,318 | 258,699,264 |
| Pension | 81,250 | 299,753,472 |
| Utility | 46,732 | 252,882,944 |

These values describe the measured fixture-process stage on this hosted runner only. They do not establish a product SLA, whole-stack memory ceiling, or general Linux support.

## G1 Evidence

- The focused CLI build passes with warnings treated as errors.
- `DemoScenarioCliTests` passes 8 tests with 0 failures; 1 symlink-capability test is skipped because the current Windows host disallows symbolic-link creation. Coverage includes later-stage result-binding expansion, readable binding labels, and structured failures when a declared token is unavailable.
- Tests exercise deterministic ordinal listing/JSON, describe bound to the accepted 0.10F scenario index, missing-fixture and occupied-port preflight, accepted stage outcomes and exact JSON assertions, refusal of unasserted expected-defect stages, project materialization, graph-hash neutrality for display changes, marker-owned reset, unrelated data preservation, reset/run repeatability, and path boundaries.
- G2 adds the Pension flagship; G3 adds compact cross-domain kits. G1 PASS is limited to the generic manifest catalog and lifecycle foundation.

## G2 Evidence

- The Pension manifest references accepted `pension-fast` and is validated against its identity and artifact counts. It runs the accepted physical test in an isolated marker-owned workspace, asserts exact summary and report values, captures runner output separately from structured results, and binds run IDs into later ProofShift report/compare stages.
- Two complete local Docker-backed `demo run pension` executions passed. Each ran the SQL Server/PostgreSQL fixture, the false-Reverse report, defective and corrected reports, and defective-to-corrected comparison. A scoped reset separated the clean repeat.
- The repeat's human-readable output presents 149 defective findings/`NOT QUALIFIED`, corrected 0 discrepancies/0 unaccounted/0 unexplained/`QUALIFIED`, and false-Reverse Verification `PASSED` with zero Verification failures while Recovery rejects two edges. It lists the machine-readable summary and all report paths without dumping finding records.
- Local implementation validation: CLI build passed; focused CLI tests passed 8, failed 0, skipped 1 (Windows symlink capability); Pension report aggregation passed 1/1; two full physical demo runs passed. Later exact-candidate regression/vendor results are recorded under G7.

## G3 Evidence

- Utility, Justice, HCM, and FHIR-style manifests reuse their accepted PS-0.10F physical fixtures. Each is fixture-owned and does not borrow a different domain's project configuration. The fixtures write a generic machine/human assurance report only when launched with the runner's workspace-confined result path; ordinary test runs remain unchanged.
- All four `demo run` commands passed with assertions against accepted artifact counts, exact seeded defect/finding counts, corrected failures, source-accounting and target-lineage gaps, Recovery/rehearsal status, and `QUALIFIED`. The human report separates external observation from corrected shadow Projection/Recovery, lists Evidence references, and omits raw values.
- Local results: Utility 10,200 artifacts / 14 defects / 26 findings; Justice 16,200 / 15 / 24; HCM 40,575 / 14 / 25; FHIR-style 7,000 / 9 / 15. Every corrected run asserted zero failures/accounting gaps/lineage gaps and Recovery `PASSED` / `QUALIFIED`.
- All four accepted-index/Docker/SDK preflights passed during local implementation validation. Later exact-candidate regression/vendor results are recorded under G7.

## G4 Evidence

- Added a domain-neutral `DemoAssuranceReport` with pack/version/claim binding, checkpoint totals, two explicitly read-only external observations, corrected shadow Verification/Recovery, source accounting, target lineage, stable Evidence IDs/codes/severity, and run/fingerprint references. The report has deterministic JSON and invariant-culture, value-free human-readable output.
- Each cross-domain physical fixture writes the JSON report and `.txt` sidecar only when the demo runner supplies its workspace-confined result path. Demo runs retain only the report and `.proofshift` Evidence/Recovery artifacts under the marker-owned workspace; normal test runs keep unique temporary roots and clean them.
- Utility, Justice, FHIR-style, and HCM each completed their final full Docker-backed report flow: exact report assertions, defective Evidence integrity/run/fingerprint inspection, and corrected Recovery CLI output (`QUALIFIED`, rehearsal `PASSED`, 0 failed edges). Their text reports list exact source accounting, target lineage, pack terminology, and Evidence IDs without expected/actual values.
- Pension retained its generic lifecycle output with exact accepted summary bindings, defective/corrected/false-Reverse report assertions, Recovery distinction, and comparison output. Added `demo help` process coverage. Focused CLI suite: 10 passed, 0 failed, 1 expected Windows symlink-capability skip.
- At the time of the local G4 implementation checks, the full solution/vendor/remote CI gates had not yet run. Their latest historical results are recorded under G7; the split clean-runner package campaign has not yet executed.

## G5 Evidence

- Added `scripts/package-evaluation.ps1`. It restores/builds the CLI and EndToEnd fixture module, publishes the CLI without debug symbols, and creates a source-free ZIP with fixed entry order/timestamps, a package manifest/content digest, and a SHA-256 sidecar. Package-only manifest conversion replaces repository `dotnet-test` project stages with bounded direct MTP binary-module stages; checked-in manifests remain source-project based for development.
- The allowlist includes the published CLI/runtime assets, compiled MTP fixture module/runtime assets, accepted scenario configs/manifests, evaluation docs, license, and generated third-party notices. The enforced denylist rejects source/project/PDB files, `.runtimeconfig.dev.json`, `.git`, `.proofshift`, `obj`, and build/test output directories; required `clidriver/bin` package runtime files are included and covered by the resolved dependency notices/digest. No vendor database images are included.
- Two independent final4 builds from unchanged inputs using the guarded stable staging path produced identical ZIP SHA-256 `6b72a2b54b3173f63467d97a8db8262867a29be1a2c108c8786798e0cfb763aa`. The package manifest records the accepted source HEAD plus `sourceWorktreeClean=false` and the exact content digest; this is not a final candidate build. The extracted archive contained zero denied source/debug/developer-state files, five binary-only demo runners, and 28 third-party license/notice texts with no unresolved license metadata.
- From a clean short Windows extraction root, the final package passed `--version`, catalog/preflight, and Pension/Justice/HCM normal-parser sample validation. Its FHIR physical demo passed exact 9-defect / 15-finding / corrected `QUALIFIED` assertions plus all report/Evidence/Recovery stages. Deep Windows extraction paths can exceed SQLite's legacy file-path limit; `docs/EVALUATION.md` specifies a short extraction path.
- Exact-candidate run 38172351820 passed Windows package-only smoke and Ubuntu Docker preflight, two reproducible Linux builds, and all five extracted-package demos. The FHIR human report is checked against its own structured canonical boundary, while the scenario manifest boundary is independently validated through packaged `demo describe`. All six jobs and both artifact uploads succeeded; acceptance remains In Progress pending formal review.

## G6 Evidence

- Extracted the final4 binary-only ZIP to `C:\PS0G-Final4` on the existing Windows rehearsal host, not a separate physical machine. The archive had no source, project, PDB, repository, local `.proofshift`, or developer-runtime files; all commands used the packaged CLI/module and package-root inputs.
- All five packaged physical demos passed, including their human/JSON report, Evidence, and Recovery stages: Pension 8,495 checkpoint artifacts / 8,595 targets / 149 defective findings / corrected 0; Utility 10,200 artifacts / 14 defects / 26 findings; Justice 16,200 / 15 / 24; HCM 40,575 / 14 / 25; FHIR-style 7,000 / 9 / 15. Each corrected shadow qualified with zero Verification/accounting/lineage gaps. Pension false-Reverse Verification passed with 0 failures while Recovery rejected two unsafe Reverse edges.
- The final4 ZIP SHA-256 is `6b72a2b54b3173f63467d97a8db8262867a29be1a2c108c8786798e0cfb763aa`; two builds from unchanged inputs matched. Its provenance reports the accepted source HEAD and `sourceWorktreeClean=false`, so it is rehearsal evidence, not a final candidate package.
- Exact-candidate run 38172351820 passed all five packaged physical demos on clean `ubuntu-latest`; fixture-stage elapsed time and peak working set are recorded in Fresh-Runner Measurements above. Windows package-only smoke passed separately on `windows-latest`. General-purpose Linux product support remains unclaimed.

## G7 Local Regression Evidence

- `dotnet build ProofShift.sln --no-restore` succeeded. All 9 test projects used by the CI non-vendor loop passed: 271 passed, 0 failed, 3 skipped (Windows host disallowed symlink creation), 274 total. EndToEnd alone passed 138, skipped 3, failed 0, total 141.
- One follow-up invocation exited 5 after discovering zero tests; do not treat it as successful validation. The subsequent authoritative discovery check found all 141 EndToEnd tests, and the successful complete EndToEnd run (138 passed, 3 documented Windows symlink-capability skips, 0 failed) is the acceptance evidence. The failed invocation did not enable `--diagnostic`, so no MTP trace was produced; its non-reproduced cause remains unknown.
- Local RemoteStorage passed 53/53, Oracle 2/2, and Db2 2/2, all with 0 skips. Combined with the non-vendor run, local test totals are 328 passed, 0 failed, and 3 documented Windows symlink-capability skips. Exact-candidate run 38172351820 passed `build-and-test` (274), RemoteStorage (53), Oracle (2), and Db2 (2): 331 passed, 0 failed, 0 skipped. Windows package-only and all five Ubuntu package demos also passed. The former Windows-Docker blocker and FHIR harness mismatch are resolved within the authorized split-workflow design; formal review is pending.
