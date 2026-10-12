# Soforr Business OS — API/UI Traceability Audit

**Status:** Initial source-level audit  
**Baseline:** `main` source inspected before this document was created  
**Purpose:** Keep the business questions, source records, API behavior, UI, and verification status traceable in both directions.

> This document is not proof of end-to-end behavior. “Present in source” means an implementation was found in the inspected source. Runtime behavior, complete authorization coverage, migrations, and browser workflows must be verified separately. Update this audit as code changes.

## 1. Status vocabulary

- **Source found** — relevant endpoint or screen exists in inspected source.
- **Tests found / needs confirmation** — tests should be inspected and mapped to the behavior; this audit does not claim a test passed unless recorded with evidence.
- **UI absent** — no corresponding route/screen was found in the inspected current Vue router.
- **Partial / verify** — some support exists, but the complete user workflow or semantics need further inspection.
- **Not established** — the inspected files did not establish the capability. Do not interpret this as proof that no related code exists anywhere in the repository.

## 2. Forward traceability: business question → records → API → UI

| ID | Business question / decision | Source records and calculation needed | API source found | Current UI finding | Gap / next action |
|---|---|---|---|---|---|
| F-01 | What did we sell in the selected period? | Non-voided sales and sale lines; period and comparison window must use organization calendar rules | Intelligence: `GET /sales-summary`; Sales list/detail | No Overview or sales route in current router | Build Overview card and sales detail route; verify date-window and timezone semantics in handler/tests |
| F-02 | Which products sell best, are slow-moving, or declining? | Sale-line quantities/revenue across comparable windows | Intelligence: `GET /sales-summary` | No intelligence screen | Surface a ranked list with period and explanation; verify definitions of “slow mover” and “declining” |
| F-03 | What gross margin are we earning? Which products are at a loss? | Sale-line selling price and recorded cost; margin = revenue minus recorded line cost; percentage and comparison rules | Intelligence: `GET /margins` | No intelligence screen | Show margin, not net profit; identify missing cost records; inspect permission behavior for InsightsView and ProductViewCost |
| F-04 | Are supplier costs changing? | Placed purchase orders, product/supplier unit costs, quantities and spend across completed local days | Intelligence: `GET /purchase-costs`; Purchasing orders | No purchasing/intelligence screen | Show latest-vs-prior cost and supplier comparison; distinguish insufficient history from no change |
| F-05 | How much do customers owe, and what is overdue? | Credit sales, due dates, payments and reversals; aging buckets in organization calendar | Intelligence: `GET /receivables`; Customers: `GET /{id}/balance` | No customer/receivables screen | Build receivables list with due/overdue breakdown and links to underlying sales; verify reversal treatment |
| F-06 | What stock do we have? | Active products and stock movement ledger; never-stocked product returns zero | Inventory: `GET /stock`, `GET /stock/{productId}` | Stock and product stock detail routes exist | Verify paging, search, zero-stock display and UI error/loading states |
| F-07 | Which products are at or below reorder point? | Current stock versus configured reorder point for active products with reorder settings | Inventory: `GET /low-stock` | Stock screen has low-stock filter | Ensure count/list agree with endpoint; don't imply reorder quantity is calculated |
| F-08 | Why did stock change? | Append-only movement history and required adjustment note | Inventory: `POST /adjustments`, `GET /stock/{productId}/movements` | Stock product detail exists | Verify adjustment form and movement history expose reason, date and actor as permitted |
| F-09 | What sales were recorded, voided or paid? | Sale headers/lines, status, payment records and reversals | Sales: `POST /`, `GET /`, `GET /{id}`, `POST /{id}/void`, `POST /{id}/payments`, `POST /{id}/payments/{paymentId}/reverse` | No sales UI route | Build sales list/detail and guarded actions; preserve reason requirements and idempotency/conflict handling |
| F-10 | What goods are on order or have arrived? | Suppliers, purchase order status/lines and received quantities | Purchasing: supplier, order, place/cancel and receipt endpoints | No purchasing UI route | Build supplier/order workflows; distinguish ordered quantities from received stock |
| F-11 | Who are our customers and what are their credit terms? | Customer profile, active status, credit terms and balances | Customers: create/list/get/update/deactivate/balance | No customers UI route | Build customer screens; keep contact data hidden unless permission allows |
| F-12 | Who made a sensitive change? | Organization-scoped audit events, actor, event, timestamp and filters | Audit: list audit events | No audit UI route | Decide if owner-facing audit history is in sellable v1; enforce Owner-only visibility |
| F-13 | Which organization/timezone and threshold apply? | Organization name, timezone, overdue materiality threshold; currency cannot be changed | Organization: get and update settings | No settings route found in current router | Build settings screen or explicitly defer; verify timezone affects period and aging displays |
| F-14 | Can the user keep selling during connectivity loss? | Offline grant, server-observed contact gaps, offline sale/payment payloads and sync results | Offline: grant issue/contact; sales code accepts validated offline events | No offline UI workflow established | Treat as a dedicated workflow; verify client persistence, sync, recovery, and error UX before claiming offline-ready |
| F-15 | What are operating expenses and net profit? | Expense transactions/categories and rules for classifying costs | No complete expense-recording workflow established by this source review | No expense UI route found | Scope a real expense ledger before displaying expenses or net profit |
| F-16 | How much cash is actually available? | Opening balance, cash receipts/payments, expenses, transfers and reconciliations | No complete cash ledger established by this source review | No cash UI route found | Do not label revenue or receivables as cash; define a ledger/reconciliation model if required |
| F-17 | What is inventory value? | On-hand quantities multiplied by a clearly defined cost basis, with missing-cost policy | Stock API exists; margin and purchase-cost intelligence exists | No inventory valuation screen established | Verify whether a supported valuation endpoint/calculation exists before exposing an inventory-value metric |
| F-18 | What should I do next? | Rules combining low stock, declining sales, margin risk, overdue balances and confidence/data-quality checks | Several separate intelligence/inventory endpoints exist | No Overview screen | Begin with deterministic, explainable alerts; link each alert to source records and avoid unsupported causal claims |

## 3. Backward traceability: API capability → user-facing destination

The endpoint paths below are **module-relative route templates** taken from endpoint source summaries. Confirm route-group prefixes in endpoint mapping before treating them as complete external URLs.

| API module / capability | Main behavior found in source | Current frontend destination | Decision |
|---|---|---|---|
| Catalog / Products | Create, list/search, get, update, deactivate; cost field is permission-sensitive | `/products`, `/products/new`, `/products/:id/edit` | Keep; map every field and validation error |
| Inventory / Stock | Stock list, single-product stock, low-stock list, adjustment, movement history | `/stock`, `/stock/:id` | Keep; verify adjustment/history coverage |
| Sales | Create/list/get/void sale; record payment; reverse payment | None found | Add Sales list/detail and action flows |
| Purchasing | Suppliers; draft orders; place/cancel; receive goods | None found | Add Suppliers and Purchase Orders flows |
| Customers | Create/list/get/update/deactivate and computed balance | None found | Add Customers and customer balance screens |
| Intelligence / Sales summary | Revenue and quantity over N complete local days vs preceding N days; top sellers, slow movers, declining products; voided sales excluded | None found | Add Overview and sales insight detail |
| Intelligence / Margins | Gross margin based on recorded cost at sale; product margin movement, loss-making products and missing costs | None found | Add margin insight; permission and data-quality states required |
| Intelligence / Purchase costs | Latest cost vs supplier's prior purchase, supplier gap, volume/spend; insufficient history called out | None found | Add purchasing insight |
| Intelligence / Receivables | Outstanding, not-yet-due, overdue aging buckets and most overdue customers/sales | None found | Add receivables view |
| Audit | Owner-only paginated/filterable audit events | None found | Add owner-only audit page or explicitly defer |
| Organizations | Read organization; update name/timezone/overdue materiality threshold; currency immutable | None found | Add settings page or explicitly defer |
| Offline | Issue offline grant and record device contact; sale endpoint validates event time/grant | None found | Requires explicit offline-capable client and sync UX; API presence alone is insufficient |
| Identity/session | Sign-in/session and sign-out experience exists; auth endpoint mapping is environment-specific per Program.cs comments | Sign-in and app shell | Verify session expiry, permission-denied behavior, production signup/verification and route redirects |
| Health/readiness/OpenAPI | Hosting health checks; OpenAPI/Scalar mapped in Development | Operational tooling, not primary business UI | Document deployment/operations checks separately |

## 4. Overview metric contracts (proposed)

These are the minimum contracts to settle before implementing Overview components.

| Metric / insight | Source of truth | Must display | Must not imply |
|---|---|---|---|
| Sales revenue | Sales summary endpoint | Currency, selected period, comparison period, loading/error/empty state | Cash collected or profit |
| Quantity sold / top products | Sales summary endpoint | Period and quantity/revenue definition | Inventory on hand |
| Gross margin | Margin endpoint | Amount, percentage, comparison and missing-cost state | Net profit or audited accounting result |
| Low-stock count | Low-stock endpoint or consistent paginated aggregation | Threshold definition and link to affected items | Exact suggested purchase quantity |
| Receivables | Receivables endpoint | Total outstanding, not-yet-due, overdue and aging context | Cash balance |
| Purchase cost change | Purchase-cost endpoint | Supplier/product, current and previous unit costs, history sufficiency | General inflation or supplier fault |
| Declining products | Sales summary endpoint | Comparison window and basis for decline | A causal explanation for the decline |

**Currency:** use the organization's configured currency.  
**Time:** use the organization's timezone/calendar semantics; show the period in human-readable form.  
**Permissions:** never infer access from a hidden UI element alone; the API remains authoritative.  
**Data quality:** missing cost, insufficient history, empty results, forbidden access and API failure are different states. Do not turn them all into zero.  
**Navigation:** every alert/card should open a filtered list or detail view showing the records behind the result.  
**Accessibility:** values need readable labels and explanations; color alone must not convey severity.

## 5. Gaps and recommended order

### P0 — establish reliable contracts
1. **Completed in §9:** intelligence handlers, gathering/calculation rules, DTOs, and integration-test source files have been inspected. The tests were not run in this audit.
2. **Completed in §9:** the four intelligence route prefixes and query parameters were confirmed from endpoint mappings and generated OpenAPI types.
3. **Partially completed in §9:** the typed Vue API client and generated schemas were inspected; no active Vue consumer for the four intelligence reports was found. Product/Stock UI state handling still needs component/test review.
4. Still confirm what expense, cash-ledger and inventory-valuation code exists elsewhere in the repository before classifying those capabilities as missing.

### P1 — close the most valuable existing API/UI gap
1. Add an Overview route and screen backed only by existing intelligence and low-stock endpoints.
2. Build Sales list/detail and payment/void actions.
3. Build Customers and receivables screens.
4. Build Purchasing and goods-receipt screens.
5. Add margin and purchase-cost insights.
6. Decide on audit/settings screens based on target customer needs and role model.

### P2 — extend the business model only where justified
1. Expense recording and categories, with explicit treatment of refunds and reversals.
2. Cash ledger and reconciliation if Soforr intends to report actual available cash.
3. Inventory valuation with an explicit cost basis and missing-cost behavior.
4. Reorder suggestions that account for lead time, demand and open purchase orders.
5. Complete offline-client persistence/synchronization verification if offline selling is a v1 promise.

## 6. Verification register

Do not mark a row complete until evidence is recorded.

| Check | Evidence to record | Status |
|---|---|---|
| Intelligence calculation contracts traced | Handlers, gatherers, calculators and integration-test source inspected; test execution not performed | Source traced; execution pending |
| Endpoint route prefix and version are confirmed | Endpoint group mapping and generated OpenAPI schema | Confirmed in source |
| Frontend intelligence client usage | Typed client and generated schema inspected; no Vue consumers found for intelligence operations | No consumer found; Overview/UI implementation pending |
| Every visible screen handles loading, empty, error and forbidden states | Component tests / browser workflow | Pending |
| Role and organization isolation are enforced server-side | Authorization tests + cross-organization integration tests | Pending |
| Overview values link to supporting records | Browser test per card/alert | Pending |
| Expense/cash/inventory valuation scope is resolved | Repository search + product decision | Pending |
| Full priority workflows pass end-to-end | CI evidence and reproducible test scenario | Pending |

## 7. Repository pointers inspected

- `src/Soforr.Api/Program.cs` — maps Catalog, Customers, Audit, Inventory, Purchasing, Sales, Intelligence and Offline endpoint modules.
- `src/Soforr.Api/Intelligence/IntelligenceEndpoints.cs` — sales summary, margins, purchase costs and receivables.
- `src/Soforr.Api/Sales/SalesEndpoints.cs` — sale creation/list/detail/void, payment and reversal behavior.
- `src/Soforr.Api/Purchasing/PurchasingEndpoints.cs` — suppliers, purchase orders and goods receipts.
- `src/Soforr.Api/Customers/CustomerEndpoints.cs` — customer lifecycle and balances.
- `src/Soforr.Api/Inventory/InventoryEndpoints.cs` — stock levels, low stock, adjustments and movement history.
- `src/Soforr.Api/Audit/AuditEndpoints.cs` — audit history.
- `src/Soforr.Api/Organizations/OrganizationEndpoints.cs` — organization settings.
- `src/Soforr.Api/Offline/OfflineEndpoints.cs` — offline grant/contact endpoints.
- `src/Soforr.Web/src/router/index.ts` — current route list; root redirects to products.
- `src/Soforr.Web/src/App.vue` — current navigation shows Products and Stock.

## 8. Change policy

When an API contract, business calculation, permission, or screen changes, update this document in the same PR. Add a test or explicitly explain why a test is not applicable. Never mark a capability “working” solely because an endpoint or component file exists.

## 9. Verified intelligence contracts (source and test inspection)

**Evidence level:** application handlers, calculation/gathering code, endpoint mappings, generated OpenAPI types, and integration-test source were inspected on `main`. The tests were **not executed as part of this audit**, so this section records implemented rules and existing test coverage, not a fresh passing test run.

### 9.1 Sales summary — `GET /api/v1/intelligence/sales-summary?days=&top=`

- Defaults to 30 complete local calendar days and compares with the immediately preceding equal-length period; `days` must be 1–92 and `top` 1–50.
- Uses the organization's timezone and returns both periods' local dates and UTC boundaries, currency, totals, sale counts, top sellers, slow movers and declining products.
- Only completed, non-voided sales count. Revenue sums the Domain-computed sale-line totals, so it should reconcile with recorded sales rather than a separate approximate formula.
- Revenue growth is `(current − previous) / previous × 100`, rounded to one decimal place. If previous revenue is zero, growth is null and `growthStatus` is `no_comparable_previous_sales`; it is not represented as zero growth.
- Top sellers rank by quantity, then revenue, name and ID; quantities remain per product because units such as bags and kilograms are not comparable.
- Declining products must have sales in both periods and a quantity decrease of at least 20%. Slow movers are active products created before the current window, ranked by quantity; with fewer than five qualifying products the whole list is shown.
- Requires `InsightsView`; Owner/Manager policy is enforced in the application permission model.

**Test sources found:** `SalesSummaryTests.cs` covers calculation/period behavior and reconciliation against raw SQL; `SalesSummaryApiTests.cs` covers the HTTP/OpenAPI contract. Source inspection only; not run in this audit.

### 9.2 Gross margin — `GET /api/v1/intelligence/margins?days=&top=`

- Uses the same 30-day default, 92-day maximum, local-calendar comparison windows and completed-sale exclusions as sales summary.
- Margin is a management measure: revenue actually charged minus the cost price captured on each sale line at sale time. It does **not** implement FIFO, weighted-average costing or inventory valuation.
- A recorded cost of zero means cost is untracked. Those lines are excluded from the margin calculation and reported as revenue/quantity without recorded cost. DTO statuses distinguish `no_sales`, `cost_not_recorded`, `partly_costed` and `ok`; a missing margin is not guessed.
- Margin percentage is rounded to one decimal place; margin change is a difference in percentage points and is only available when both periods have a margin percentage. Average unit price/cost use costed lines.
- The report includes top products, negative-margin products, declining-margin products and products with missing/partial cost. Declining margin uses a five-percentage-point threshold; negative margin takes precedence in the attention insight.
- Requires both `InsightsView` and `ProductViewCost`. Do not show this endpoint's data to someone who cannot view costs.

**Test sources found:** `MarginTests.cs` includes raw-SQL reconciliation and calculation cases; `MarginApiTests.cs` covers the HTTP/OpenAPI contract. Source inspection only; not run in this audit.

### 9.3 Purchase costs — `GET /api/v1/intelligence/purchase-costs?days=&top=`

- Defaults to a 30-complete-local-day volume window, with `days` 1–92 and `top` 1–50. Cost comparison history is bounded to the preceding 365 days.
- Counts lines from placed, partially received or received purchase orders. Draft and cancelled orders are excluded. The comparison date is when the order was placed, not when stock was received.
- Compares each product's latest costed purchase against the previous costed purchase from the same supplier. A zero unit cost is treated as unrecorded and excluded from comparisons. One costed purchase returns `insufficient_history`, not “no change.”
- A change or supplier gap is notable at an exact 10% threshold; the displayed percentage is rounded separately. Recent volume includes order count, quantity and spend. The DTO includes the threshold, lookback, currency and timezone.
- Requires both `InsightsView` and `ProductViewCost`.

**Test sources found:** `PurchaseCostsTests.cs` includes real-PostgreSQL and raw-SQL volume reconciliation cases; `PurchaseCostsApiTests.cs` covers the HTTP/OpenAPI contract. Source inspection only; not run in this audit.

### 9.4 Receivables — `GET /api/v1/intelligence/receivables?top=`

- Evaluates balances as of the current clock time; returns totals for all customers even though the customer list is capped. The default list size is 25, maximum 100.
- Only completed sales made on account with a positive outstanding balance count. Walk-in and voided sales do not count.
- Payments reduce only the sale they were recorded against; reversing a payment restores the balance of that same sale. No automatic reallocation across a customer's other sales occurs.
- A due instant strictly earlier than now is overdue. Aging uses whole local calendar days in the organization's timezone and buckets overdue balances into 0–30, 31–60, 61–90 and 91+ days.
- The response includes currency/timezone, as-of time, configured materiality threshold, outstanding/not-yet-due/overdue totals, aging buckets, counts of customers with balances/overdue balances, and customers with their open-sale counts and overdue-sale details.
- Requires `InsightsView`; viewing receivables does not require cost visibility.

**Test sources found:** `ReceivablesTests.cs` includes real-PostgreSQL balance reconciliation and aging cases; `ReceivablesApiTests.cs` covers the HTTP/OpenAPI contract. Source inspection only; not run in this audit.

### 9.5 What the Vue layer currently has — and does not have

- The shared `src/api/client.ts` is a typed `openapi-fetch` client generated from the committed OpenAPI snapshot. It carries the session cookie, adds antiforgery headers to unsafe methods, and calls the configured unauthorized handler on 401 responses.
- `src/api/schema.d.ts` includes generated request/response types for all four intelligence operations. That proves the contract is represented in generated types, **not** that a screen calls the endpoint.
- Search of frontend source found no active component/store call for these four intelligence operations. The router currently exposes sign-in, products and stock; `App.vue` navigation exposes Products and Stock. There is no Overview, Sales, Purchasing, Customers or Intelligence route yet.
- Therefore, the current gap is not “invent the business formulas first.” It is to build the right screen over these verified contracts, then add browser/component coverage for loading, empty, forbidden, error and drill-down behavior.

### 9.6 Overview design implications

1. **Lead with plain-language outcomes**, e.g. “Sales were GMD X over 30 complete days, up/down Y% versus the previous 30 days,” rather than requiring owners to interpret a chart.
2. If previous revenue is zero, show “No comparable previous sales” instead of a growth percentage.
3. Display gross margin only when the caller has cost permission; make partial/unrecorded cost visible and never label margin as net profit.
4. Present overdue balances separately from sales revenue and cash. Make aging and due dates visible.
5. Treat insufficient supplier history as a real state. Don't call a price stable based on one purchase.
6. Make every metric actionable with a drill-down into the sales, products, purchase orders or customer balances behind it.
7. Keep currency and period labels visible, and distinguish no activity from failed loading or denied access.

