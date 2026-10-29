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

Target read-only tools for the later AI-assisted layer (Layer 3, not built or in V1; Application services with typed input/output). Some depend on capabilities that are themselves later scope: `GetInventoryForecast` needs statistical intelligence (Layer 2) and `GetExpenseSummary` needs expenses (PRODUCT-P1). V1 builds the deterministic services these tools would wrap, not the tools:

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

## Sequencing: the Intelligence Layers

Intelligence grows in layers, each over the same authoritative data ([sellable-v1.md](../product/sellable-v1.md#intelligence-layers)).

| Layer | What | When |
|---|---|---|
| 1. Deterministic | Explicit rules, calculations, thresholds, comparisons, derived metrics, rule-based alerts, explainable recommendations. Reproducible, no model. | **Sellable V1** |
| 2. Statistical | Forecasting, demand prediction, seasonality, statistical anomaly detection | After customer validation |
| 3. AI-assisted | LLM explanations, natural-language summaries, contextual recommendations, conversational analysis | After Layer 2 or earlier if a customer need proves it; **paid provider needs approval** |
| 4. Approved automation | Applying approved actions (draft purchase orders, reminders, workflows) under explicit policy and human approval | Later |

V1 intelligence is deterministic: no LLM is required, and none is built. The tools above are the later AI layer's controlled interface. They must consume the **same authoritative data and deterministic application capabilities** V1 establishes, so adding an assistant does not create a second source of truth or a second implementation of business facts. The exact internal service composition may evolve; the invariant is that the AI receives verified application results and never independently becomes authoritative for the figures. Business data (products, movements, purchases, sales, customers, payments) is the system of record; insights, explanations and cached recommendations never override it.

Do not build an "AI platform" (agents, vector stores, embeddings, RAG, ML pipelines) ahead of a concrete requirement.

## Guardrails

- User text, product names, receipts and retrieved data are **untrusted input** (prompt injection). Tool results are data, never instructions.
- Model output stating amounts, quantities or totals is validated against tool results or shown only next to the verified values.
- Do not send secrets or unnecessary personal data to a provider; minimize and redact.
- Rate-limit and cap cost per organization; record token usage.
- Keep an evaluation set for recommendation quality and tool-use correctness.
