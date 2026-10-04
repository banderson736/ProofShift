# ProofShift Market Research Context

**Research snapshot: October 2026.** This file records the market hypothesis and public signals that motivated the current product direction. It is not a substitute for continuing customer discovery.

## Primary finding

The strongest initial wedge identified is **public pension / retirement-system modernization**, followed by utility CIS/billing and justice/case-management modernization.

The reason is not that pension data has a universal FHIR-like schema. The value is that the same business concepts and assurance requirements recur while the physical source/target implementations vary widely.

## Public pension signals

Recent/current public pension modernization materials describe needs such as:

- source-to-target mapping/crosswalks;
- custom transformation rules;
- data cleansing;
- repeated mock/trial conversions;
- reconciliation and discrepancy reports;
- complete reconciliation of financial and benefit balances;
- automated anomaly checks;
- side-by-side legacy/new-system comparison;
- formal conversion acceptance/sign-off;
- independent/third-party conversion and IV&V roles.

Examples previously reviewed in research include:

### Orange County Employees Retirement System (OCERS)
Modernization materials call for mock conversions, ETL/validation/reconciliation, automated checks, side-by-side comparison, detailed reconciliation reporting, and reconciliation of financial/benefit balances.

Source previously reviewed:
https://www.ocers.org/sites/main/files/file-attachments/ocers_horizon_pension_adm_solution_rfp.pdf

### San Mateo County Employees' Retirement Association (SamCERA)
2026 PASS procurement Q&A indicates a third-party data-conversion vendor managing conversion from legacy extraction through delivery to the new pension administration vendor.

Source previously reviewed:
https://content.samcera.gov/wp-content/uploads/2026/04/SamCERA-PASS-RFP-Round-2-Bidder-Question-Responses.pdf

### Oregon PERS
2026 board materials describe legacy data as a modernization risk and planning for vendor-supported data-conversion/migration work.

Source previously reviewed:
https://www.oregon.gov/pers/Documents/Board-Meetings/2026/05-29-2026-PERS-Board-Meeting-Packet.pdf

### District of Columbia Retirement Board
A 2026 Benefits Data Analyst role included data conversion, data validation, and legacy-system reconciliation responsibilities.

Source previously reviewed:
https://dcrb.dc.gov/release/benefits-data-analyst

### Texas ERS RISE
Texas Employees Retirement System's multi-year modernization is replacing PeopleSoft retirement/insurance functions with TELUS Health Ariel. Data-conversion recruiting around the effort references ETL/conversion scripts/reconciliation tooling and SQL Server/T-SQL/SSIS skills.

Research sources previously reviewed included public ERS materials and job/recruiting postings.

## Workflow signal from pension specialists

Specialist firms describe consultant work that closely resembles the ProofShift thesis:

- profile legacy data;
- gather plan/business rules;
- encode rules as SQL/conversion logic;
- identify invalid data;
- load staging/target systems;
- build reconciliation scripts;
- repeat conversion/reconciliation cycles.

This suggests a potential productization opportunity: reusable semantic mappings, rules, evidence, lineage, and reconciliation rather than rebuilding custom SQL/script frameworks for every project.

Potential ecosystem categories:

- pension data-conversion consultancies;
- pension IV&V / modernization consultancies;
- pension administration system vendors;
- retirement agencies;
- government primes/subcontractors.

Named ecosystem examples surfaced during research include Linea Solutions, MBS, LRS Retirement Solutions, TELUS Health, Vitech, Sagitec, and Tegrit. These are not automatically customers; some may be partners, competitors, or discovery targets.

## Utility CIS / billing

Utility-system replacements routinely include complex conversion requirements around:

- customer/account/premise/service-point relationships;
- meters and readings;
- billing history;
- payments/adjustments;
- deposits;
- outstanding balances;
- historical continuity.

This domain has useful semantic standardization such as MultiSpeak, which may make a future Utility Pack more reusable.

Potential semantic model:

```text
Customer
Account
Premise
ServicePoint
Meter
Reading
Bill
Payment
Adjustment
Balance
```

Example invariants:

- every active account exists;
- service point remains attached to correct premise;
- meter history remains continuous;
- charges + adjustments - payments reconcile to balance;
- historical billing totals reconcile;
- deposits and receivables reconcile.

## Justice / case-management

Government case-management modernization often requires full data conversion, mapping/crosswalking, cleanup, reconciliation, and relationship preservation.

Potential semantic concepts:

```text
Person
Case
Incident
Charge
CourtEvent
Disposition
Agency
Officer
Document
Evidence
SupervisionEvent
```

NIEM may provide useful common government semantics for a future Justice Pack, although ProofShift must not assume one universal justice schema.

## Healthcare / FHIR

FHIR gives far more standardized semantics than pensions/utilities. A Healthcare Pack could become powerful because it can leverage resource types, profiles, cardinalities, terminology, references, extensions, and implementation guides.

However, healthcare/FHIR is not the first commercial wedge because:

- mature FHIR tooling exists;
- direct competition is stronger;
- the founder's current employment overlaps with state healthcare/FHIR work, creating a reason to maintain a clean independent product boundary.

The future Healthcare Pack should be based exclusively on public standards and independently created implementation.

## ERP/HCM

The market is large and migration assurance is real, but competition from SAP/Oracle/Workday ecosystems, system integrators, and established data-testing tools makes it a poor initial beachhead.

## Generic DB migration

Do not position ProofShift as PostgreSQL-to-PostgreSQL reconciliation. Generic products already handle many record-count and field-level migration checks.

Generic DB connectors remain necessary plumbing and excellent deterministic test infrastructure.

## Market validation plan

Before large investment beyond PS-0/early PS-1, seek at least three of these signals:

1. **Repeated pain** — at least five practitioners independently describe substantial manual reconciliation/validation effort.
2. **Existing spend** — employers/consultancies/contracts clearly pay for migration validation, IV&V, data conversion, or reconciliation.
3. **Weak tooling** — practitioners rely heavily on SQL, spreadsheets, Python, custom scripts, manual sampling, and one-off reports.
4. **Consequences** — late defects cause cutover delays, acceptance disputes, remediation, financial issues, or regulatory risk.
5. **Trial intent** — two or more credible organizations ask to see/demo/test the product on a real upcoming project.

## Research questions

Ask about real historical behavior, not hypothetical purchasing intent:

1. Describe the most recent large migration.
2. How did you determine it succeeded?
3. Who independently validated converted data?
4. How were transformed schemas compared?
5. How were missing/duplicate/mis-linked records detected?
6. How was history validated?
7. How were financial totals reconciled?
8. What evidence was needed for acceptance?
9. What tools/scripts were used?
10. Which parts were manual?
11. How many trial conversions were run?
12. What failed late?
13. How was rollback/cutover recovery handled?
14. What would be changed next time?
15. Where would a reusable semantic assurance tool fit or fail?

## Ongoing monitoring

A scheduled ProofShift RFP watch was created in ChatGPT to monitor new migration/reconciliation/IV&V opportunities, prioritizing pensions, utility CIS/billing, justice systems, healthcare/FHIR, and ERP/HCM.
