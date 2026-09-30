# Architecture Decision Records

Significant decisions are recorded here as `NNNN-short-title.md` (e.g. `0001-modular-monolith.md`).

Use an ADR for: architecture changes, choice of a major dependency, tenant-isolation or authentication design, data-model conventions, and anything an agent needs human approval for.

## Template

```markdown
# NNNN — Title

- Status: Proposed | Accepted | Superseded by NNNN
- Date: YYYY-MM-DD
- Deciders: <human approver>

## Context
## Decision
## Consequences
## Alternatives considered
```

Only **Accepted** ADRs are authoritative (see [AGENTS.md](../../AGENTS.md)). Agents may propose ADRs; a human accepts them.

## Index

| ADR | Title | Status |
|---|---|---|
| [0001](0001-dependency-model.md) | Layer dependency model | Proposed |
| [0002](0002-authentication.md) | Authentication, authorization and tenant isolation | Proposed |
| [0003](0003-commands-and-queries-without-mediator.md) | Commands and queries without a mediator library | Proposed |
| [0004](0004-inventory-concurrency.md) | Inventory concurrency | Proposed |
| [0005](0005-testing-infrastructure.md) | Testing infrastructure | Proposed |
