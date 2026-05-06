# Sample plugin

Reference implementation of the AdaptiveAPI plugin contract — a single C#
project that demonstrates every extension point the SDK exposes.

## What it does

- Declares a `PluginManifest` (id `sample-plugin`, version `0.1.0`).
- Registers a request-translation hook that logs each incoming request and
  stamps an `X-AdaptiveApi-Sample-Plugin: saw-request` response header.
- Maps a tiny admin endpoint at `GET /plugins/sample-plugin/ping`.

## Build

```bash
dotnet build examples/sample-plugin/AdaptiveApi.Plugins.SamplePlugin.csproj
```

The output DLL lands in
`examples/sample-plugin/bin/Debug/net10.0/AdaptiveApi.Plugins.SamplePlugin.dll`.

## Install

Two options:

1. **Drop next to the host binary.** Copy the DLL into the API's base
   directory (`AdaptiveApi.*.dll` files there are scanned automatically).
2. **Mount under `plugins/`.** Copy the DLL into the API's
   `plugins/` subfolder. Any `*.dll` there is scanned. The
   docker-compose stack mounts `./plugins/` on the host into
   `/app/plugins/` inside the API container.

Restart the host. The plugin appears at `/admin/plugins` (UI: Plugins page)
and starts intercepting requests.

## Verify

```bash
curl -i http://localhost:8080/plugins/sample-plugin/ping
# -> 200, body: {"plugin":"sample-plugin","ok":true}
# (returns 401 if Auth:Mode=oidc and you don't pass a token)

# Check the response header on a real proxy round-trip:
curl -i http://localhost:8080/v1/<route-token>/chat/completions \
  -H "Authorization: Bearer $OPENAI_API_KEY" \
  -H "Content-Type: application/json" \
  -d '{"model":"gpt-4o-mini","messages":[{"role":"user","content":"hi"}]}'
# response now includes: x-adaptiveapi-sample-plugin: saw-request
```

## Disable without redeploying

The Plugins admin page has an enable / disable toggle. The host persists
the choice in the `plugin_settings` table, so it survives restart.
