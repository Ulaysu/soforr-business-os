# Definition of Done

A backlog item is Done only when **all** applicable points hold. "It compiles" is not done. Anything intentionally left incomplete is stated explicitly in the pull request and the backlog.

## Checklist

1. **Implementation:** the described behavior actually works and every acceptance criterion in the backlog item is met.
2. **Tests:** meaningful automated tests exist for new behavior (per the item's testing requirements); the full suite passes.
3. **Build hygiene:** `dotnet build` has no new warnings; `dotnet format --verify-no-changes` passes; CI is green.
4. **Security review:** authentication/authorization enforced server-side; tenant scoping verified; inputs validated; no secrets committed; no sensitive data in logs or error responses; new dependencies checked. Result recorded in the PR template.
5. **Architectural coherence:** dependency direction respected; no business logic in endpoints or Vue; no rule duplicated across layers; no unapproved architecture change.
6. **Data integrity:** migrations reviewed and applied to a clean database; constraints/indexes considered; transactions and concurrency handled where stock or money is involved.
7. **Observability:** important business actions logged/audited; errors handled through the central mechanism.
8. **Documentation:** docs, OpenAPI, and ADRs updated where behavior, architecture or conventions changed.
9. **Verification:** the agent actually ran the code (tests and, where applicable, the API/UI) and reports results honestly, including failures, warnings and known gaps.
10. **Review:** diff inspected for unintended changes; PR filled in completely; human approval obtained before merge.
11. **No known critical defects** remain.
12. **Intelligence items:** the formula, comparison period and thresholds are documented; the result is reproducible from the data and a fixed clock; every insight shows its facts and reason; rules are covered by tests with fixtures including the edge cases (no data, zero values, period boundaries); cost-derived output respects `ProductViewCost`; nothing computed by intelligence overrides a transaction.

Sellable V1 as a whole is done only when its [exit criteria](../product/sellable-v1.md#v1-exit-criteria) hold.

## Never Claim Done When

- tests were not run or are failing or skipped;
- a migration was not applied;
- a security concern is unresolved;
- acceptance criteria are only partially met without saying so.
