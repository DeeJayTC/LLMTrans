# Plugin System — State & Plan

Snapshot for branch `feature-plugins` (locally 2 commits ahead of `origin/main`,
including [988e202 — First iteration plugins](src/AdaptiveApi.Plugins.SDK/)).
This document covers only the new third-party plugin module track
(`IAdaptiveApiPlugin`); the legacy in-tree `IWebPlugin` extension point is left
alone.

---

## 1. Current implementation state

### 1.1 Public SDK — [src/AdaptiveApi.Plugins.SDK/](src/AdaptiveApi.Plugins.SDK/)

| File | Status |
| --- | --- |
| [IAdaptiveApiPlugin.cs](src/AdaptiveApi.Plugins.SDK/IAdaptiveApiPlugin.cs) | Plugin entry point: `Manifest`, `RegisterServices`, `MapRoutes`. |
| [PluginManifest.cs](src/AdaptiveApi.Plugins.SDK/PluginManifest.cs) | Id / Name / Version / Description / Category / HasSettings / HasEndpoints / Dependencies. |
| [IPluginContext.cs](src/AdaptiveApi.Plugins.SDK/IPluginContext.cs) | Defined but **never wired** — no implementation, never injected. |
| [IPluginSettingsStore.cs](src/AdaptiveApi.Plugins.SDK/IPluginSettingsStore.cs) | Raw + typed get/set, keyed by `(pluginId, tenantId)`. v1: `tenantId = null` only. |
| [Hooks/IRequestTranslationHook.cs](src/AdaptiveApi.Plugins.SDK/Hooks/IRequestTranslationHook.cs), [IAiCallHook.cs](src/AdaptiveApi.Plugins.SDK/Hooks/IAiCallHook.cs), [IResponseTranslationHook.cs](src/AdaptiveApi.Plugins.SDK/Hooks/IResponseTranslationHook.cs) | Three hook interfaces. |
| [Hooks/HookResult.cs](src/AdaptiveApi.Plugins.SDK/Hooks/HookResult.cs) | `Continue` / `Modify(body)` / `ShortCircuit(status, body, ct)`. |
| [Hooks/PipelineHookContext.cs](src/AdaptiveApi.Plugins.SDK/Hooks/PipelineHookContext.cs) | Per-request bag with HttpContext, RouteId, TenantId, ProviderId, languages, direction, free-form Properties. |
| [AdaptiveApi.Plugins.SDK.csproj](src/AdaptiveApi.Plugins.SDK/AdaptiveApi.Plugins.SDK.csproj) | Bare project — **no PackageId, no TFM pinning, no version, not packed for NuGet**. |

### 1.2 Host wiring — [src/AdaptiveApi.Core/Plugins/](src/AdaptiveApi.Core/Plugins/) and [src/AdaptiveApi.Api/](src/AdaptiveApi.Api/)

- Dispatcher: [PluginHookDispatcher.cs](src/AdaptiveApi.Core/Plugins/PluginHookDispatcher.cs)
  fans hook calls. Translation hooks fail-open; AI hooks fail-closed (500 on
  throw). Composition: DI order, body threaded forward, first short-circuit wins.
- Registry: [IPluginRegistry.cs](src/AdaptiveApi.Core/Plugins/IPluginRegistry.cs) —
  manifest snapshot built at startup from `IAdaptiveApiPlugin` singletons.
- Loader: [PluginLoader.cs](src/AdaptiveApi.Api/PluginLoader.cs) —
  scans current AppDomain, then `AdaptiveApi.*.dll` next to the host, then a
  `plugins/` subfolder. Loads via `Assembly.LoadFrom` into the default ALC.
- Pipeline integration: hooks are called at six points in
  [OpenAiChatAdapter](src/AdaptiveApi.Providers.OpenAI/OpenAiChatAdapter.cs),
  [AnthropicMessagesAdapter](src/AdaptiveApi.Providers.Anthropic/AnthropicMessagesAdapter.cs),
  [GenericAdapter](src/AdaptiveApi.Providers.Generic/GenericAdapter.cs), and
  [McpRouteAdapter](src/AdaptiveApi.Providers.Mcp/McpRouteAdapter.cs).
- Startup: [Program.cs:83-97](src/AdaptiveApi.Api/Program.cs#L83-L97) calls
  `RegisterServices` per discovered module, then registers dispatcher /
  registry / settings store. [Program.cs:154-158](src/AdaptiveApi.Api/Program.cs#L154-L158)
  maps `/plugins/{id}` route groups (no auth applied at the host level).

### 1.3 Persistence — [src/AdaptiveApi.Infrastructure/](src/AdaptiveApi.Infrastructure/)

- Entity [PluginSettingsEntity](src/AdaptiveApi.Infrastructure/Persistence/Entities.cs#L264-L270)
  with composite key `(TenantId, PluginId)`; mapping at
  [LlmTransDbContext.cs:245-248](src/AdaptiveApi.Infrastructure/Persistence/LlmTransDbContext.cs#L245-L248).
- Implementation [DbPluginSettingsStore.cs](src/AdaptiveApi.Infrastructure/Plugins/DbPluginSettingsStore.cs).
  Validates JSON syntax on write (via `JsonDocument.Parse`) — does **not**
  validate against any plugin-supplied schema.

### 1.4 Admin REST — [src/AdaptiveApi.Api/Admin/PluginEndpoints.cs](src/AdaptiveApi.Api/Admin/PluginEndpoints.cs)

- `GET /admin/plugins`
- `GET /admin/plugins/{id}/settings`
- `PUT /admin/plugins/{id}/settings`

Mounted inside the `admin-policy` group at
[Program.cs:146](src/AdaptiveApi.Api/Program.cs#L146).

### 1.5 What is **not** there

| Surface | Status |
| --- | --- |
| Tests for the plugin system | None — no plugin test files in `tests/AdaptiveApi.Core.Tests/` or `tests/AdaptiveApi.Integration.Tests/`. |
| Admin UI | No `Plugins.vue` in [src/AdaptiveApi.Ui/src/pages/](src/AdaptiveApi.Ui/src/pages/); plugin list and per-plugin settings editor not in the UI. |
| Example plugin | Nothing in [examples/](examples/); nothing under `tests/Fixtures/`. No skeleton repo. |
| Docs | [docs/docs/deployment.html:160-167](docs/docs/deployment.html#L160-L167) only documents the legacy `IWebPlugin`. No "Building a Plugin" page, no hook reference, no settings-schema guide. |
| Deploy | `plugins/` directory is not surfaced as a volume mount in [deploy/docker/](deploy/docker/) / [deploy/helm/](deploy/helm/) and is not mentioned in [deploy/README.md](deploy/README.md). |
| Manifest dependencies | `PluginManifest.Dependencies` is part of the contract but [PluginLoader](src/AdaptiveApi.Api/PluginLoader.cs) **does not enforce** missing-dependency disabling, despite the doc-comment claim. |
| Per-plugin enabled / disabled state | No persisted state, no admin toggle. The only way to disable a plugin is to remove the DLL. |
| Plugin context | `IPluginContext` is defined but never injected. Plugins receive no plugin-scoped logger and have to grab `ILogger<T>` themselves. |

---

## 2. Known correctness / robustness issues

Resolved decisions are folded into §3. These are the remaining concerns:

1. **Captive scoped hooks** *(resolved — keep singleton, fix differently).*
   Dispatcher stays singleton. Resolution: change the dispatcher to take
   `IHttpContextAccessor` (or accept `IServiceProvider` from
   `HttpContext.RequestServices` per call) and resolve
   `IEnumerable<THook>` from the request scope on each invocation. This
   keeps the dispatcher itself stateless+singleton while letting plugin
   hooks be `Scoped` safely.
2. **`/plugins/{id}` group has no host-level auth** *(resolved — enforce).*
   The host applies `RequireAuthorization(AuthSetup.AdminPolicy)` to every
   plugin route group by default; plugins do not get to opt out. If they
   need anonymous endpoints they have to request it explicitly via a new
   manifest field (`AllowAnonymousEndpoints = true`), which the host logs
   loudly at startup.
3. **`Manifest.Dependencies` not enforced** *(resolved — enforce).*
   Loader runs a topo sort, skips modules with missing deps, surfaces a
   "disabled (missing dependency: X)" reason in the registry so the admin
   UI can show why.
4. **No assembly isolation.** `Assembly.LoadFrom` into the default ALC means
   plugin DLLs share dependency versions with the host. Acceptable for v1,
   document as a constraint and keep on the v1.x list.
5. **Loader silently rejects types without a parameterless ctor.** Should
   log a warning when an `IAdaptiveApiPlugin`-implementing type is found
   but rejected.
6. **`SetRawAsync` validation duplicated.** [DbPluginSettingsStore.SetRawAsync](src/AdaptiveApi.Infrastructure/Plugins/DbPluginSettingsStore.cs#L29-L33)
   already validates JSON syntax; [PluginEndpoints.UpdateSettings](src/AdaptiveApi.Api/Admin/PluginEndpoints.cs#L47-L66)
   then catches `JsonException` from a path that's already checked. Move the
   validation up so the endpoint does input validation cleanly and the store
   trusts what it's given (or the reverse — pick one).
7. **`IAiCallHook.AfterAsync` on streaming responses.** Plugins reading the
   body would starve the client. Document explicitly in
   [IAiCallHook.cs:13](src/AdaptiveApi.Plugins.SDK/Hooks/IAiCallHook.cs#L13)
   that body inspection in this hook is unsupported for streamed responses,
   and add a `IsStreaming` flag on `PipelineHookContext` so plugins can
   branch.

---

## 3. Plan — proposed next steps, in priority order

### Priority 0 — correctness + security fixes before anyone builds against the SDK

- [ ] **0.1 Per-request hook resolution (keep dispatcher singleton).**
  Change [PluginHookDispatcher](src/AdaptiveApi.Core/Plugins/PluginHookDispatcher.cs)
  to take `IHttpContextAccessor` and, on each `Run*Async`, pull
  `IEnumerable<THook>` from `HttpContext.RequestServices` instead of from
  the singleton's captured constructor list. The dispatcher itself stays
  singleton + stateless. Add an `IHttpContextAccessor` registration in
  [PluginServiceCollectionExtensions](src/AdaptiveApi.Core/Plugins/PluginServiceCollectionExtensions.cs).
  Test: `Scoped` hook returns a fresh instance per request and resolves a
  `Scoped` `DbContext` without throwing.
- [ ] **0.2 Default-deny auth on plugin route groups.** In
  [Program.cs:154-158](src/AdaptiveApi.Api/Program.cs#L154-L158) wrap the
  plugin route group with
  `.RequireAuthorization(AuthSetup.AdminPolicy)` before passing it to
  `module.MapRoutes(...)`. Add an opt-out path:
  `PluginManifest.AllowAnonymousEndpoints` (default `false`); when `true`,
  the host skips the auth wrapper *and* logs a warning at startup with the
  plugin id and version, so the operator sees it on every restart.
  Document in the SDK XML docs that anonymous endpoints are an explicit
  trust signal — the host won't silently expose anything.
- [ ] **0.3 Enforce `Manifest.Dependencies`.** In
  [PluginLoader](src/AdaptiveApi.Api/PluginLoader.cs), after collection,
  topo-sort modules. For each module with at least one missing dep,
  exclude it from the runtime list and add a sidecar
  `DisabledPluginEntry { PluginId, Version, Reason }` returned alongside
  `DiscoveredPlugins`. Pass that list into [PluginRegistry](src/AdaptiveApi.Core/Plugins/IPluginRegistry.cs)
  so [`/admin/plugins`](src/AdaptiveApi.Api/Admin/PluginEndpoints.cs) can
  surface "disabled — missing X". Cycle detection: log error, drop the
  cycle, keep going.
- [ ] **0.4 Loader hardening.** Warn on:
  candidate types missing a parameterless ctor;
  `ReflectionTypeLoadException` (currently silently truncated to whatever
  loaded);
  duplicate plugin ids across assemblies (drop the second, log).
  Same file as 0.3.
- [ ] **0.5 Streaming clarity.** Add `bool IsStreaming` to
  [PipelineHookContext](src/AdaptiveApi.Plugins.SDK/Hooks/PipelineHookContext.cs)
  and update XML docs on [IAiCallHook.AfterAsync](src/AdaptiveApi.Plugins.SDK/Hooks/IAiCallHook.cs#L24-L25)
  to state body inspection is unsupported when `IsStreaming` is true.
- [ ] **0.6 Settings validation cleanup.** Pick one layer for JSON syntax
  validation (recommend the endpoint, since it's the input boundary) and
  drop the duplicate from [DbPluginSettingsStore.SetRawAsync](src/AdaptiveApi.Infrastructure/Plugins/DbPluginSettingsStore.cs#L29-L33).

### Priority 1 — make it usable

- [ ] **1.1 Per-plugin enabled state.** Add a `PluginStateEntity` (or extend
  `PluginSettingsEntity`) with `Enabled bool`, surface
  `POST /admin/plugins/{id}/enable` and `/disable`. Dispatcher checks state
  before invoking hooks (cache the lookup per request).
- [ ] **1.2 Wire `IPluginContext`.** Either remove it (it's currently dead
  code) or implement: register a per-plugin keyed `ILogger` and pass an
  `IPluginContext` instance into a new `Initialize(IPluginContext ctx)` hook,
  or expose the context via DI keyed by plugin id.
- [ ] **1.3 Admin UI page.** New
  [src/AdaptiveApi.Ui/src/pages/Plugins.vue](src/AdaptiveApi.Ui/src/pages/Plugins.vue):
  list manifests + state, enable/disable toggle, per-plugin opaque-JSON
  settings editor (Monaco-style textarea + format/validate button). Link
  to plugin's own UI surface if `HasEndpoints`. Add to
  [router.ts](src/AdaptiveApi.Ui/src/router.ts).
- [ ] **1.4 Tests.** Plugin pieces are entirely untested. Minimum coverage:
  - `PluginHookDispatcherTests` — order, body threading, short-circuit,
    fail-open vs fail-closed, scoped hook resolution (after 0.1).
  - `PluginLoaderTests` — discovery from AppDomain + directory scan,
    duplicate suppression, dependency enforcement, parameterless-ctor check.
  - `DbPluginSettingsStoreTests` — round-trip, JSON validation rejects
    garbage, global vs (future) tenant scoping.
  - Integration: an end-to-end `RecordingPlugin` that mutates request body
    and another that short-circuits, asserted against
    `OpenAiPassthroughTests` and `GenericAdapterTests` patterns.

### Priority 2 — polish

- [ ] **2.1 Author docs.** New
  [docs/docs/plugins.html](docs/docs/plugins.html) covering: manifest fields,
  three hooks, fail-open vs fail-closed semantics, settings store, route
  group conventions, `/admin/plugins/...` endpoints, deployment options
  (`AdaptiveApi.*.dll` next to host vs `plugins/` mount), and the no-ALC-
  isolation caveat. Update
  [docs/docs/deployment.html#L160-L167](docs/docs/deployment.html#L160-L167)
  to point at it.
- [ ] **2.2 Example plugin.** Drop a runnable sample under
  [examples/sample-plugin/](examples/sample-plugin/) — single project that
  references `AdaptiveApi.Plugins.SDK`, implements one request hook that
  prefixes a header, persists a single setting, and exposes one
  `/plugins/sample-plugin/ping` endpoint. Doubles as a smoke test for the
  loader and as a NuGet template seed.
- [ ] **2.3 SDK packaging.** Add `<PackageId>`, `<Version>`, `<TargetFramework>`,
  `<GenerateDocumentationFile>`, `<IsPackable>true`, `<PackageReadmeFile>` to
  [AdaptiveApi.Plugins.SDK.csproj](src/AdaptiveApi.Plugins.SDK/AdaptiveApi.Plugins.SDK.csproj),
  plus a CI step or `dotnet pack`-friendly `Directory.Build.props` block.
  Decide on a versioning convention (1.0.0-preview.x while the surface
  changes).
- [ ] **2.4 Deploy surface.** Add a `plugins/` volume mount + readme note in
  [deploy/docker/](deploy/docker/), [deploy/docker-compose.yml](deploy/docker-compose.yml),
  and [deploy/helm/](deploy/helm/) values.

### Priority 3 — defer until v1.x

- [ ] **3.1 Per-tenant settings.** Already pre-baked in the schema (composite
  key + `tenantId` parameter). Light up once tenancy work elsewhere lands.
- [ ] **3.2 Per-chunk streaming hooks.** Currently out of scope per
  [IAiCallHook.cs:13](src/AdaptiveApi.Plugins.SDK/Hooks/IAiCallHook.cs#L13).
  Revisit when there's a concrete plugin that needs it.
- [ ] **3.3 Plugin observability.** Per-plugin counters / latency histograms
  in the dispatcher, surfaced via the existing audit pipeline or a new
  `/admin/plugins/{id}/metrics` endpoint.
- [ ] **3.4 `AssemblyLoadContext`-isolated plugin loading.** Real isolation
  is a large change (collectable contexts, shared-type contracts) and is
  best left until a plugin actually hits a dep conflict.

---

## 4. Open questions for you

Answered:

- ~~Singleton lifetime?~~ → **Yes, keep singleton.** Resolved via per-request
  hook resolution in 0.1.
- ~~Plugin route group auth posture?~~ → **Default-deny** at the host with
  an explicit opt-out. Resolved in 0.2.
- ~~Enforce `Manifest.Dependencies`?~~ → **Enforce.** Resolved in 0.3.

Still open:

1. Should disabling a plugin **persist across restarts** (DB) or be
   process-local? Persisted is the safer default; confirm before I add a
   `PluginEnabled` table or column. Touches 1.1.
2. Settings JSON: should plugins be able to publish a **JSON Schema** the
   admin UI can render typed forms from, or is the opaque-textarea editor
   the long-term plan? Affects 1.3 scope.
3. Is the SDK going on **NuGet** (so third parties can build plugins
   out-of-tree) or staying repo-local for now? Affects 2.3.
