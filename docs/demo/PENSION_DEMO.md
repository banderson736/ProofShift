# Public Pension Assurance Demo

## Purpose

This synthetic scenario demonstrates how an independent IV&V team can verify a conversion performed by another migration vendor. ProofShift is not required to execute the conversion. It checkpoints source state, observes target state, compares pension semantics, preserves evidence, and evaluates recovery readiness before a production cutover decision.

All generated data is synthetic. It names no real pension system or agency.

## Legacy and target representations

The clean generator emits legacy-style records for members, employment intervals, contributions, service credits, beneficiaries, retirement elections, benefit payments, documents, and historical exports. `PensionTargetDataModel` emits a different target vocabulary: participant/display-name/status, employment state events, contribution/payment transaction names, relationship rows, target option codes, and object-key document references. Employment history is represented as intervals in the source and JOINED/TERMINATED/REINSTATED events in the target.

The current generator exports one CSV per concept. It does not yet create or populate the planned SQL Server legacy database, PostgreSQL target schema, or document payload directories. The existing `ps05` SQL Server/PostgreSQL Docker scenario remains the runnable physical projection/recovery demonstration and is not the full PS-0.9 pension vertical.

## Generate the v1 data

From the repository root:

```powershell
dotnet run --project src/ProofShift.Cli -- demo generate .proofshift/pension-demo --scale fast --seed 20261003
```

The output has three data sets:

- `source/`: clean synthetic legacy records;
- `target-corrected/`: clean, structurally different target records;
- `target-defective/`: corrected target records after deterministic defect injection.

`demo-manifest.json` records generator, target-model, and defect-set versions, seed, scale, source fingerprint, record counts, and exact defect manifest. Generation refuses to overwrite a non-empty output directory. Use `--scale large` to stream the specified five-million-contribution target fixture; this is opt-in and has not been benchmarked.

The repeatable operator entry point is:

```powershell
./scripts/pension-demo.ps1
```

Add `-WithDocker` to include the existing physical projection/recovery CLI scenarios after Docker and the PS-0.5 connection environment are configured.

## Exercise the semantic rules

The CI-scale test suite creates source and externally populated target observations independently, resolves rules through `PensionPack`, builds an Evidence Graph, and asserts every record-level defect count exactly:

```powershell
dotnet test --project tests/ProofShift.EndToEnd.Tests/ProofShift.EndToEnd.Tests.csproj --filter-class ProofShift.EndToEnd.Tests.PensionSyntheticDataTests --no-restore
dotnet test --project tests/ProofShift.EndToEnd.Tests/ProofShift.EndToEnd.Tests.csproj --filter-class ProofShift.EndToEnd.Tests.PensionSemanticRuleTests --no-restore
```

The clean semantic test accepts interval/event employment equivalence, decimal contribution/payment reconciliation, and regrouped service-credit periods with the same total. The defect test proves the configured rule provider detects the exact rule-level counts and produces structured, fingerprinted evidence without calling Projection.

| Defect category | Expected | Detected | Clean false positives |
|---|---:|---:|---:|
| Missing members | 7 | 7 | 0 |
| Duplicate members | 4 | 4 | 0 |
| Wrong member statuses | 3 | 3 | 0 |
| Missing employment periods | 12 | 12 | 0 |
| Incorrect employment dates | 8 | 8 | 0 |
| Incorrect service-credit totals | 17 | 17 | 0 |
| Missing contributions | 39 | 39 | 0 |
| Duplicate contributions | 8 | 8 | 0 |
| Incorrect contribution amounts | 6 | 6 | 0 |
| Benefit-payment discrepancies | 11 | 11 | 0 |
| Broken beneficiary relationships | 6 | 6 | 0 |
| Wrong-member beneficiaries | 2 | 2 | 0 |
| Retirement-election mappings | 3 | 3 | 0 |
| Code transformations | 5 | 5 | 0 |
| Missing documents | 9 | 9 | 0 |
| Wrong-member documents | 4 | 4 | 0 |
| Missing historical exports | 3 | 3 | 0 |
| False-Reverse declarations | 2 | 2 analyzer rejections | 0 |

The clean false-positive count is asserted as zero by evaluating all configured rules against the corrected target before defects are injected. The false-Reverse cases are separately passed through the generic Recovery transformation-loss analyzer; they are not yet run as Recovery edges in the complete scenario.

The rule-level external-target fixture is not yet a complete `VerificationService` run: full-service verification currently binds to a completed checkpoint, projection manifest, journal, and connector read-back. Composing a PS-0.9 external target adapter/fixture is still an acceptance item.

## Existing physical shadow demonstration

After configuring the Docker-backed PS-0.5 fixture as described in `scenarios/pension-modernization/README.md`, run:

```powershell
dotnet run --project src/ProofShift.Cli -- dry-run scenarios/pension-modernization/ps05/proofshift.yaml --json
```

Use its `dryRunId` to inspect the stored reports:

```powershell
dotnet run --project src/ProofShift.Cli -- report scenarios/pension-modernization/ps05/proofshift.yaml --run <dry-run-id>
dotnet run --project src/ProofShift.Cli -- compare scenarios/pension-modernization/ps05/proofshift.yaml --before <dry-run-id> --after <dry-run-id>
```

That physical fixture is the accepted 10-member projection/recovery slice. Its results must not be presented as the 149-defect PS-0.9 full-system scenario.

## 10-15 minute operator flow

1. Show `demo-manifest.json`, generator version, seed, scale, and the distinct source/target CSVs.
2. Point out the source employment intervals and target event representation.
3. Run the generator test and show fast-scale counts.
4. Run the semantic rule test; show the exact defective finding counts and clean equivalent-history pass.
5. Inspect one timeline exception and its evidence references in the test/debugger.
6. Inspect a financial exception; evidence uses group fingerprints, counts, totals, difference, and tolerance.
7. Inspect beneficiary/document exceptions and their evidence-backed ownership/hash fields.
8. Run the physical PS-0.5 Docker dry run when its environment is configured.
9. Open the `report` command output and point to source accounting, lineage, recovery status, and fingerprints.
10. Compare the same physical dry run with itself to show a zero-delta control, then compare separately persisted runs during remediation work.
11. Close by distinguishing shadow technical qualification from business approval or production authorization.

## What the evidence demonstrates

- Pension rules are pack-owned and resolved by the existing versioned rule registry.
- Rules consume normalized source/expected/observed target records from a generic disk-backed Verification workspace.
- Records stream in semantic-key order, so transaction and history rules aggregate one group at a time.
- Raw member names and record values are not emitted as finding payloads; evidence references and safe fingerprints bind findings to the run.
- The report consumes persisted Verification evidence plus a Recovery assessment that pins the same Verification Evidence fingerprint.

## Remaining demo acceptance

PS-0.9 is still in progress. The generated data has not yet been integrated into a full SQL Server-to-PostgreSQL-and-document dry run. The v1 false-Reverse count is manifest data but the two false inverses are not yet present as executable graph edges. The report/comparison commands, high-volume scale benchmark, physical external-target VerificationService scenario, archive/excluded document disposition, and complete source/target schemas still need end-to-end acceptance coverage.
