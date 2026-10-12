# Soforr Sellable V1 — Overview Screen Specification

**Status:** Proposed UI/API contract for implementation planning. No application behavior is changed by this document.

**Product promise:** Soforr does not merely record what happened; it tells the owner what needs attention and why.

## 1. Purpose and audience

The Overview is the first screen after sign-in for an Owner or Manager. It answers, in plain language:

1. How much did we sell in the selected period?
2. What needs attention now?
3. What is at risk in stock, margin, supplier cost or customer balances?
4. What record should I open to understand or act on each item?

It is not a wall of charts, an accounting statement, or an AI-generated summary. All insights are deterministic and traceable to business records.

## 2. V1 screen hierarchy

### A. Header and period

- Heading: **Overview**
- Show the organization name and configured currency.
- Default period: the last **30 complete local calendar days**, compared with the preceding 30 complete local calendar days, matching the sales and margin report contracts.
- Display exact human-readable dates and the timezone context where it prevents ambiguity.
- Allow a small set of supported periods only if API contracts remain consistent (30 days initially; optional 7/90-day selection later). Never silently compare unequal windows.
- Show data freshness: when the combined insights were evaluated and, for any cached/offline information, its last successful sync time.

### B. “Things that need your attention” — primary section

This is the product's differentiator and should be the first substantial section, above ordinary summary metrics.

Each insight row/card contains:
- Severity text: High, Medium, Low or Info. Do not use color alone.
- A concise subject and statement grounded in facts (e.g. “Rice may run out before a new order arrives”).
- Two to four named facts, with readable units (current stock, reorder point, coverage days, lead time, overdue balance, margin movement, etc.).
- A short explanation of the rule that triggered it.
- One recommended action that navigates to the relevant existing workflow; the action must not perform a mutation automatically.
- A clear empty state: “Nothing currently needs your attention based on the available records.”
- A visible indication when cost-sensitive insights are omitted because the current user cannot view costs, without leaking the underlying cost facts.

**API prerequisite:** the Application has a combined `GetInsights` handler that evaluates registered sources and sorts results by deterministic severity, but the current API endpoint module does not expose it. Add a protected `GET /api/v1/intelligence/insights` (final route name to be confirmed during implementation) that delegates to that handler. Require `InsightsView`; only include cost-derived sources for callers with `ProductViewCost`. Do not reimplement the rules in Vue or duplicate them in an API endpoint.

The response should expose evaluation timestamp and ordered insight items with stable key/type, severity, subject, named facts, reason, and recommended-action code/label. The UI maps stable action codes to routes. Unknown action codes must fall back to a safe detail destination, not a broken link.

### C. Sales performance

Use the existing sales-summary report:
- Current-period sales revenue and completed sale count.
- Revenue change versus the preceding equal-length period, with explicit status if the prior period has no comparable sales.
- Top-selling products by quantity, with unit and revenue.
- Declining products only when both windows have sales and the defined decline threshold is met.
- A link to a future/current Sales list filtered to the relevant period/product.

Required language:
- “Sales revenue” is not “cash received.”
- A null growth rate due to no previous sales must read “No comparable previous sales,” not 0%.
- Do not invent causal explanations for a decline.

### D. Stock health

Use the existing stock/low-stock endpoints and the deterministic inventory-risk insight source:
- Count/list of products at or below configured reorder point.
- Highest-severity stock risks first: out of stock or likely to run out before lead-time restock, then ordinary low stock.
- Where present, show current quantity, reorder point, average daily sales, coverage days, lead time and suggested reorder quantity.
- Explain the basis of a suggested quantity; do not create a purchase order directly from the Overview in V1.
- Drill down to Stock detail. If the purchasing workflow is available, link to the purchase-order workflow without silently creating an order.

No recent sales is not infinite coverage and must be displayed as “No recent sales,” not as an enormous number of days.

### E. Margin and supplier-cost watch (permission-sensitive)

Use the existing margin and purchase-cost reports only when the API authorizes the caller:
- Gross margin amount and percentage with a clear “management estimate from recorded sale-line costs” label.
- Show cost coverage/data-quality state: no sales, cost not recorded, partly costed, or OK.
- Surface negative-margin products and material margin deterioration with drill-down.
- Surface meaningful supplier/product cost changes and insufficient-history states.
- Require both `InsightsView` and `ProductViewCost` for cost-derived reports. Never trust hidden controls as authorization; the API is authoritative.

Do not show net profit, accounting-grade COGS or total inventory value.

### F. Customer balances

Use the existing receivables report:
- Total outstanding, not-yet-due and overdue amounts, plus aging buckets.
- Show the organization-configured materiality threshold and currency.
- Link to the relevant customer and open sale/balance.
- Clearly label overdue balances as receivables, not cash.

### G. Navigation and operating workflow

The main navigation should evolve to match the sellable V1 workflow:
- Overview
- Sales
- Products
- Stock
- Purchasing
- Customers

Settings and Audit may be secondary destinations depending on release acceptance criteria. A route should be shown only when its workflow is implemented; avoid dead-end nav links.

The complete journey is: Product → Purchase → Receive stock → Inventory → Sale → Inventory update → Metrics → Insight → Action.

## 3. Explicitly excluded from Overview in V1

- Expenses, expense totals and net profit (expense ledger is Product P1).
- Cash balance, cashbook, bank balance or reconciliation (not implemented).
- Inventory valuation / “worth of stock” (no agreed cost basis or valuation engine).
- Forecasts, probability scores, generated AI explanations or natural-language chat.
- One-click purchase-order creation, automated mutations or stored/approvable reorder recommendations.
- Any metric whose source, period, currency, permissions or missing-data semantics are undefined.

## 4. States and reliability

Every data section must distinguish:
- Loading.
- Successful result with data.
- Successful result with no matching data.
- Forbidden / insufficient permission.
- Session expired (redirect/sign-in behavior follows the shared API client).
- API failure or network unavailable.
- Stale cached data / offline mode, with last-sync time.

One failed report must not make the whole Overview look like the business has zero activity. Prefer independent section-level error states. The combined insights endpoint currently fails as a whole if any registered source fails; preserve that behavior unless a deliberate API contract change is reviewed. Do not silently omit a broken insight source.

## 5. Acceptance criteria

1. A signed-in Owner/Manager sees Overview as the default route after sign-in.
2. The page uses existing API DTOs and shared generated OpenAPI types; no business formula is copied into Vue.
3. The attention list uses server-computed insight facts, reason, severity and action codes.
4. Every displayed number includes currency/unit and a clear period/as-of date.
5. No-comparable-history, no-sales, missing-cost and partial-cost states are not displayed as zero or as a confident conclusion.
6. Cost-derived content is absent for users without cost permission; API tests prove denial/omission behavior.
7. Every insight and metric has a working drill-down to the supporting record/list.
8. Loading, empty, forbidden, expired-session, failure and offline/stale states are covered by component/browser tests.
9. Tenant isolation remains server-enforced; the UI never supplies an organization id as authority for data scope.
10. No expenses, cash balance, net profit, inventory valuation, AI or automatic purchasing claims appear.
11. Tests verify stable severity ordering and stable action-code routing; unknown codes fail safely.
12. The current product and stock screens' error/loading/empty conventions are reused consistently.

## 6. Implementation sequence

1. Add the combined insights HTTP endpoint and endpoint-level integration tests; regenerate the OpenAPI snapshot/types.
2. Add an Overview API composable/service that only orchestrates typed API calls.
3. Build the primary attention list and the sales/stock/receivables summary sections.
4. Add cost-sensitive margin/purchase-cost panels with permission-aware loading and explicit data-quality states.
5. Add the Overview route, make it the signed-in landing route, and update navigation.
6. Add drill-downs only to implemented workflows; as Sales/Purchasing/Customers screens arrive, connect the relevant action codes.
7. Add browser tests for each state and for owner/manager/staff access; validate the end-to-end journey.

**Implementation guardrail:** follow the master backlog sequence and do not jump ahead of dependencies. This specification defines the target; it does not authorize silently changing the accepted V1 scope or skipping review/tests.
