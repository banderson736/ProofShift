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

## Initial rule catalog

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
