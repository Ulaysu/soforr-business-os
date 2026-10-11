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

To verify that the committed types are exactly what the snapshot generates (CI does the equivalent and fails on any difference), run from `src/Soforr.Web`:

```bash
npm run api:types:check
```

It generates into a temporary folder and compares, so it never changes your working tree. CI runs `npm run api:types` followed by `git diff --exit-code -- src/api/schema.d.ts`, then restores the file.

## Screens so far

Sign in, the product list (search, show inactive, paging, deactivate) and the product form (create and edit). Owner and Manager can change products; Staff can only look. That choice only decides which controls are shown; the server refuses everyone else with 403 whatever the screen does, and validates everything again. The stock screens list every active product with what is on hand (search, a low-stock filter, paging) and show one product's level, its movement history and the adjustment form (a change and a required note; the adjustment carries a client-chosen id, so trying again after a lost answer cannot adjust twice). Owner and Manager adjust and read the history; Staff only look at the levels. Registration is not a screen yet: the sign-in endpoints exist only in Development, so create your first local user through the API (the OpenAPI document describes `POST /api/v1/auth/register`, which needs an antiforgery token from `GET /api/v1/auth/antiforgery`).

## How the app is put together

`bootstrap.ts` builds the application: the API client, the session, the router and the root component. `main.ts` mounts it; the tests mount the very same application against a fake server (`src/test/fakeServer.ts`), so the screens are tested through the router and the session as a person meets them. The session (`src/stores/session.ts`) only remembers the answer of `GET /api/v1/auth/me`; nothing about it is stored in the browser. A 401 on any call (the session ended) sends the person to sign in again. Errors the API returns (a message and a code, per-field messages for validation) are read by `src/api/problem.ts` and shown as written.

## Layout

```text
src/api/       typed client (client.ts), generated types (schema.d.ts), problem parsing, injection
src/stores/    the session
src/router/    routes and the sign-in guard
src/views/     one component per screen, with its test
src/test/      the fake server and the helper that mounts the application
src/styles/    Tailwind entry
```
