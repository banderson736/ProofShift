# Configuration Authoring (PS-0.10B Accepted)

PS-0.10B is Accepted after Docker-backed [run 37353339103, job 111909408991](https://github.com/banderson736/ProofShift/actions/runs/37353339103/job/111909408991) verified final implementation commit `625ca84` with 143 passed, 0 failed, 0 skipped. See [the walkthrough](AUTHORING_WALKTHROUGH.md) and the completed workflow notes below.

## Working Commands

```powershell
proofshift init my-project
proofshift init pension-project --template pension
proofshift validate my-project/proofshift.yaml --strict
proofshift rules list
proofshift rules describe pension-contribution-total
proofshift rules describe pension-contribution-total --json
proofshift rules schema > proofshift-rules.schema.json
proofshift capabilities --json
proofshift connectors schema oracle > oracle-connector.schema.json
proofshift discover my-project/proofshift.yaml --system source --endpoint records --output discovery/source --json
proofshift discovery diff discovery/source discovery/source-later --json
proofshift scaffold --source-discovery discovery/source --target-discovery discovery/target --output suggested-project
proofshift mapping import --csv reviewed.csv --source-discovery discovery/source --target-discovery discovery/target --output reviewed-project
proofshift explain reviewed-project/proofshift.yaml project --json
proofshift explain reviewed-project/proofshift.yaml rule accounting --json
proofshift explain reviewed-project/proofshift.yaml mapping mapping-0 --json
proofshift config show reviewed-project/proofshift.yaml --effective --json
```

Initialization writes block-YAML referenced files under `systems/`, `migration/`, `rules/`, and `recovery/`, plus a header-only synthetic CSV. It refuses non-empty directories. Both templates validate immediately without credentials. The PostgreSQL target is a `shadow-target` and uses `PROOFSHIFT_TARGET_CONNECTION` as a secret reference; target schema provisioning and the real runtime environment value remain prerequisites for execution. This starter is not the complete 149-defect Pension demo.

## Explicit Pack Selection

```yaml
packs:
  - id: pension
    version: "0.9.0"
```

`proofshift.pension` is also an installed ID. No unavailable version is substituted. Generic rules are always available; modern projects activate only selected packs. Listing installed descriptors/capabilities is not pack activation. Legacy single `pack` remains parseable; mixing it with `packs` is rejected.

## Structured Rule Documents

Rule-document version is independent of root format and implementation version. Root `proofshift: 1` remains supported. Typed rules explicitly opt in:

```yaml
version: 2
rules:
  contribution-total:
    type: pension-contribution-total
    version: "1"
    severity: critical
    targetNode: target-contribution
    semanticType: Pension.Contribution
    amountField: contribution_amount
    groupBy:
      - participant_id
      - payroll_period
      - contribution_kind
    tolerance: 0.01
```

Sequences remain ordered sequences; decimals/booleans retain their types; nested mappings remain immutable objects. Provider-owned descriptors drive validation, JSON Schema, descriptions and declared defaults. The installed catalog is authoritative: parser/schema support for nested values is not permission to use an unregistered generic alias or silently activate a pack. Pension aggregate/relationship/timeline semantics use the installed provider's configured rules.

## Diagnostics And Strict Policy

PS-0.10C adds provider-owned connector schemas for Oracle, Db2, fixed-width, JSON, NDJSON and XML. `proofshift connectors schema <id>` emits the installed connector's endpoint and selector JSON Schema. `proofshift validate <config> --strict` rejects undeclared endpoint/selector properties with source file and configuration path diagnostics; it also checks required endpoint properties, selector identity, format-required selector values and fixed-width field start/length pairs. Dynamic `fields.<name>.*` and XML `namespaces.<prefix>` properties are constrained by provider-declared patterns.

Fixed-width `width`, `start` and `length` are one-based positions/counts in decoded .NET UTF-16 code units, not source bytes. Supported fixed-width encodings are UTF-8 and UTF-16. NDJSON supports UTF-8 and UTF-16; top-level JSON arrays are UTF-8 only and streamed. XML uses XML-declaration/parser encoding detection and does not accept an endpoint-locale override. These format contracts describe current implementation, not semantic approval.

| Code | Meaning |
| --- | --- |
| PSRULE002 | Requested implementation version unavailable |
| PSRULE003 | Incorrect configured option type |
| PSRULE004 | Required option absent |
| PSRULE005 | Unsupported option |
| PSRULE006 | Unsupported enum |
| PSRULE007 | Numeric value outside declared range |
| PSRULE009 | Unknown semantic type |
| PSRULE010 | Unknown graph node |
| PSRULE011 | Field outside selected node identities/mappings |
| PSPACK001/002/003 | Unavailable/ambiguous pack, unavailable version, duplicate selection |
| PSAUTHOR001 | Unconfirmed required mapping in strict mode |
| PSAUTHOR003 | Reverse declaration without proven inverse |

Diagnostics include file/path and YAML line/column where available, without resolved secret values. Authoring checks reference syntax without requiring secret values; execution still uses default environment/runtime checks. Legacy projects retain their default environment-check behavior. Strict mode applies named mapping/recovery policies, not blanket warning promotion.

## Editor Association

Associate exported schemas through the YAML extension's normal VS Code settings:

```json
{
  "yaml.schemas": {
    "./proofshift-rules.schema.json": ["rules/*.yaml"]
  }
}
```

Generation is deterministic for an installed descriptor set. The loader, descriptor validator and graph compiler remain authoritative. No IDE extension or destructive comment-rewriting formatter was added.

## Compatibility And Status

Omitted rule-document version remains legacy version 1 with its accepted scalar/sequence interpretation and fingerprint bytes. Version 2 uses a structured canonical marker; lists must be sequences and numeric/boolean values must match descriptors. Source-location metadata is not fingerprinted. Automatic configuration migration remains pending.

Full local completion tests: 143 total, 141 passed, 0 failed, 2 Windows symlink-capability skips. Real Docker SQL Server/PostgreSQL discovery, CSV/filesystem discovery, discovery drift/integrity, scaffold determinism/ambiguity, CSV import diagnostics, named-map false-Reverse, effective explanations, and the committed physical Pension pipeline are covered. Later selector/draft/root corrections passed focused file-discovery and authored-command tests; restore/build pass. Remote Docker CI passed all 143 tests with 0 failures and 0 skips, with totals verified directly from the job log.

## Completed Discovery And Review Workflow

Discovery artifacts contain connector/version, logical endpoint identity, observation time, sorted physical objects/fields/keys/relationships, a timestamp-independent structural fingerprint, and a payload-hash-bound manifest. Counts/bytes are observational summaries. SQL discovery reads catalogs for tables/views, native types, nullability, primary/unique keys/indexes and foreign keys without writing sources or running unrestricted data counts. CSV inference is labeled and streamed, including delimiter/path metadata; filesystem discovery emits aggregate extension patterns/counts/bytes rather than filenames and enforces containment.

Diff detects objects/fields added/removed, type/nullability changes, and primary/unique/foreign-key changes. Scaffolds expose exact/normalized-name matches, compatible type families and key-role reasons as suggestions only. Ambiguous/unmapped required fields remain visible. Drafts with keyless/unsupported shapes include normal compiler diagnostics; no validation/execution bypass exists. Strict validation rejects unconfirmed required suggestions. Physical similarity never asserts Pension/business semantics.

CSV import uses `IMappingImporter`, documented stable headers, and located `PSIMPORT001`-`PSIMPORT007` failures for unknown fields/objects, duplicate/ambiguous source or target mappings, unsupported transforms and invalid required/review values. Omitted source fields stay unresolved. Executable imports use normal loader/compiler/rule checks. Initial transforms are copy, trim and normalize-string; artifact exclusion remains an authored graph operation. XLSX is deferred without spreadsheet dependencies.

## Reusable Maps And Effective Meaning

Named `codeMaps` require nonempty values and explicit `unmapped: fail|pass-through|default`; default policy requires a default value, and `allowedTargets` constrains mapped/default targets where supplied. Normal graph parsing resolves named references into existing transformation parameters. Duplicate/empty/reserved keys, empty targets, unknown references and missing/invalid policies fail closed. Generic Recovery still detects non-injective maps under Reverse. Canonical implicit-fail representation preserves the accepted graph hash.

Explain project/rule/mapping and effective-config inspection use normal configuration/graph/rule resolution and expose only secret references, never resolved values. Descriptor defaults and aggregate meaning are shown; no second independent semantic model is hashed. `PSRULE012` warns where a source field needs physical confirmation because identities/mapped fields are not a complete source schema. Source locations and stable codes remain available; strict mode does not blindly promote all warnings.

## Complete Pension Project And Scope

`scenarios/pension-modernization/ps010b` contains ordinary systems, corrected/false-Reverse graphs, named maps, typed rules and recovery policy. The primary physical integration path loads these files instead of constructing its migration meaning in C#. Fixture provisioning/defect injection remain code. The full regression preserves 149 defective, 0 corrected, 0 unaccounted, 0 unexplained, corrected Recovery success/QUALIFIED, false-Reverse and reports/comparison; corrected compiled graph hash matches the legacy construction exactly. Medium was not rerun because execution architecture was not materially changed.

Schema names and authored notes can be confidential. Inferred CSV types and normalized-name/type-family suggestions are review aids, not semantic certainty. Legacy documents remain readable and new output uses typed v2; comments are not destructively formatted. PS-0.10C, new connectors/packs, UI/AI and production migration/rollback remain deferred.