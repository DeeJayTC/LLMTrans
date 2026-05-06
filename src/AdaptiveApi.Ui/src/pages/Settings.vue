<script setup lang="ts">
import { onMounted, ref } from 'vue';
import { api, friendlyError, type OidcValidation, type ReadinessCheck, type SecretList } from '../api';
import type { Tenant } from '../types';

const tenants = ref<Tenant[]>([]);
const health = ref<string>('unknown');
const checks = ref<ReadinessCheck[]>([]);
const error = ref<string | null>(null);

// ---- secret store (translator API keys at rest) ----
const secrets = ref<SecretList>({ storeAvailable: false, secrets: [] });
const secretError = ref<string | null>(null);
const draft = ref<Record<string, string>>({});
const SECRET_KEYS = [
  { key: 'translator.deepl.apikey', label: 'DeepL API key' },
  { key: 'translator.llm.apikey', label: 'LLM translator API key' },
];

async function refreshSecrets() {
  try { secrets.value = await api.secrets.list(); }
  catch (e: unknown) { secretError.value = friendlyError(e); }
}

function isSet(key: string) {
  return secrets.value.secrets.some(s => s.key === key);
}

function lastUpdated(key: string) {
  return secrets.value.secrets.find(s => s.key === key)?.updatedAt ?? null;
}

async function saveSecret(key: string) {
  const value = (draft.value[key] ?? '').trim();
  if (!value) return;
  secretError.value = null;
  try {
    await api.secrets.set(key, value);
    draft.value[key] = '';
    await refreshSecrets();
  } catch (e: unknown) {
    secretError.value = friendlyError(e);
  }
}

async function deleteSecret(key: string) {
  if (!confirm(`Delete ${key}? Translators will fall back to environment-variable config.`)) return;
  await api.secrets.delete(key);
  await refreshSecrets();
}

const oidcAuthority = ref('');
const oidcResult = ref<OidcValidation | null>(null);
const oidcChecking = ref(false);

async function validateOidc() {
  if (!oidcAuthority.value) return;
  oidcChecking.value = true;
  oidcResult.value = null;
  try {
    oidcResult.value = await api.system.validateOidc({ authority: oidcAuthority.value });
  } catch (e: unknown) {
    oidcResult.value = {
      ok: false, issuer: null, authorizationEndpoint: null, tokenEndpoint: null,
      jwksUri: null, scopesSupported: [], error: friendlyError(e),
    };
  } finally {
    oidcChecking.value = false;
  }
}

async function refresh() {
  error.value = null;
  try {
    const [t, h, r] = await Promise.all([
      api.tenants.list(),
      api.health(),
      api.system.readiness(),
    ]);
    tenants.value = t;
    health.value = h.status;
    checks.value = r.checks;
  } catch (e: unknown) {
    error.value = friendlyError(e);
  }
  await refreshSecrets();
}

function dotColor(status: ReadinessCheck['status']) {
  return status === 'ok' ? 'bg-green-500'
    : status === 'warn' ? 'bg-yellow-500'
    : 'bg-red-500';
}

onMounted(refresh);
</script>

<template>
  <div class="flex flex-col gap-4">
    <div v-if="error" class="card border-red-300 border">
      <div class="card-body text-sm text-red-700">{{ error }}</div>
    </div>

    <!-- ===== production-readiness ===== -->
    <div class="card">
      <div class="card-header">
        <span>Production readiness</span>
        <span class="text-xs text-surface-700">
          Read-only checklist. Anything red blocks a production deployment;
          yellow is fine for dev but should be cleaned up before exposing
          this instance.
        </span>
        <button class="btn ml-auto" @click="refresh">
          <span class="i-carbon-renew" /> Refresh
        </button>
      </div>
      <div class="card-body flex flex-col gap-2">
        <div v-for="c in checks" :key="c.id"
             class="flex items-start gap-3 px-2 py-2 border-b last:border-b-0 border-surface-200">
          <span class="inline-block h-2 w-2 rounded-full mt-2 shrink-0" :class="dotColor(c.status)" />
          <div class="flex-1">
            <div class="text-sm font-500">{{ c.name }}</div>
            <div class="text-xs text-surface-700">{{ c.detail }}</div>
          </div>
          <span class="chip text-xs"
                :class="c.severity === 'blocker' ? 'bg-red-50 text-red-700' :
                        c.severity === 'warning' ? 'bg-yellow-50 text-yellow-900' : ''">
            {{ c.severity }}
          </span>
        </div>
        <div v-if="checks.length === 0" class="text-sm text-surface-700 px-2 py-3">
          No checks reported.
        </div>
      </div>
    </div>

    <!-- ===== Translator credentials ===== -->
    <div class="card">
      <div class="card-header">
        <span>Translator credentials</span>
        <span class="text-xs text-surface-700">
          API keys live at rest in the encrypted secret store. The host
          falls back to <code>.env</code> values when the store has no
          entry. Keys are written through this form and never displayed
          back — only the metadata (set / not set, last update) is.
        </span>
      </div>
      <div class="card-body flex flex-col gap-3">
        <div v-if="!secrets.storeAvailable" class="text-xs text-yellow-900 bg-yellow-50 border border-yellow-200 rounded p-3">
          Secret store not configured. Set
          <code>AdaptiveApi:Secrets:Kek</code> to a base64-encoded 32-byte
          key (e.g. <code>openssl rand -base64 32</code>) to enable.
          Until then, translator keys must come from environment variables.
        </div>

        <div v-for="k in SECRET_KEYS" :key="k.key"
             class="border border-surface-200 rounded p-3 flex flex-col gap-2">
          <div class="flex items-center gap-2">
            <span class="font-500 text-sm">{{ k.label }}</span>
            <span v-if="isSet(k.key)" class="chip text-xs bg-green-50 text-green-700">
              set · updated {{ new Date(lastUpdated(k.key)!).toLocaleString() }}
            </span>
            <span v-else class="chip text-xs">env-only</span>
            <button v-if="isSet(k.key)" class="btn btn-danger ml-auto text-xs"
                    @click="deleteSecret(k.key)">Remove</button>
          </div>
          <div class="flex gap-2">
            <input class="input flex-1 font-mono text-xs" type="password"
                   v-model="draft[k.key]" placeholder="paste new value" />
            <button class="btn-primary" :disabled="!secrets.storeAvailable || !(draft[k.key] ?? '').trim()"
                    @click="saveSecret(k.key)">
              {{ isSet(k.key) ? 'Replace' : 'Save' }}
            </button>
          </div>
          <span class="text-xs font-mono text-surface-500">{{ k.key }}</span>
        </div>

        <div v-if="secretError" class="text-xs text-red-700">{{ secretError }}</div>
      </div>
    </div>

    <!-- ===== OIDC validator ===== -->
    <div class="card">
      <div class="card-header">
        <span>OIDC discovery probe</span>
        <span class="text-xs text-surface-700">
          Tests an authority's <code>/.well-known/openid-configuration</code>
          before you switch <code>AdaptiveApi:Auth:Mode</code> to
          <code>oidc</code>. Doesn't store anything; this is a sanity check
          you can re-run anytime.
        </span>
      </div>
      <div class="card-body flex flex-col gap-2">
        <div class="flex gap-2">
          <input class="input flex-1 font-mono text-xs" v-model="oidcAuthority"
                 placeholder="https://accounts.example.com" />
          <button class="btn-primary" :disabled="oidcChecking || !oidcAuthority" @click="validateOidc">
            {{ oidcChecking ? 'Checking…' : 'Validate' }}
          </button>
        </div>
        <div v-if="oidcResult" class="border border-surface-200 rounded p-3 flex flex-col gap-1 text-xs">
          <div class="font-500"
               :class="oidcResult.ok ? 'text-green-700' : 'text-red-700'">
            {{ oidcResult.ok ? '✓ Discovery document looks usable' : '✗ Validation failed' }}
          </div>
          <div v-if="oidcResult.error" class="text-red-700">{{ oidcResult.error }}</div>
          <div v-if="oidcResult.issuer">issuer: <code>{{ oidcResult.issuer }}</code></div>
          <div v-if="oidcResult.tokenEndpoint">token: <code>{{ oidcResult.tokenEndpoint }}</code></div>
          <div v-if="oidcResult.jwksUri">jwks: <code>{{ oidcResult.jwksUri }}</code></div>
          <div v-if="oidcResult.scopesSupported.length">
            scopes: <code>{{ oidcResult.scopesSupported.join(', ') }}</code>
          </div>
        </div>
      </div>
    </div>

    <div class="grid grid-cols-2 gap-4">
      <div class="card">
        <div class="card-header">Tenants</div>
        <div class="card-body">
          <table v-if="tenants.length > 0">
            <thead><tr><th>ID</th><th>Name</th><th>Created</th></tr></thead>
            <tbody>
              <tr v-for="t in tenants" :key="t.id">
                <td class="font-mono text-xs">{{ t.id }}</td>
                <td>{{ t.name }}</td>
                <td class="text-xs">{{ new Date(t.createdAt).toLocaleString() }}</td>
              </tr>
            </tbody>
          </table>
          <div v-else class="text-sm text-surface-700">No tenants yet.</div>
        </div>
      </div>

      <div class="card">
        <div class="card-header">System</div>
        <div class="card-body flex flex-col gap-2 text-sm">
          <div>API health: <span class="chip">{{ health }}</span></div>
          <div class="text-xs text-surface-700">
            Translator keys, DeepL server region, and KMS integration are
            still configured via environment variables — see
            <code>deploy/.env.example</code>. Editing them in the UI is on
            the roadmap.
          </div>
        </div>
      </div>
    </div>
  </div>
</template>
