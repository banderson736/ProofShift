# Agent Guide

This repository is intended to be developed primarily with an IDE coding agent while preserving product/architecture continuity.

Agents must read `.github/copilot-instructions.md` and the linked documentation before meaningful changes.

The repository documentation is part of the product specification. When code and docs disagree, do not silently choose one: identify the conflict and resolve it explicitly.

PS-0.1 through PS-0.8 are accepted. PS-0.6 was accepted by Docker-backed GitHub Actions run 37180351598, job 111371493382 (88 passed, 0 failed, 0 skipped). PS-0.7 was accepted by Docker-backed GitHub Actions run 37185122084, job 111385343893 (96 passed, 0 failed, 0 skipped). PS-0.8 Recovery Readiness & Dry-Run Qualification was accepted by Docker-backed GitHub Actions run 37190061657, job 111400188926 (103 passed, 0 failed, 0 skipped).

PS-0.9 Public Pension Assurance Vertical was accepted after Docker-backed GitHub Actions run 37238973286, job 111543651181 passed 113 tests with zero failures and zero skips. Do not implement production migration execution, production rollback, or PS-0.10 without an explicit new assignment.

PS-0.10A Scale Baseline & Hot-Path Hardening is currently In Progress under an explicit assignment. Preserve the exact PS-0.9 149-defect / corrected-zero assurance semantics and completed bounded PostgreSQL batching, WAL/NORMAL scratch workspace, persisted Verification ledger, aggregate Recovery coverage, typed ordering keys, streamed Evidence, and explicit checkpoint consistency work. Do not begin PS-0.10B/C, connector/domain expansion, production migration execution, or rollback without a new explicit assignment. Remote Docker-backed CI and the medium full-pipeline benchmark remain required acceptance gates.
