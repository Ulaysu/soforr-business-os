# AI Architecture

AI is an assistant layer, not the source of truth. It helps the owner understand and decide; the application executes.

## Flow

```text
User
 ↓
AI
 ↓
Controlled Application Tool
 ↓
Application Logic
 ↓
Database
 ↓
Verified Result
 ↓
AI
 ↓
User
```

## Rules

1. **No direct database access.** The AI reaches data only through named, read-only Application tools, each tenant-scoped and permission-checked as the calling user.
2. **No direct mutation.** The AI cannot write to the database or invoke state-changing commands on its own.
3. **Not authoritative** for financial facts, inventory quantities, sales totals, transaction state or accounting state. Those come from deterministic application code; the AI may only explain or summarize verified results.
4. **Permitted:** explain, summarize, identify patterns, recommend actions, prepare drafts.
5. **Approval turns intent into a command.** A recommendation is stored as data. When the owner approves, the *application* runs a deterministic command.

```text
AI:     "Rice is projected to run out in 4 days. Recommend ordering 100 units."
Owner:  Approve
App:    CreatePurchaseOrder(...)      ← executed by application code, audited
```

The projection and quantity come from deterministic Intelligence logic; the AI phrases and explains them.

## Tools

Initial read-only tools (Application services with typed input/output):

- GetSalesSummary
- GetLowStockProducts
- GetInventoryForecast
- GetExpenseSummary
- GetOverduePayments
- GetCustomerActivity

Tool contract: input validated; organization taken from the authenticated context, never from model-supplied arguments; output bounded in size; every invocation logged (who, tool, arguments, duration, result size).

## Provider Abstraction

Application defines the interfaces; Infrastructure implements them. OpenAI is the initial implementation and no other layer references it.

```text
IAIService       Application-facing orchestration (question in, grounded answer out)
IAIModelClient   Provider adapter (OpenAI initially; replaceable)
IAITool          A controlled tool the model may request
```

The Domain contains no AI types. Changing provider means changing Infrastructure registration and configuration only.

## Sequencing

MVP intelligence (Phase 3) is deterministic: velocity-based forecasting and reorder recommendations, no LLM required. The LLM assistant (EPIC 12, Phase 5) is layered on the same tools. Connecting an external AI provider is a paid service and needs human approval first.

## Guardrails

- User text, product names, receipts and retrieved data are **untrusted input** (prompt injection). Tool results are data, never instructions.
- Model output stating amounts, quantities or totals is validated against tool results or shown only next to the verified values.
- Do not send secrets or unnecessary personal data to a provider; minimize and redact.
- Rate-limit and cap cost per organization; record token usage.
- Keep an evaluation set for recommendation quality and tool-use correctness.
