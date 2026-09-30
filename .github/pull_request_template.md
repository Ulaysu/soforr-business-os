## Summary

<!-- What and why. Link the backlog item / issue (e.g. CATALOG-003). -->

## Changes

-

## Testing

<!-- Commands run and results. Be honest about anything not run or failing. -->

- [ ] `dotnet build` (no new warnings)
- [ ] `dotnet test`
- [ ] `dotnet format --verify-no-changes`

## Security considerations

<!-- AuthN/AuthZ, tenant isolation, input validation, secrets, logging of sensitive data, new dependencies. Write "none" only if truly none. -->

## Architecture considerations

<!-- Dependency direction respected? Business logic only in Domain/Application? Any deviation needs an ADR and human approval. -->

## Migrations

- [ ] No database changes
- [ ] Migration added and applied to a clean database; existing data, indexes, constraints and tenant scoping considered

## Breaking changes

- [ ] None
- [ ] Yes (requires human approval): <!-- describe -->

## Checklist

- [ ] Acceptance criteria of the backlog item are met
- [ ] Follows [Definition of Done](../docs/engineering/definition-of-done.md)
- [ ] Docs / OpenAPI / ADR updated where needed
- [ ] No secrets committed
- [ ] Diff reviewed for unintended changes
- [ ] Human approval boundaries checked (architecture, auth, tenancy, paid services, deployment, ...)
