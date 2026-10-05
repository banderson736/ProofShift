# Reviewed Synthetic Pension Assurance Project

This is the complete accepted synthetic Pension mapping/rule/recovery scenario expressed as ordinary referenced configuration. `proofshift.yaml` uses recoverable Restore declarations; `proofshift-false-reverse.yaml` selects the deliberately unsafe Reverse graph used by recovery regressions. Both use named code maps and typed rule-document version 2. Fixture provisioning and defect injection remain code; migration graph meaning is loaded from these files.

Required runtime references are `PS09_CORPUS_SOURCE_CONNECTION`, `PS09_CORPUS_TARGET_CONNECTION`, `PS09_CORPUS_CSV_ROOT`, `PS09_CORPUS_SOURCE_FILES_ROOT`, and `PS09_CORPUS_TARGET_FILES_ROOT`. No resolved values belong in these files. Source SQL Server checkpoint isolation is explicitly Serializable for the deterministic synthetic fixture; it is not a customer default or permission to change database settings.

```powershell
proofshift validate scenarios/pension-modernization/ps010b/proofshift.yaml --strict
proofshift explain scenarios/pension-modernization/ps010b/proofshift.yaml project
proofshift explain scenarios/pension-modernization/ps010b/proofshift.yaml rule contribution-total
proofshift explain scenarios/pension-modernization/ps010b/proofshift.yaml mapping target-contribution
proofshift config show scenarios/pension-modernization/ps010b/proofshift.yaml --effective
proofshift rules list
proofshift dry-run scenarios/pension-modernization/ps010b/proofshift.yaml --json
proofshift report scenarios/pension-modernization/ps010b/proofshift.yaml --run <dry-run-id> --json
```

Execution requires the synthetic SQL Server/PostgreSQL schemas, CSV indexes, and payload files. The existing physical integration fixture provisions them and now loads these committed files as its primary semantic configuration. It asserts exactly 149 defective business discrepancies, 0 corrected discrepancies, 0 unaccounted sources, 0 unexplained targets, corrected Recovery success and QUALIFIED, plus false-Reverse behavior and reports/comparison. The parity regression verifies the corrected compiled graph hash equals the accepted legacy construction.

Discovery/scaffolding is demonstrated separately; it must not overwrite these reviewed mappings or infer Pension meaning from column names. Physical schema names may be confidential even when discovery contains no credentials. Validate any source field outside selected/mapped columns against discovery; `PSRULE012` marks incomplete static field knowledge rather than claiming the field is absent.