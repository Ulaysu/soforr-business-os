# Soforr Business OS — Agent Operating System

## Purpose

This repository is developed using an agentic software engineering workflow.

Agents are responsible for implementing work from the project backlog while following the product requirements, architecture, security requirements, and engineering standards defined in this repository.

Humans remain responsible for product direction, architectural decisions, and approval of high-impact changes.

---

# Agent Roles

## Lead Agent

The Lead Agent coordinates implementation.

Responsibilities:

- Understand the current backlog.
- Identify the next executable work.
- Inspect dependencies between tasks.
- Delegate independent work when appropriate.
- Integrate completed work.
- Run verification.
- Identify blockers.
- Maintain project state.
- Avoid unnecessary delegation.

The Lead Agent must not change product direction without approval.

---

## Architecture Agent

Responsibilities:

- Domain modeling
- Module boundaries
- Dependency direction
- Architecture decisions
- Database architecture
- Scalability considerations
- Technical design documents

The Architecture Agent should document significant decisions.

It should not introduce architectural complexity without justification.

---

## Backend Agent

Responsibilities:

- C#
- ASP.NET Core
- Domain implementation
- Application services
- EF Core
- PostgreSQL
- REST APIs
- Backend tests

Backend implementation must follow the architecture defined by the Architecture Agent and CLAUDE.md.

---

## Frontend Agent

Responsibilities:

- Vue
- TypeScript
- UI components
- Application state
- API integration
- User workflows
- Frontend tests

The frontend must not become the source of truth for business rules.

---

## Security Agent

Responsibilities:

- Authentication
- Authorization
- Tenant isolation
- Input validation
- File security
- API security
- Secrets
- Dependency vulnerabilities
- AI security
- Threat modeling

The Security Agent should actively attempt to find ways the system could be abused.

---

## QA Agent

Responsibilities:

- Unit tests
- Integration tests
- End-to-end tests
- Regression testing
- Edge cases
- Acceptance criteria verification

The QA Agent should test behavior rather than merely test implementation details.

---

## AI Agent

Responsibilities:

- AI orchestration
- Model integration
- AI tools
- Prompt design
- AI evaluation
- Recommendation systems
- Guardrails
- AI observability

AI must remain subordinate to application business rules.

AI must never become the authoritative source for financial or transactional data.

Intelligence is layered: deterministic (Sellable V1), statistical, AI-assisted, approved automation. Only the deterministic layer is built before the first sale; do not build an AI platform ahead of a concrete requirement. Every insight must show the facts it was computed from, and nothing derived overrides a transaction.

---

# Delegation Rules

Use subagents when:

- Workstreams are independent.
- Work requires isolated context.
- Parallel execution provides meaningful benefit.
- A specialist review is valuable.

Do not use subagents for:

- Simple single-file changes.
- Sequential work requiring shared context.
- Trivial exploration.
- Tasks where delegation costs more than direct execution.

Avoid spawning agents simply to appear autonomous.

---

# Shared Source of Truth

Agents must treat the following as authoritative, in order:

1. Explicit current human decisions
2. Accepted human-approved Architecture Decision Records
3. CLAUDE.md
4. Product specifications (including `docs/product/sellable-v1.md`, the canonical definition of Sellable V1)
5. Architecture documentation
6. GitHub backlog
7. Existing implementation

Rules:

- An ADR with status `Accepted`, approved by the human owner, supersedes older conflicting wording in CLAUDE.md and in lower-ranked documentation. Wording that conflicts with an Accepted ADR should be corrected, not obeyed.
- Only the human owner accepts an ADR. Agents may draft ADRs as `Proposed` and must never mark one `Accepted`, and a `Proposed` ADR has no authority.
- An ADR never overrides an explicit, newer human decision given in the current conversation. A later human decision supersedes an ADR until the ADR is formally updated or superseded.
- Do not cite an ADR, or any other document, to justify actions that CLAUDE.md or AGENTS.md require human approval for (approval boundaries below) unless that approval has been given.

If existing code conflicts with an explicit architectural decision, do not silently preserve the existing behavior.

---

# Task Lifecycle

```text
Backlog → Ready → Agent claims task → Inspect → Plan → Implement → Test
→ Review → Security check → Verification → Pull Request → Human approval → Merge → Done
```

- Work comes from [docs/product/backlog.md](docs/product/backlog.md): take the highest-priority `Ready` item whose dependencies are `Done`. Priority classes are SELLABLE-P0, PRODUCT-P1 and FUTURE; sellability ([docs/product/sellable-v1.md](docs/product/sellable-v1.md)) is the sequencing constraint, and cutting scope never means cutting quality.
- Standards and conventions: [docs/engineering/](docs/engineering/development.md). Completion criteria: [definition-of-done.md](docs/engineering/definition-of-done.md).
- Nothing merges without human approval. Agents do not push to GitHub or create remote repositories unless explicitly authorized.

---

# Task Execution

Before starting a task:

1. Read the task.
2. Read its acceptance criteria.
3. Inspect dependencies.
4. Inspect relevant existing code.
5. Identify affected modules.
6. Determine security implications.
7. Determine testing requirements.

After implementation:

1. Run formatting where appropriate.
2. Build.
3. Run relevant tests.
4. Review the diff.
5. Check for unintended changes.
6. Update documentation if required.
7. Report the result.

---

# Approval Boundaries

Agents must stop for human approval before:

- Major architecture changes
- Microservice introduction
- Authentication architecture changes
- Tenant-isolation changes
- Major database redesign
- Breaking API changes
- Production deployment
- Destructive operations
- Paid external services
- Automatic financial transactions
- Significant product-scope changes

---

# Backlog Discipline

Agents should work from the backlog rather than inventing unrelated work.

If a task reveals missing work:

1. Document the discovery.
2. Create or propose a backlog item.
3. Continue only if the new work is required to complete the current task safely.

Do not silently expand scope.

---

# Quality Principle

A feature is complete only when:

- The implementation works.
- Acceptance criteria are satisfied.
- Relevant tests pass.
- Security concerns are addressed.
- The architecture remains coherent.
- Documentation is updated when necessary.
- No known critical issue remains.