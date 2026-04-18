# adaptiveapi Helm chart

```bash
# Put translator secrets into a Kubernetes secret first:
kubectl create secret generic adaptiveapi-translators \
  --from-literal=deeplApiKey=$DEEPL_API_KEY \
  --from-literal=llmApiKey=$OPENAI_API_KEY \
  --from-literal=stripeSecretKey=$STRIPE_SECRET_KEY

# Then install:
helm install adaptiveapi ./deploy/helm \
  --set edition=saas \
  --set translators.existingSecret=adaptiveapi-translators \
  --set ingress.host=adaptiveapi.yourdomain.com
```

The chart provisions:

- **API Deployment** with HPA (CPU-driven, 2→10 replicas) + PDB (`minAvailable: 1`),
  resource requests/limits, non-root security context, SQLite PVC by default,
  Postgres/SQL Server connection string from a referenced Secret.
- **UI Deployment** (nginx serving the built Vite bundle) with a Service.
- **Ingress** routing `/v1`, `/anthropic`, `/mcp`, `/mcp-translate`, `/generic`, `/admin`,
  `/saas`, `/scim`, `/healthz` → API service; everything else → UI. SSE buffering is
  disabled via the nginx-ingress annotation.
- **ConfigMap** for non-secret settings (edition toggle, provider, OTLP endpoint,
  Stripe subscription-item map).

Switch to production posture via `values.yaml`:

- `edition: saas` activates orgs + invites + SCIM + the Stripe usage worker.
- `database.provider: Postgres` + `database.existingSecret` — set the connection
  string in a pre-created Secret (chart does not manage DB credentials).
- `otel.enabled: true` + `otel.endpoint` — point at your OTLP collector.
- `redis.enabled: true` + `redis.connectionString` — enables the translation cache,
  rate limiting, and route/rule cache.
