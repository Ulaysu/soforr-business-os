# Soforr.Web

The Vue 3 application of Soforr Business OS: TypeScript (strict), Vite, Tailwind CSS, Vue Router, Vitest. At this stage it is a **shell**: a home page, a not-found page, a typed API client and the tooling. Business screens arrive with their own work items. **No business rule belongs in this project** (CLAUDE.md section 4): the server decides prices, stock, credit, permissions and tenants; the client shows and sends.

## Commands

Node 22 or newer.

```bash
npm ci               # install exactly what package-lock.json says
npm run dev          # dev server on http://localhost:5173, proxying /api and /health to the API
npm run build        # type-check and build to dist/
npm test             # Vitest
npm run lint         # ESLint
npm run format:check # Prettier (npm run format fixes)
npm run typecheck    # vue-tsc
```

## Talking to the API

The browser only talks to the dev server's own origin, which proxies `/api` and `/health` to the API (default `https://localhost:7168`; set `SOFORR_API_URL` in the environment or in a git-ignored `.env.local`). This keeps the session cookie and the antiforgery cookie same-site, as in production ([ADR 0002](../../docs/decisions/0002-authentication.md)). The default target is HTTPS because the session and antiforgery cookies are `Secure`: use the API's HTTPS address (the `https` launch profile) for anything that signs in; a plain-HTTP target works only for unauthenticated calls such as `/health/live`. Start the API with `dotnet run --project src/Soforr.Api` (Development) and a database (see the repository README), then `npm run dev`.

## The typed API client

`src/api/client.ts` wraps `openapi-fetch` around types generated from the API's own OpenAPI document, so a request or response that no longer matches the API fails to compile. It sends the session cookie (the only credential; nothing is stored in the browser) and adds the antiforgery header to every unsafe request, fetching the token when needed and again after signing in or out.

The types come from a committed snapshot of the document, `openapi/openapi.json`. A .NET integration test (`OpenApiSnapshotTests`) fails when the API changes without the snapshot being regenerated. To regenerate after an API change:

```bash
SOFORR_UPDATE_OPENAPI=1 dotnet test tests/Soforr.Api.IntegrationTests --filter OpenApiSnapshot   # rewrites openapi/openapi.json
cd src/Soforr.Web && npm run api:types                                                            # regenerates src/api/schema.d.ts
```

Commit both files. `src/api/schema.d.ts` is generated and not edited by hand.

## Layout

```text
src/api/       typed client (client.ts), generated types (schema.d.ts)
src/router/    routes
src/views/     one component per route
src/styles/    Tailwind entry
```
