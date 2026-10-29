# Invoice Capture: Decision Record and Proposed Minimal Workflow

**Status: PROPOSED. The owner's decision is pending.** Nothing in this document is approved, scheduled or implemented. It defines the question precisely and drafts the smallest workflow so the owner can decide with facts. Decision thread: [issue #69](https://github.com/Ulaysu/soforr-business-os/issues/69).

**Authority.** This document does not change [sellable-v1.md](sellable-v1.md), the backlog or any accepted ADR. Until the owner decides, invoice capture is neither in Sellable V1 nor excluded from it, and no work item for it exists. If the owner approves a direction, `sellable-v1.md` and `backlog.md` are updated through the normal scope-change process (see [AGENTS.md](../../AGENTS.md)).

## 1. The question

Business owners enter purchases by hand: supplier, products, quantities and unit costs, then receive the stock. Capturing this from the supplier's invoice could materially reduce manual entry. Two things must be decided by the owner:

1. **Is invoice capture a V1 sellability requirement, or a post-V1 differentiator?**
2. **Which extraction technologies, if any, are acceptable** given that V1 intelligence is deterministic and V1 excludes AI-assisted features ([sellable-v1.md](sellable-v1.md#intelligence-layers)).

The repository already records invoice capture and OCR as unresolved in `sellable-v1.md`; what is missing is a detailed, bounded workflow and the evidence needed to decide scope. General file attachments and receipts remain PRODUCT-P1 (Files, EPIC 13).

## 2. Three things that must not be conflated

| Capability | What it is | Current status |
|---|---|---|
| **Generic attachments / receipts** (Files, EPIC 13) | Storing and serving files attached to records | PRODUCT-P1, not started |
| **Invoice capture** (this document) | Turning one supplier invoice into a reviewed purchase draft | Undecided |
| **AI-assisted intelligence** (Layer 3) | LLM explanations, natural-language questions | FUTURE, excluded from V1 |

Invoice capture is a narrow workflow with its own file handling. It must not pull general-purpose attachments or document management into V1, and it is not an AI assistant. **Whether an extraction technique (for example OCR, structured e-invoice parsing, or an LLM) counts as the "AI" V1 excludes is not decided here.** The workflow below is technology-neutral on purpose, so the owner can make that call separately.

## 3. Proposed smallest workflow

All of this is a proposal for the owner to accept, change or reject.

1. **Provide an invoice.** An authorised user supplies one invoice file for one supplier. Proposed supported types: PDF, JPEG, PNG, within a size limit the owner sets. One file per capture; no email ingestion, no batches.
2. **Extract a draft.** The system proposes a *draft*, never a record. Proposed fields: supplier name, invoice number, invoice date, currency, and per line: description, quantity, unit cost, line total; plus invoice total. Each field is marked *found* with a confidence indication, or *not found*.
3. **Review and match the supplier.** The user sees the file beside the draft and resolves the extracted supplier name to an existing supplier record. The user can correct the match; no supplier is silently created. The user edits any field and maps each line to an existing product. Suggestions (by SKU, barcode or name) are offered but never applied without the user's action. A line the user cannot map is left for the user to resolve using the existing `CreateProduct`. Nothing is guessed.
4. **Match an existing purchase before creating one.** The user must choose whether the invoice belongs to an existing purchase order or represents a new purchase. If it matches an existing order, the capture is attached to that order's review/receiving flow and must not create a second order. If the goods have already been received, the workflow must not receive them again. Existing purchasing and receiving validation remains authoritative.
5. **Duplicate detection and conflicts.** Before confirmation, check exact file content (hash), and supplier plus invoice number **regardless of whether the extracted total matches**. A supplier/invoice-number match with a different total is a conflict to review, not a clean invoice. Also compare supplier, invoice date and total as a secondary heuristic when invoice numbers are missing. Show the suspected matching record and require an explicit, audited override to proceed; an override never bypasses purchase/receipt validation or idempotency.
6. **Confirm the purchase.** Only explicit user confirmation for a genuinely new purchase creates a purchase order, using the **existing** purchasing commands (`CreatePurchaseOrder`, `PlacePurchaseOrder`) with normal server-side validation, permissions, audit and tenant scoping. Matching an existing purchase does not create another one.
7. **Receive stock separately.** Stock changes **only** when the user confirms a receipt through the existing `ReceivePurchase` command. Capture may pre-fill a receipt for review, but receiving is a second explicit confirmation. The receipt uses the existing idempotent receipt identifier, and a prior receipt must be detected rather than replayed.

**The exact moment authoritative data changes** (proposed): for a genuinely new purchase, purchase-order creation and placing happen at step 6; inventory movements happen only at step 7. Matching, extraction and review do not create a duplicate purchase, and inventory changes only for a separately confirmed, valid, not-already-recorded receipt.

## 4. Invariants (proposed, derived from #69 and existing rules)

- **No mutation without confirmation.** Extraction output never writes to inventory, purchases or any financial record without user confirmation and the existing server-side validation.
- **No new back door.** Capture uses the existing commands. It adds no second implementation of stock, cost or purchasing rules.
- **Missing is not zero.** A unit cost of 0 currently means "no cost recorded" ([modules.md](../architecture/modules.md#purchasing), margin rules). An uncertain or missing cost must stay visibly missing and block confirmation; it must never default to 0.
- **Currency.** Purchase orders are in the organisation's currency and multi-currency purchasing is out of scope. An invoice in another currency is flagged, not converted or silently accepted.
- **Tenant isolation and permissions.** The file, the draft and the capture history are tenant-scoped like any other record. Which permission governs capture (reusing purchasing permissions or adding one) is open.
- **Audit.** Confirmation and any duplicate/conflict override are auditable events through `IAuditLog`.
- **Deterministic and explainable.** Totals shown are checked by application code against the user-confirmed lines; a model never supplies a figure that is stored.

## 5. Security requirements

Invoices are untrusted input. In addition to [File Uploads](../security/security.md#file-uploads): size, extension, content-type and magic-byte checks; generated storage keys; tenant-authorised downloads. The existing note "malware scanning is deferred; decide before EPIC 13 ships" must be decided before any capture feature ships. If any model-based extraction is chosen, invoice text is prompt-injection input ([ai-architecture.md](../architecture/ai-architecture.md#guardrails)): tool results are data, not instructions; unnecessary personal data is not sent to a provider.

## 6. Explicitly out of scope for this workflow

General-purpose document management or attachments on other records; email or supplier-portal ingestion; batch import; automatic approval or autonomous purchasing; automatic creation of suppliers or products without review; multi-currency conversion; an AI chat assistant; any marketplace or supplier-network feature.

## 7. Decision gate

#69 asks that the V1-versus-post-V1 choice be based on a test with real supplier invoices and target customers. Proposed measurements (the owner sets the thresholds; none are invented here):

- A sample of real supplier invoices from target customers, in the formats they actually receive (PDF, photo, scan).
- Field-level accuracy of each candidate technique, especially quantities and unit costs.
- Share of lines needing correction, and time to a confirmed purchase compared with manual entry.
- Fit with real products and SKUs, and the rate of suspected duplicates.
- Operating cost per invoice.

## 8. Approvals required before any build

Per `CLAUDE.md` §21 and #69, none may happen without explicit owner approval:

- Choosing and connecting a **paid extraction service or provider**, and its credentials and operating cost.
- Adding any **new dependency** (OCR or parsing library, file-scanning component).
- A decision on **malware scanning** and file storage.
- Deciding whether extraction counts under the V1 AI exclusion.
- Moving this into Sellable V1 (a significant product-scope change).

## 9. If the owner decides

- **Include in V1:** define the minimal workflow as new SELLABLE-P0 backlog items separate from EPIC 13, update `sellable-v1.md` (scope, exit criteria), and record any architectural choice in an ADR.
- **Post-V1:** record it as a PRODUCT-P1 differentiator that builds on EPIC 13's file security and the purchasing commands.
- **Not at all:** record that decision here and close #69.

## 10. Where each #69 acceptance criterion stands

| Acceptance criterion | Status |
|---|---|
| `sellable-v1.md`, `roadmap.md` and backlog tell one story | Addressed by the separate documentation-alignment PR ([#70](https://github.com/Ulaysu/soforr-business-os/pull/70)); this document adds the invoice-capture detail once the owner decides |
| Invoice capture distinguished from generic attachments and AI-assisted intelligence | Section 2 |
| First marketplace milestone recorded as post-V1, without dates or payment/logistics scope | Addressed by #70 (`roadmap.md`, `vision.md`); not an invoice-capture matter |
| New dependencies or paid providers called out for approval | Section 8 |
| No accepted ADR silently changed | None touched; an ADR is proposed only if the owner's choice requires an architectural decision |
| **Decide V1 requirement vs post-V1 differentiator** | **Pending owner decision (section 7)** |
