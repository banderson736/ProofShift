# ProofShift Pension Pack — Initial Semantic Brief

This document defines the initial semantic intent of `proofshift.pension`. It is not a universal pension-industry standard and must not be mistaken for one. It provides reusable concepts and invariants for the PS-0 synthetic scenario and future customer-specific mapping.

## Design principle

A pension pack describes **business meaning**, not database layout.

A legacy system may represent a member across dozens of tables. A target PAS may expose one object, events, stored procedures, APIs, or vendor staging tables. Both can map to the same ProofShift semantic concepts while retaining full source/target provenance.

## Initial semantic types

### Pension.Member

Represents a participant/member identity and current membership-level state.

Likely attributes:

- stable source identity;
- person/member identifiers;
- name components;
- date of birth where synthetic scenario requires it;
- membership/enrollment date;
- membership status;
- retirement status;
- termination/death indicators where applicable;
- plan membership references.

Important distinction: current member state is not a substitute for historical employment/status events.

### Pension.Employment

Effective-dated employment or participation periods.

Likely attributes:

- member relationship;
- employer/agency;
- start/end;
- employment type/status;
- full/part-time semantics if needed;
- eligible/pensionable indicators if needed.

Primary verification concern: semantic timeline continuity and correct member association.

### Pension.Contribution

Financial contribution transaction.

Likely attributes:

- member;
- employer;
- payroll/fiscal period;
- contribution type;
- employee amount;
- employer amount where applicable;
- pensionable compensation basis;
- transaction/effective date;
- adjustment/reversal indicator;
- source transaction identity.

Primary verification concerns:

- every contribution accounted for;
- duplicates/missing transactions;
- member association;
- period/group totals;
- lifetime totals;
- adjustment semantics.

### Pension.ServiceCredit

Represents credited service or a service period contributing to eligibility/benefit calculation.

Likely attributes:

- member;
- period;
- credit amount/unit;
- credit type;
- source employment/contribution basis;
- adjustments/purchases where applicable.

Primary verification concerns:

- total credit preservation;
- period continuity;
- duplicates;
- effective dates;
- member association;
- transformation/rounding rules.

### Pension.Beneficiary

Represents beneficiary relationship/history rather than only an unrelated person row.

Likely attributes:

- member;
- beneficiary identity;
- relationship;
- designation type;
- percentage/allocation;
- effective dates;
- primary/contingent order;
- status.

Primary verification concerns:

- beneficiary belongs to correct member;
- allocation totals where applicable;
- effective history;
- no missing/duplicated beneficiary relationships.

### Pension.RetirementElection

Represents retirement/election choices that influence benefit processing.

Likely attributes:

- member;
- retirement/effective date;
- election/option code;
- survivor/beneficiary option references;
- payment option;
- source election identity;
- status/version/history if applicable.

Primary verification concern: code transformations must preserve business meaning, not merely produce valid target codes.

### Pension.BenefitPayment

Represents historical benefit payment transactions.

Likely attributes:

- member/retiree;
- payment period/date;
- gross amount;
- deductions where represented;
- net amount where represented;
- payment type;
- reversal/adjustment information;
- source payment identity.

Primary verification concerns:

- complete historical accounting;
- member association;
- period totals;
- lifetime totals;
- duplicate/missing/reversal semantics.

### Pension.Document

Represents source documents and historical statements.

Likely attributes:

- logical owner/member;
- source path/object identity;
- document category;
- date;
- content hash;
- metadata;
- archive/migration disposition.

Primary verification concerns:

- no silent loss;
- correct member association;
- byte/hash preservation where copied unchanged;
- explicit archive/transform disposition where representation changes.

## Initial relationships

Examples:

```text
Member HAS_EMPLOYMENT Employment
Member HAS_CONTRIBUTION Contribution
Member HAS_SERVICE_CREDIT ServiceCredit
Member HAS_BENEFICIARY Beneficiary
Member HAS_RETIREMENT_ELECTION RetirementElection
Member RECEIVES_BENEFIT_PAYMENT BenefitPayment
Member HAS_DOCUMENT Document
```

Future models may introduce Employer/Plan/Payroll semantic types. Do not add them until a vertical slice needs them.

## PS-0.9 rule catalog

### Accounting

- `SourceDispositionRule`: every source artifact has a disposition.
- `TargetLineageRule`: every target artifact has lineage.

### Member

- `MemberUniquenessRule`
- `MemberPresenceRule`
- `MemberStatusSemanticRule`

### Employment/history

- `EmploymentTimelineEquivalenceRule`
- `EmploymentMemberRelationshipRule`
- optional no-unexplained-gap/no-invalid-overlap checks where plan semantics support them.

### Contributions

- `ContributionAccountingRule`
- `ContributionIdentityRule`
- `ContributionMemberRelationshipRule`
- `ContributionPeriodTotalRule`
- `ContributionLifetimeTotalRule`

### Service credit

- `ServiceCreditTotalRule`
- `ServiceCreditTimelineRule`
- `ServiceCreditMemberRelationshipRule`

### Beneficiaries

- `BeneficiaryRelationshipRule`
- future allocation-total/effective-history rules as scenario expands.

### Retirement elections

- `RetirementElectionSemanticMappingRule`

### Benefit payments

- `BenefitPaymentAccountingRule`
- `BenefitPaymentPeriodTotalRule`
- `BenefitPaymentLifetimeTotalRule`

### Documents

- `DocumentAccountingRule`
- `DocumentRelationshipRule`
- `DocumentContentHashRule` where no content transform occurs.

### Recovery

- `RecoveryCoverageRule`
- `RequiredSnapshotRule`

The implemented provider currently registers `MemberAccountingRule`, `MemberUniquenessRule`, `PensionMemberPresenceRule`, `PensionMemberStatusRule`, `EmploymentTimelineRule`, `ContributionAccountingRule`, `ContributionTotalRule`, `ServiceCreditTotalRule`, `BeneficiaryRelationshipRule`, `RetirementElectionRule`, `BenefitPaymentRule`, `BenefitPaymentTotalRule`, `DocumentAccountingRule`, `DocumentRelationshipRule`, and `PensionCodeTransformationRule`. Generic source disposition, target lineage, unexpected-target, and physical attribute rules remain provided by generic Verification. All rule logic is resolved through the existing versioned provider registry; the provider identity is `proofshift.pension` version `0.9.0`.

The installed rule types are configured by their registry identifiers (for example `pension-employment-timeline`, `pension-contribution-total`, and `pension-document-accounting`). Each configured definition supplies a rule version and options; its version/options and the Pension provider version contribute to the generic rule-set fingerprint. Financial rules use an explicit decimal `tolerance`, defaulting to `0.01`; the tolerance is part of the rule definition fingerprint.

## Semantic comparison contracts

### Employment history

`EmploymentTimelineRule` groups source intervals and target events by configured member keys. It checks valid starts, overlaps, optional gaps, effective dates, and state transitions. A legacy ACTIVE/INACTIVE/ACTIVE interval sequence may correspond to target JOINED/TERMINATED/REINSTATED events. The rule compares semantic event dates/codes, not physical row shape or row count. Configure source/target node keys and field names explicitly; `allowGaps` defaults to false.

The small generator currently creates three contiguous intervals per generated member and a structurally different target event record for each interval. It does not model every jurisdiction's rehire, leave, purchased service, concurrent employer, or plan-specific continuity rules.

### Financial reconciliation

`ContributionAccountingRule` compares transaction identity, member, period, category, and decimal amount. `ContributionTotalRule` compares exact transaction counts and decimal totals for configured member/period/category groups. `BenefitPaymentRule` performs transaction-level identity/member/period/date/amount checks; `BenefitPaymentTotalRule` adds period totals. Evidence stores safe grouping fingerprints and count/total/difference/tolerance values, not member names or raw record payloads.

`ServiceCreditTotalRule` groups by member by default and compares decimal totals while allowing physical period records to be restructured. It intentionally does not require equal row counts. The rule does not calculate actuarial eligibility, vesting, or benefit amounts.

### Relationships and elections

`BeneficiaryRelationshipRule` checks beneficiary identity, member association, referenced-member existence when `memberNode` is configured, relationship type, and allocation tolerance. `DocumentRelationshipRule` checks document/member association. Retirement-election codes are expected to be mapped in the migration graph; `RetirementElectionRule` compares the graph-derived target option, identity, and effective date. The pack does not invent jurisdiction-specific election semantics.

### Documents and exports

`DocumentAccountingRule` compares expected and observed document identities/content hashes; its `missingCode` option lets the same generic pack rule classify historical exports separately. `DocumentRelationshipRule` checks member ownership. Source dispositions and target lineage are still calculated by generic Verification and must be inspected alongside the document rules. The integrated fast scenario materializes deterministic binary document/export payloads on the filesystem, resolves them through Archive graph edges, and verifies their target content hashes and ownership.

## Deterministic data and defects

`PensionSyntheticDatasetGenerator` version `proofshift-pension-generator-v1` produces a clean dataset lazily. `PensionTargetDataModel` version `proofshift-pension-target-model-v1` changes names, code representations, and employment intervals to target events. `PensionDefectInjector` is a separate transformation identified by `proofshift-pension-defects-v1`. The default seed is `20261003`; identical seed, version, and scale produce the same source fingerprint and records.

The fast scale is 100 members, 300 employment periods, 5,000 contributions, 350 service periods, 160 beneficiaries, 35 retirement elections, 2,000 benefit payments, 250 documents, and 25 historical exports. The lazy large scale targets 100,000 members, 300,000 employment periods, 5,000,000 contributions, 350,000 service periods, 160,000 beneficiaries, 35,000 elections, 2,000,000 payments, 250,000 documents, and 25,000 exports. On the current environment, `proofshift demo benchmark --scale large --seed 20261003` generated 8,220,000 records in 16,592 ms, estimated 984,245,832 UTF-8 bytes, 495,415 records/sec, 59,320,016 estimated bytes/sec, and observed 55,627,776 bytes peak process working set. The physical integrated fast run completed in 396,017 ms: 8,495 SQL/CSV/filesystem checkpoint artifacts, 8,595 PostgreSQL/filesystem projected targets, 336 persisted evidence records, 519,651,328 bytes peak process working set, and zero temporary workspace bytes after cleanup. It produced a defective report with 149 discrepancies and a not-qualified result, and a corrected report with zero discrepancies and a qualified result. This is a local fast-scale integrated benchmark, not a large-scale or production-throughput benchmark.

The v1 defect manifest declares exactly 149 discrepancies across the 18 categories in `PensionDefectCounts.V1`; rule and service tests assert all 17 record-level finding categories and the false-Reverse count exactly. In the full fast-corpus projection scenario, both declared false-Reverse transformations are actual target-producing graph edges; Projection and Verification pass, and generic Recovery rejects both.

The machine report has 18 stable business-discrepancy categories separate from `EvidenceFailureCounts`. Supporting contribution/payment aggregate evidence is not added again to transaction-level discrepancy totals. The false-Reverse business count comes from persisted generic Recovery assessment edges classified as lossy while configured `Reverse`.

## Known limitations

- The fast generated corpus is physically captured from SQL Server, CSV, and filesystem sources and projected to PostgreSQL/filesystem targets. The projected defective and corrected runs each persist Verification, Recovery, and report artifacts; their comparison attributes resolved defects to the source/checkpoint and graph differences.
- Recovery coverage is evaluated by generic `ProofShift.Recovery`; a separate Pension-owned `RecoveryCoverageRule` is not currently registered because Recovery runs after Verification and remains domain-neutral.
- The separate Docker-backed external-target service test still verifies independently populated PostgreSQL observations without Projection artifacts or target writes. It asserts all 18 exact discrepancy categories and graph-derived expected lineage; it does not claim observed vendor execution lineage.
- Rule streaming is bounded by one configured group/member timeline, except the beneficiary rule currently keeps the configured member-key set in memory. Evidence is persisted as an integrity-checked NDJSON stream and pension report aggregation reads it incrementally; detailed failure exceptions are retained in the final report.
- The demo generator writes synthetic CSV representations; the physical integrated test separately materializes and validates deterministic binary payloads. The large benchmark measures generator enumeration only; no large-scale full-pipeline result exists. PS-0.9 was accepted after Docker-backed [GitHub Actions run 37238973286, job 111543651181](https://github.com/banderson736/ProofShift/actions/runs/37238973286/job/111543651181) passed 113 tests with zero failures and zero skips.
- Timeline transition semantics and code maps are configurable but intentionally generic within the pension pack. Validate assumptions with a pension subject-matter expert before applying them to a real system.

## Important semantic distinction: artifact vs concept

One source artifact may contribute to multiple semantic concepts/targets, and multiple source artifacts may combine into one target concept.

Examples:

```text
MEMBER + MEMBER_STATUS_HISTORY
        ↓
Pension.Member + target event history
```

or:

```text
CONTRIBUTION rows × N
        ↓
Contribution transaction history
        +
Derived lifetime aggregate
```

The migration/evidence graph must represent this without losing source accounting.

## PS-0 synthetic-schema intent

The SQL Server schema should deliberately look like a plausible older relational system while the PostgreSQL shadow target uses a different structure/naming convention. The purpose is to test semantic mapping, not mimic any real vendor schema.

Do not copy or imitate proprietary pension-vendor schemas.
