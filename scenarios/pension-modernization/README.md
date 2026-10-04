# Pension Modernization Synthetic Scenario

This scenario will become the deterministic PS-0 end-to-end demonstration.

## Intended source topology

- SQL Server legacy pension data
- file/document storage

## Intended target topology

- PostgreSQL shadow pension model
- filesystem/object-store shadow document store

## Domain concepts

- Pension.Member
- Pension.Employment
- Pension.Contribution
- Pension.ServiceCredit
- Pension.Beneficiary
- Pension.RetirementElection
- Pension.BenefitPayment
- Pension.Document

## Dataset behavior

The final generator should support 100,000 members and millions of financial/history records with a fixed seed. Development begins with a 10-member vertical slice.

## Initial 10-member seed defects

- one missing member;
- one invalid status transformation;
- one capitalization-only difference that should normalize and pass.

Later defect counts are specified in `docs/PS0_SPECIFICATION.md`.
