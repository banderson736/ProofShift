# Pilot Intake Checklist

This checklist is for deciding whether a proposed **controlled technical
evaluation** fits the current product boundary. It is not a sales contract,
implementation commitment, production-readiness review, or promise to build
unsupported connectors. Do not provide production credentials or real
customer data through this checklist.

## Migration shape

- [ ] Source technology and version:
- [ ] Target technology and version:
- [ ] Required connector capabilities available in the package:
- [ ] Logical systems and endpoint count:
- [ ] Migration mechanism/tool, operated separately from ProofShift:
- [ ] Expected transformations, splits, merges, and exclusions:
- [ ] Identity and key strategy:
- [ ] Current-state and history requirements:
- [ ] Business reconciliation rules that must be evaluated:
- [ ] Required source accounting and target lineage:
- [ ] Artifact volumes and approximate size ranges:
- [ ] Expected number of trial conversions and comparisons:

## Access and test data

- [ ] Deterministic synthetic or authorized non-production data is available:
- [ ] Source read access is available for discovery and checkpoint only:
- [ ] Source can be stopped or isolated after checkpoint capture:
- [ ] Target observation can use a distinct read-only account:
- [ ] The external migration process has its own separate write credential:
- [ ] Target DML and DDL denial can be tested for the observer account:
- [ ] No credential or secret value needs to be stored in project files:

## Environment and evidence

- [ ] Package platform and x64 runtime prerequisites fit the environment:
- [ ] Docker/Linux requirements for physical demonstrations are understood:
- [ ] Network/image-pull policy is compatible with the packaged rehearsal:
- [ ] Environment restrictions and offline/isolated requirements:
- [ ] Evidence and report retention location, owner, and retention period:
- [ ] Reviewers who need the persisted Evidence/report/compare output:
- [ ] Data classification and permitted synthetic test boundary:
- [ ] Required technical-pilot success criteria and stop conditions:

## Fit decision

- [ ] The workflow can be performed without ProofShift target-write access.
- [ ] A source checkpoint can be completed before the source is isolated.
- [ ] The migration can be invoked separately from ProofShift.
- [ ] Configured rules can express the intended assurance question.
- [ ] The evaluation does not require unsupported connectors, automatic
      semantic inference, production migration, rollback, UI, hosted service,
      authentication/RBAC, certification, or an SLA.
- [ ] Unsupported requirements have been recorded as limitations or a
      no-go, rather than assumed to be pilot deliverables.

If any required capability is unsupported, stop and document the gap. A
controlled pilot must not expand the product scope implicitly.
