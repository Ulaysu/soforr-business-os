# Module Boundaries

Modules are namespaces/folders inside the existing projects, not separate deployables. Each module owns its data. Other modules use its Application-level queries/interfaces and never write to its tables.

A module may depend only on modules listed under "Depends on". Cycles are forbidden.

The model is deliberately minimal; entities are named for orientation and refined in their backlog items.

---

## Identity
- **Responsibility:** Authenticate users; roles and permissions.
- **Concepts:** User, Role, Permission, Membership (user ↔ organization with a role).
- **Commands:** RegisterUser, SignIn, AssignRole, DeactivateUser.
- **Queries:** GetCurrentUser, ListMembers, GetEffectivePermissions.
- **Invariants:** Email unique; passwords never stored in plain text; one role per membership; an organization always keeps at least one Owner.
- **Depends on:** Organizations.

## Organizations
- **Responsibility:** Tenants and their settings.
- **Concepts:** Organization (name, currency, timezone).
- **Commands:** CreateOrganization, UpdateOrganizationSettings.
- **Queries:** GetOrganization.
- **Invariants:** Every tenant-owned record in any module references exactly one organization. The name is required (at most 200 characters, trimmed); the time zone is an IANA identifier supported by the application's runtime time zone database; the currency is one of Soforr's supported ISO 4217 currencies (a maintained snapshot of the current set) and is fixed at creation, so it is never silently changed once transactions exist; timestamps are UTC.
- **Depends on:** none.

## Catalog
- **Responsibility:** What the business buys and sells.
- **Concepts:** Product (SKU, name, unit of measure, selling price, cost price, reorder settings, active flag).
- **Commands:** CreateProduct, UpdateProduct, DeactivateProduct.
- **Queries:** GetProduct, SearchProducts.
- **Invariants:** SKU unique within organization; prices ≥ 0; a product with history is deactivated, never hard-deleted.
- **Depends on:** Organizations.

## Inventory
- **Responsibility:** Stock quantities, derived from movements.
- **Concepts:** InventoryMovement (append-only: product, signed quantity, reason, source reference, actor, timestamp), StockLevel (current quantity per product, maintained transactionally with movements).
- **Commands:** AdjustInventory (manual, requires reason); RecordMovement (internal, called by Purchasing/Sales handlers).
- **Queries:** GetStockLevel, ListLowStock, GetMovementHistory.
- **Invariants:** Every change has a reason and a movement; movements are immutable (corrections are new movements); stock level equals the sum of movements; stock cannot go negative by default; updates are concurrency-safe.
- **Depends on:** Catalog, Organizations.

## Purchasing
- **Responsibility:** Buying stock from suppliers and receiving it.
- **Concepts:** Supplier, PurchaseOrder (draft → placed → partially received → received / cancelled), PurchaseOrderLine.
- **Commands:** CreateSupplier, CreatePurchaseOrder, PlacePurchaseOrder, ReceivePurchase, CancelPurchaseOrder, ApproveReorder (creates a purchase order from a recommendation).
- **Queries:** GetPurchaseOrder, ListPurchaseOrders, ListSuppliers.
- **Invariants:** Receiving is idempotent per receipt (no double stock); received quantity cannot exceed what the order allows; totals computed by deterministic code; receiving writes Inventory movements in the same transaction.
- **Depends on:** Catalog, Inventory, Organizations.

## Sales
- **Responsibility:** Recording sales and their effect on stock.
- **Concepts:** Sale, SaleLine, payment status (recorded only; no payment processing), optional Customer reference.
- **Commands:** CreateSale, VoidSale, RecordReturn (after the first slice).
- **Queries:** GetSale, ListSales, GetSalesSummary.
- **Invariants:** Prices and totals computed server-side; completing a sale writes Inventory movements atomically; a completed sale is never edited, only voided/returned via new records.
- **Depends on:** Catalog, Inventory, Customers (optional reference), Organizations.

## Customers
- **Responsibility:** Who the business sells to.
- **Concepts:** Customer (name, contact details).
- **Commands:** CreateCustomer, UpdateCustomer, DeactivateCustomer.
- **Queries:** GetCustomer, SearchCustomers.
- **Invariants:** Belongs to one organization; personal data minimized; customers with sales are deactivated, not deleted.
- **Depends on:** Organizations. Sales references Customers (not the reverse); customer activity is exposed by a Sales query consumed by Reporting/Intelligence.

## Expenses
- **Responsibility:** Non-inventory business costs.
- **Concepts:** Expense (amount, category, date, optional receipt file).
- **Commands:** CreateExpense, UpdateExpense, VoidExpense.
- **Queries:** ListExpenses, GetExpenseSummary.
- **Invariants:** Amount > 0; currency matches organization; voided rather than deleted.
- **Depends on:** Organizations, Files (optional).

## Reporting
- **Responsibility:** Verified read-only summaries.
- **Concepts:** Read models over other modules' data.
- **Commands:** none.
- **Queries:** SalesReport, StockReport, ExpenseReport.
- **Invariants:** Read-only, deterministic, tenant-scoped.
- **Depends on:** Sales, Inventory, Purchasing, Expenses (via their queries).

## Notifications
- **Responsibility:** Telling the owner what needs attention.
- **Concepts:** Notification (recipient, type, reference, read state); in-app channel first.
- **Commands:** CreateNotification, MarkNotificationRead.
- **Queries:** ListNotifications.
- **Invariants:** Notifications reference verified facts, not model output; recipient must be a member of the organization.
- **Depends on:** Identity, Organizations.

## Automation
- **Responsibility:** Safe, rule-based routine operations with execution history.
- **Concepts:** AutomationRule, AutomationRun.
- **Commands:** EnableRule, DisableRule, ExecuteRule (system).
- **Queries:** ListRules, ListRuns.
- **Invariants:** Automations invoke only pre-approved safe commands; financial or high-impact actions require human approval; every run is recorded.
- **Depends on:** Other modules through explicit Application commands; Notifications.

## Intelligence
- **Responsibility:** Understanding activity: forecasting, recommendations, later the AI assistant.
- **Concepts:** StockForecast, ReorderRecommendation (product, projected stock-out date, suggested quantity, rationale, status: pending / approved / dismissed), AI tools.
- **Commands:** GenerateReorderRecommendations, DismissRecommendation. Approval invokes `Purchasing.ApproveReorder`; Intelligence itself never writes purchases or stock.
- **Queries:** GetInventoryForecast, ListRecommendations; AI tools (GetSalesSummary, GetLowStockProducts, ...).
- **Invariants:** Forecasts are deterministic and explainable (simple sales-velocity model in MVP); AI output is never stored as a financial fact; an approved recommendation is traceable to the resulting command and approving user.
- **Depends on:** Inventory, Sales, Purchasing, Catalog, Notifications. See [ai-architecture.md](ai-architecture.md).

## Files
- **Responsibility:** Storing and serving attachments (receipts, invoices).
- **Concepts:** StoredFile (metadata, owner reference, content type, size, storage key).
- **Commands:** UploadFile, DeleteFile (soft).
- **Queries:** GetFileMetadata, DownloadFile.
- **Invariants:** Tenant-scoped access on every read; type and size validated server-side; storage keys never derived from user-supplied names.
- **Depends on:** Organizations.

---

## Dependency Overview

```text
Organizations ← Identity
Organizations ← Catalog ← Inventory ← Purchasing
                            ↑  ↑
                          Sales ┘ ── Customers (optional reference)
Sales / Inventory / Purchasing ← Reporting, Intelligence
Intelligence → Notifications ; Automation → any module via commands
Files ← Expenses
```
