# Intelligence conventions

How deterministic intelligence is built (Sellable V1, layer 1; see [sellable-v1.md](../product/sellable-v1.md) and [architecture.md](../architecture/architecture.md#deterministic-intelligence)). Every INTEL item follows these rules. If a rule here is in the way, change this document first, with approval, not the code quietly.

## What it is

An **insight** is a finding about the business, derived from authoritative records by an explicit rule. It is never the record of what happened and is never stored as a source of truth.

- **Computed on request.** `GetInsights` reads the records and the clock and returns the current list. There is no insight table, nothing to go stale and nothing that can disagree with a transaction. (If a later item materializes anything, only state such as "dismissed" is stored, never the facts, and that choice gets an ADR.)
- **Gathering is separate from judging.** A *source* loads exactly the records a rule needs (read-only, through the tenant-filtered `IAppDbContext`, `AsNoTracking`, never saving). A *rule* is a pure function of a typed input snapshot, explicit thresholds and, when it needs time, the clock passed in. A rule touches no database, network or ambient clock, so it is tested with fixtures and a fixed clock.
- **Every insight is traceable.** `Insight.Create` refuses an insight without facts, a reason, a subject or an action, so nothing is shown that cannot be traced to records.

## The structure (`Soforr.Application/Intelligence`)

| Piece | What it is |
|---|---|
| `Insight` | type (stable dotted code), severity, subject (kind, id, name), named facts, one-sentence reason, recommended action. `Key` (type + subject) is stable for the same data. |
| `InsightFact` | a named value as invariant-culture text (no grouping, dot as decimal separator, no trailing zeros) with an optional unit. Presentation is the client's job. |
| `InsightAction` | a stable `Code` the client maps to a screen (for example `create_purchase_order`) and a plain `Label`. The target is the insight's subject. |
| `IInsightSource` | gathers records for one capability and calls its rules. Declares `RequiresProductViewCost`. |
| `InsightWindow` | a comparison period: whole local days, UTC bounds, half-open. |
| `Rules/` | the pure rules (for example `InventoryRiskRule`). |
| `Sources/` | the sources (for example `InventoryRiskInsightSource`). |
| `GetInsights` | the query: checks `InsightsView`, runs every registered source, orders the result. |

**Adding a capability** (INTEL-002 onwards): write the pure rule and its tests, write a source that gathers its input, done. Sources are found by scanning the assembly (`AddInsightSources`), so nothing is listed by hand and only capabilities that exist appear on the attention list.

## Ordering and severity

Severity is a fixed scale, shown highest first: **High** (costing or about to cost money now, for example out of stock), **Medium** (needs action soon), **Low** (worth knowing), **Info** (an observation, no action required). The attention list is ordered by severity, then insight type, then subject name (ordinal), then subject id, then key, so the same data always gives the same order. Each rule documents its own severity assignment and its own internal order.

## Formulas, periods and thresholds

Every rule documents, next to the code (on the rule's type) and in its backlog item:

1. **The formula**, in words and symbols, with units.
2. **The comparison period.** A window is **N complete local calendar days before today** (`InsightWindows.TrailingCompleteDays`), or "the immediately preceding period of equal length", half-open `[start, end)`, where "today" is the day of the evaluation time (the clock passed in) in the **organization's time zone**, not the server's. Today's partial day is left out on purpose, so the same data gives the same answer at any time of the day. Windows are whole local days, so across a clock change a day is 23 or 25 hours; a local midnight that does not exist starts the day when the clocks move forward, one that happens twice starts at its first occurrence. The window is passed to the rule and shown in the insight's facts (`window`, `window_days`). A period with no data is "no data", never zero or infinity.
3. **The thresholds**, as named constants or a thresholds record with the default stated (for example "notable cost change = at least 10% relative change"; "slow movers = bottom 20% by quantity, at least 5 qualifying products"). No hidden monetary or percentage threshold; an organization-configurable one is stated as such.
4. **The edge cases**: no history, zero velocity, zero stock, inactive products, missing inputs. Missing inputs yield no insight or an explicit "not enough data", never a guess, and never a suggestion computed from missing numbers.
5. **Rounding.** Calculations use `decimal`. Round only where a rule says so, state the places and the mode (half away from zero, as `Money.Multiply`), and round once at the end.

Every rule has tests with a **fixed clock** and fixtures for each edge case, an ordering test, and a determinism test (same input, same output, whatever the input order). Rules that read records also get an integration test on real PostgreSQL, a tenant-isolation test, and, where a figure is also shown elsewhere, a **reconciliation test** (for example the reorder-point insights name exactly the products of the low-stock list).

## Permissions and tenancy

- `InsightsView` (Owner and Manager) is required to see any insight. Staff do not see the attention list.
- A source whose insights reveal costs or margins sets `RequiresProductViewCost`; the evaluation runs it only for a caller who holds `ProductViewCost`, so a source never has to check that itself. `MarginInsightSource` (INTEL-004) is the first cost source; `PurchaseCostInsightSource` (INTEL-006) is the second. `ReceivablesInsightSource` (INTEL-005) is not a cost source: it needs `InsightsView` only. *Test gap:* today every role that holds `InsightsView` also holds `ProductViewCost`, so the "skipped" half of that gate cannot be exercised through a real role; the day a role holds `InsightsView` without `ProductViewCost`, add the test for it.
- Sources read only through the tenant-filtered context; another organization's records cannot reach a rule. Every source gets an isolation test.

## What is out of scope

No LLM, agent, vector store, forecasting infrastructure, stored recommendations or approvals in V1 (INTEL-008 and INTEL-009 are PRODUCT-P1). Later layers call these same queries as their controlled tools ([ai-architecture.md](../architecture/ai-architecture.md)).
