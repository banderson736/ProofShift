# Authoring Walkthrough

This approximately 20-30 minute technical walkthrough uses deterministic synthetic fixtures and existing SQL Server/PostgreSQL connectors. It does not mutate real customer sources. Database provisioning below is separate from read-only ProofShift discovery.

## 1. Initialize And Configure Endpoints

```powershell
proofshift init authoring-starter
proofshift rules list
proofshift capabilities --json
```

Start disposable SQL Server 2022 and PostgreSQL 16 databases using your normal Docker tooling. Use the synthetic fixture SQL in `scenarios/authoring-walkthrough/sqlserver-source.sql` and `postgres-target.sql` to provision `dbo.records` and `public.records`. The source contains two synthetic rows; the target is an empty shadow-template table. Supply connection strings through runtime environment references, never inline YAML values:

```yaml
# systems/source.yaml
id: source
name: Synthetic Source
role: source
storage:
  records:
    connector: sqlserver
    connection:
      secret: PROOFSHIFT_SOURCE_CONNECTION
    checkpoint:
      consistency: observed
```

The generated target already uses `PROOFSHIFT_TARGET_CONNECTION`. Configure its actual environment value locally. Change the starter's source selector to `kind: table`, `properties: {name: dbo.records}`, and `identity: [id]`. Physical-source configuration and semantic review are separate; no command silently labels this table as Pension.Member.

## 2. Discover Without Writing Sources

```powershell
proofshift discover authoring-starter/proofshift.yaml --system source --endpoint records --output discovery/source --json
proofshift discover authoring-starter/proofshift.yaml --system target --endpoint records --output discovery/target --json
```

Each new destination receives `physical-model.json` and an integrity-bound `manifest.json`. SQL discovery reads provider catalogs for tables/views, native columns, nullability, keys and foreign keys; it does not create indexes, change statistics/isolation settings, or run unrestricted data counts. CSV discovery streams configured files for headers/counts/inferred types/empty prevalence. Filesystem discovery summarizes extension patterns/counts/bytes without individual filenames. Observation time, absolute paths and record/byte summaries are excluded from structural fingerprints. Schema names can themselves be confidential.

## 3. Scaffold And Review

```powershell
proofshift scaffold --source-discovery discovery/source --target-discovery discovery/target --output suggested-project
proofshift validate suggested-project/proofshift.yaml --strict --json
```

Exact/normalized names, compatible physical type families and key roles produce suggestions with deterministic reasons. Even exact matches remain suggested, not approved. Ambiguous/unmapped required fields remain explicit. Strict validation rejects them using stable `PSAUTHOR001` diagnostics. Generated referenced-file projects and `mapping-review.json` are review artifacts, not migration approval. Keyless/unsupported physical shapes may require editing before the normal graph compiler accepts them; no special validation bypass exists.

The reviewed example CSV explicitly confirms `id` and `value`:

```powershell
proofshift mapping import --csv scenarios/authoring-walkthrough/reviewed-mappings.csv --source-discovery discovery/source --target-discovery discovery/target --output reviewed-project
proofshift validate reviewed-project/proofshift.yaml --strict
```

CSV headers are `Source Entity,Source Field,Target Entity,Target Field,Transformation,Required,Notes,Review Status`. Supported initial field transforms are copy, trim and normalize-string. Explicit artifact exclusions are authored as graph operations rather than pretending a field pipeline can exclude a complete source artifact. Invalid objects/fields, duplicate mappings, unsupported transforms, and invalid booleans/review states produce `PSIMPORT001`-`PSIMPORT007` with CSV row/column context. Import is built on `IMappingImporter`; no XLSX dependency is installed. Imported projects undergo the ordinary loader, graph compiler, rule and recovery checks.

## 4. Explain Effective Meaning

```powershell
proofshift explain reviewed-project/proofshift.yaml project --json
proofshift explain reviewed-project/proofshift.yaml mapping mapping-0 --json
proofshift explain reviewed-project/proofshift.yaml rule accounting --json
proofshift config show reviewed-project/proofshift.yaml --effective --json
```

These commands use the same configuration/graph/rule resolution and fingerprints as validation/execution. They show logical scope, transform pipelines, recovery declarations, descriptor defaults, selected provider versions and unresolved review state. Secret references remain references; no runtime secret values are resolved for inspection.

## 5. Isolated Dry Run And Report

The reviewed project declares PostgreSQL as `shadow-target` and Restore recovery with required checkpoints. Set its source/target environment references, then run:

```powershell
proofshift dry-run reviewed-project/proofshift.yaml --json
proofshift report reviewed-project/proofshift.yaml --run <dry-run-id> --json
```

Dry run uses isolated shadow output and existing assurance/recovery machinery. Technical QUALIFIED is not business acceptance or production migration authorization. If comparing a later discovery, use:

```powershell
proofshift discovery diff discovery/source discovery/source-later --json
```

Diff identifies object/field additions/removals, native-type/nullability changes, primary/unique keys and foreign keys. Different observation times alone do not create drift.

## Named Code Maps And Pension

Named maps live under `codeMaps` in the migration document. Each declares nonempty `values` and `unmapped: fail|pass-through|default`; default behavior requires a default value. Pipelines reference `{type: code-map, map: map-name}`. Duplicate keys are rejected by YAML loading; empty/reserved keys, empty targets, constrained target-enum violations and missing references/policies fail normal graph parsing. Generic Recovery still rejects non-injective maps for Reverse; there is no duplicated recovery implementation in authoring.

The full customer-like Pension example is `scenarios/pension-modernization/ps010b`. Its committed systems, graphs, named maps, typed rules and recovery policy replace test-only primary semantics. Its physical regression preserves exact 149-defect/corrected-zero/QUALIFIED behavior. Discovery suggestions never automatically establish Pension meaning.

## Compatibility And Editors

Read legacy rule-document version 1 and explicit typed version 2; generated projects write version 2 only. Comma-separated legacy lists retain their documented interpretation; ambiguous values are not silently migrated. No automatic migration/formatting command rewrites comments. Export `rules schema` and associate it with `rules/*.yaml` using VS Code's YAML `yaml.schemas` setting or any JSON-Schema-aware YAML editor. Schemas contain generic and installed pack descriptors and no timestamps/random IDs.