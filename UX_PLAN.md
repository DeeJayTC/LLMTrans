# UX simplification plan

The product works, but a first-time user has to learn 15+ concepts to get a
single route translating. This is a separate track from the plugin work in
[PLUGINS_PLAN.md](PLUGINS_PLAN.md). It's all reversible — nothing here changes
the proxy data plane, only the way users discover and configure it.

---

## 1. What's actually complex right now

Concrete symptoms from the current code, not opinions:

### 1.1 Too many top-level concepts to wire one route

To create a single working route a user has to know:

- Tenant (everything is keyed by it; `t_dev` is hard-coded everywhere)
- Route + Route token + Direction mode (4 enum values)
- User language vs LLM language vs source vs target (4 ways to talk about 2)
- Translator id (`passthrough` / `deepl` / `llm` / `fake-brackets`)
- Glossary (separate page, separate FK on the route)
- Request style rule + Response style rule (two slots, separate page)
- Proxy rule (separate page) — which itself wires:
  - Allowlist (`request` / `response` JSONPath arrays)
  - Denylist (`toolArgKeys`)
  - Formality (5 enum values)
  - PII pack slugs + custom rule ids + disabled detector pairs
- PII rule (custom regex tester is on a different page from where you bind it)
- MCP server vs MCP catalog (two pages, one concept)
- Translation memory id + TM threshold

That's a one-route happy path that touches **5 admin pages and ~20 fields**.

[RoutesList.vue:127-216](src/AdaptiveApi.Ui/src/pages/RoutesList.vue#L127-L216)
is the form; [PiiRules.vue](src/AdaptiveApi.Ui/src/pages/PiiRules.vue) (459
lines) is the worst offender for surface area.

### 1.2 Twelve override headers

[OpenAiChatAdapter.cs:387-432](src/AdaptiveApi.Providers.OpenAI/OpenAiChatAdapter.cs#L387-L432)
recognises:

```
X-AdaptiveApi-Source-Lang     X-AdaptiveApi-Target-Lang
X-AdaptiveApi-Mode            X-AdaptiveApi-Translator
X-AdaptiveApi-Glossary        X-AdaptiveApi-Style-Rule         (legacy)
X-AdaptiveApi-Request-Style-Rule  X-AdaptiveApi-Response-Style-Rule
X-AdaptiveApi-Translation-Memory  X-AdaptiveApi-Tm-Threshold
X-AdaptiveApi-Stream-Strategy X-AdaptiveApi-Debug
```

Three of those (`Source-Lang`/`Target-Lang`, the two `*-Style-Rule`s + the
legacy alias, `Translation-Memory` + `Tm-Threshold`) are pairs that could
collapse into single headers.

### 1.3 The UI mirrors the data model, not the user's task

12 sidebar items, each its own CRUD list. There's no "set up my first route"
flow. A user has to discover that a route binds to a glossary, which they
have to create first, on a different page.

Empty-state copy is missing or generic
([RoutesList.vue:269](src/AdaptiveApi.Ui/src/pages/RoutesList.vue#L269) just
says "No routes yet."). The dashboard shows counts but no next action when
all counts are zero.

### 1.4 Settings page is essentially empty

[Settings.vue](src/AdaptiveApi.Ui/src/pages/Settings.vue) is 45 lines: lists
tenants and prints `health.status`. Things a user actually wants to set
(translator key, default language pair, auth mode) are env-vars-only.
Comment in the file admits it: "*Translator keys, DeepL server region, KMS
integration and SaaS billing move here in M8.*"

### 1.5 Dev artefacts leak everywhere

`t_dev` and `rt_dev_LOCALDEMO` show up in every form. There's no
"production readiness" indicator that flags "you're still on the dev token /
auth is OFF / no real translator key". [AuthSetup.cs:46-53](src/AdaptiveApi.Api/Auth/AuthSetup.cs#L46-L53)
silently grants every request when `Mode=none`, and the UI never says so.

### 1.6 Errors are raw

Every page does `e instanceof Error ? e.message : String(e)` and dumps it to
the user. EF / DbContext / nginx errors land verbatim in the form footer.

### 1.7 JSONPath in the proxy-rule editor

[ProxyRules.vue:360-374](src/AdaptiveApi.Ui/src/pages/ProxyRules.vue#L360-L374)
asks the user to type strings like `$.messages[*].content` to extend the
allowlist. That's expert-only, and there are no presets per route kind.

### 1.8 No global search / no command palette

Twelve list pages, no way to jump to "the route called …" or "the rule that
matches …" without clicking through.

---

## 2. Proposed simplifications

Ordered by impact / cost. None of these block the plugin work.

### Priority 0 — kill paper cuts (1–2 days each)

- [ ] **0.1 Single language-pair concept.** Replace
  `userLanguage` / `llmLanguage` (and the `Source-Lang` / `Target-Lang`
  header pair) in the UI with one `LanguagePair` widget showing
  `de ⇄ en-US`, with direction inferred from the `Direction` enum. Backend
  fields stay (compat); this is purely a UI presentation change in
  [RoutesList.vue](src/AdaptiveApi.Ui/src/pages/RoutesList.vue) and the
  playground. Add a single `X-AdaptiveApi-Lang: de/en-US` header that
  expands to the existing two on the server, and document it as the
  preferred form.
- [ ] **0.2 Drop the legacy `X-AdaptiveApi-Style-Rule` header.** Three ways
  to set the same thing → keep two (request / response). Remove the legacy
  alias path in [OpenAiChatAdapter.cs:403-410](src/AdaptiveApi.Providers.OpenAI/OpenAiChatAdapter.cs#L403-L410)
  and equivalents in the other adapters; bump a deprecation note in the
  SDK docs.
- [ ] **0.3 Friendly error formatter.** One small helper in
  [src/AdaptiveApi.Ui/src/api.ts](src/AdaptiveApi.Ui/src/api.ts) that maps
  ProblemDetails / EF / 401 / 5xx into a sentence the user can act on,
  used by every form. Stack traces only when an `?debug=1` query param is
  present.
- [ ] **0.4 Auth-mode banner.** When `AdaptiveApi:Auth:Mode=none`, show a
  permanent yellow banner in the admin shell: "Authentication is off —
  anyone reaching this URL has admin access. Set `AdaptiveApi:Auth:Mode`
  before opening this to a network." Pulled from
  [`/admin/system/auth-status`](src/AdaptiveApi.Api/Admin/) (new tiny
  endpoint).
- [ ] **0.5 Empty-state CTAs.** Routes / Glossaries / Style rules / PII
  rules each get an empty-state card with one button: "Create your first
  X". Wires straight to `startNew()`. Plus the dashboard shows
  "Get started" instead of empty zero-counts when nothing exists.

### Priority 1 — collapse data-model overlap (1 week each)

- [ ] **1.1 "Profile" abstraction over Glossary + Style + Proxy + PII.**
  Most users don't want four FKs on a route — they want "the customer-
  support config" or "the marketing config". Add a `RouteProfile` entity
  that bundles a glossary id, a request/response style pair, a proxy rule,
  and a PII pack selection. Routes get one optional `ProfileId`; the
  individual FKs stay for power users who want to override one slot. UI:
  Routes form shows `Profile: ▼` with an "Advanced — override individual
  bindings" expander.
- [ ] **1.2 First-route wizard.** New `/wizard` route — a 4-step flow:
  *Pick a provider (OpenAI / Anthropic / MCP / Generic) → pick a language
  pair → pick a translator (and prompt for the key inline if missing) →
  preview & create.* On success it shows the issued route token and a
  copy-paste snippet for the chosen provider's SDK. Replaces the empty
  Routes table for first-run users.
- [ ] **1.3 Merge MCP catalog + servers into one page with tabs.** Two
  routes today, one concept. [router.ts:9-12](src/AdaptiveApi.Ui/src/router.ts#L9-L12).
- [ ] **1.4 Move the PII tester onto the rule editor.** The right-hand
  panel of [PiiRules.vue](src/AdaptiveApi.Ui/src/pages/PiiRules.vue)
  already has a live preview; the standalone tester at the bottom is
  duplicate UX. Drop the bottom panel, beef up the inline preview.
- [ ] **1.5 JSONPath presets.** ProxyRules allowlist editor gets
  per-route-kind preset buttons: "OpenAI chat (default)", "Anthropic
  messages (default)", "Generic (none)". Custom paths still possible
  behind an "Advanced" expander.

### Priority 2 — turn the empty Settings page into a real one (1 week)

- [ ] **2.1 Translator credentials in the UI.** Move
  `Translators:DeepL:ApiKey`, `Translators:Llm:ApiKey`,
  `PiiRedactor:Presidio` config from env-vars-only into a
  `/admin/system/translators` endpoint backed by the existing settings
  store, surfaced on the Settings page. Keep env-var read as fallback for
  bootstrap, but allow runtime edit + test.
- [ ] **2.2 Auth mode editor.** The Settings page can flip `none ↔ oidc`,
  collect Authority / ClientId / ClientSecret / TenantClaim, and link to
  a "Test sign-in" round-trip before saving. Restart-required is fine
  with a clear notice.
- [ ] **2.3 Production-readiness checklist.** A small panel on Settings
  that lists:
  *Auth mode is `none`* /
  *Default translator is `passthrough`* /
  *Dev-fixed route token is set* /
  *Demo payload logging is on* —
  each with a green/yellow/red dot and a link to fix it. Drives the right
  user behaviour without being a wall of text.

### Priority 3 — global polish (deferred until after P0–P2)

- [ ] **3.1 Command palette (`Cmd+K`).** Jumps to any route, rule,
  glossary, or settings panel by name.
- [ ] **3.2 Search/filter on every list page.** One generic `<ListSearch>`
  component, dropped into Routes / Glossaries / StyleRules / PiiRules /
  Logs.
- [ ] **3.3 Tenant picker.** Once tenancy is real, replace the hard-coded
  `t_dev` strings with a context picker in the app shell. Until then,
  hide the tenant input from forms and pull it from a single source of
  truth.
- [ ] **3.4 Real-time route view.** Logs page shows recent activity but
  not "what's happening right now"; an SSE-backed live tail of audit
  events would help debugging real deployments without leaving the UI.

---

## 3. Backend simplifications worth queuing

These are bigger, mention them so they're on the radar:

- **Collapse `formality` / `style` / `glossary` defaults into the
  Profile.** A profile with sensible defaults removes 3 fields from the
  route form for the common case.
- **Drop `Stream-Strategy` from the public surface.** Pick the right
  strategy server-side based on the upstream. `progressive` is faster;
  default to it and remove the header. ([OpenAiChatAdapter.cs:464-469](src/AdaptiveApi.Providers.OpenAI/OpenAiChatAdapter.cs#L464-L469))
- **Auto-pick Translator based on configured keys.** If only DeepL is
  configured, default to it; if only `llm` is configured, default to that.
  Today the user has to know to set
  `Translators:Default=deepl`. Make it implicit, override possible.
- **Collapse `Translation-Memory` + `Tm-Threshold` into one
  `Translation-Memory: id;threshold=85` header.** Two headers for one
  binding is friction.

---

## 4. What I want before coding any of this

1. **Approve P0 in full** — these are paper-cut fixes, low risk, high
   immediate payoff. I can ship them in one PR.
2. **Decide on "Profile" (1.1).** It's the biggest single UX win but it's
   a new entity, a migration, and an API change. Sign-off needed before I
   draft a schema.
3. **First-run wizard (1.2)** — confirm you want it as a top-level
   `/wizard` route shown when no routes exist, vs. an inline empty-state
   on `/routes`.
4. **Settings page direction (2.x)** — confirm we're moving translator
   keys from env-vars-only into the DB-backed settings store. That's a
   security posture change (keys at rest in DB vs env) and worth a yes
   before I touch it.

Once P0 + sign-off on 1.1 / 1.2 / 2.x are done, I can break each priority
into a PR-sized task list.
