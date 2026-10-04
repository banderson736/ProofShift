# PS-0.9 Synthetic Pension Schema Draft

These DDL files describe the intended synthetic legacy and target shapes for the Public Pension Assurance Vertical. They are deliberately written independently and do not copy a real pension vendor schema.

- `database/sqlserver-source.sql` defines MEMBER, EMPLOYMENT_HISTORY, CONTRIBUTION, SERVICE_CREDIT, BENEFICIARY, RETIREMENT_ELECTION, BENEFIT_PAYMENT, and a document metadata index.
- `database/postgres-shadow-template.sql` defines participant-oriented records, employment events, financial transactions, service periods, relationships, elections, payments, and an object index.

The target does not mirror the source schema: target identities are generated keys, member references are normalized, employment periods become events, service intervals become a range, and documents use object paths. Apply these scripts only to synthetic local/test databases.

The current `proofshift demo generate` command exports clean source, corrected target, and defective target CSVs. These DDL templates are not yet populated by that generator or connected to a PS-0.9 Migration Graph. Do not treat them as a runnable full vertical or as a production migration plan.
