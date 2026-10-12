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
1. Inspect intelligence application handlers and tests to record exact DTO fields, date boundaries, time-zone behavior, permission requirements, and calculation edge cases.
2. Confirm route-group prefixes and API version prefix from endpoint mapping and OpenAPI output.
3. Trace current Vue API clients, shared types, error handling and tests for Products and Stock.
4. Confirm what expense, cash-ledger and inventory-valuation code exists elsewhere in the repository before classifying those capabilities as missing.

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
| Each intelligence calculation matches its documented contract | Handler path + relevant test names/assertions | Pending |
| Endpoint route prefix and version are confirmed | Endpoint group mapping / generated OpenAPI path | Pending |
| Frontend clients map to exact request/response DTOs | Client module + type definitions | Pending |
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
