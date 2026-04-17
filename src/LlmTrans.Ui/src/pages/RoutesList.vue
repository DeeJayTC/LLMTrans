<script setup lang="ts">
import { onMounted, ref } from 'vue';
import { useRoutesStore } from '../stores/routes';
import { api } from '../api';
import type { Glossary, ProxyRule, Route, StyleRule } from '../types';

const store = useRoutesStore();
const glossaries = ref<Glossary[]>([]);
const styleRules = ref<StyleRule[]>([]);
const proxyRules = ref<ProxyRule[]>([]);

onMounted(async () => {
  await store.refresh();
  try {
    [glossaries.value, styleRules.value, proxyRules.value] = await Promise.all([
      api.glossaries.list(),
      api.styleRules.list(),
      api.proxyRules.list(),
    ]);
  } catch { /* ignore load errors — form still works without dropdowns */ }
});

const showCreate = ref(false);
const editingId = ref<string | null>(null);
const form = ref<Partial<Route>>(emptyForm());

function emptyForm(): Partial<Route> {
  return {
    id: '',
    tenantId: 't_dev',
    kind: 'OpenAiChat',
    upstreamBaseUrl: 'https://api.openai.com/',
    userLanguage: 'de',
    llmLanguage: 'en-US',
    direction: 'Bidirectional',
    translatorId: null,
    glossaryId: null,
    requestStyleRuleId: null,
    responseStyleRuleId: null,
    proxyRuleId: null,
  };
}

function startNew() {
  editingId.value = null;
  form.value = emptyForm();
  showCreate.value = true;
}

function startEdit(r: Route) {
  editingId.value = r.id;
  form.value = { ...r };
  showCreate.value = true;
}

function cancel() {
  showCreate.value = false;
  editingId.value = null;
  form.value = emptyForm();
}

const issuedToken = ref<{ routeId: string; token: string } | null>(null);
const submitting = ref(false);
const formError = ref<string | null>(null);

async function submit() {
  submitting.value = true;
  formError.value = null;
  try {
    // Normalise empty strings to null so the backend clears bindings on update.
    const normalised = {
      ...form.value,
      translatorId: form.value.translatorId || null,
      glossaryId: form.value.glossaryId || null,
      requestStyleRuleId: form.value.requestStyleRuleId || null,
      responseStyleRuleId: form.value.responseStyleRuleId || null,
      proxyRuleId: form.value.proxyRuleId || null,
    };
    if (editingId.value) {
      await api.routes.update(editingId.value, normalised);
    } else {
      await api.routes.create(normalised as Route);
    }
    await store.refresh();
    cancel();
  } catch (e: unknown) {
    formError.value = e instanceof Error ? e.message : String(e);
  } finally {
    submitting.value = false;
  }
}

async function issueToken(id: string) {
  try {
    const res = await api.routes.issueToken(id);
    issuedToken.value = { routeId: id, token: res.plaintextToken };
  } catch (e: unknown) {
    formError.value = e instanceof Error ? e.message : String(e);
  }
}

async function remove(id: string) {
  if (!confirm(`Delete route ${id}?`)) return;
  await store.remove(id);
}
</script>

<template>
  <div class="flex flex-col gap-4">
    <div class="flex items-center justify-between">
      <div class="text-sm text-surface-700">
        {{ store.routes.length }} route{{ store.routes.length === 1 ? '' : 's' }}
      </div>
      <button class="btn-primary" @click="startNew">
        <span class="i-carbon-add" /> New route
      </button>
    </div>

    <div v-if="showCreate" class="card">
      <div class="card-header">
        {{ editingId ? `Edit route ${editingId}` : 'New route' }}
        <span v-if="editingId" class="text-xs text-surface-700">
          Bindings below control what llmtrans applies when this route's token is used.
        </span>
      </div>
      <form class="card-body grid grid-cols-2 gap-3" @submit.prevent="submit">
        <label class="flex flex-col gap-1 text-xs">
          ID
          <input class="input" v-model="form.id" placeholder="r_tenant_route" required :disabled="!!editingId" />
        </label>
        <label class="flex flex-col gap-1 text-xs">
          Tenant ID
          <input class="input" v-model="form.tenantId" required :disabled="!!editingId" />
        </label>
        <label class="flex flex-col gap-1 text-xs">
          Kind
          <select class="input" v-model="form.kind" :disabled="!!editingId">
            <option>OpenAiChat</option>
            <option>AnthropicMessages</option>
            <option>Generic</option>
          </select>
        </label>
        <label class="flex flex-col gap-1 text-xs">
          Upstream URL
          <input class="input" v-model="form.upstreamBaseUrl" required />
        </label>
        <label class="flex flex-col gap-1 text-xs">
          User language
          <input class="input" v-model="form.userLanguage" placeholder="de" />
        </label>
        <label class="flex flex-col gap-1 text-xs">
          LLM language
          <input class="input" v-model="form.llmLanguage" placeholder="en-US" />
        </label>
        <label class="flex flex-col gap-1 text-xs">
          Direction
          <select class="input" v-model="form.direction">
            <option>Bidirectional</option>
            <option>RequestOnly</option>
            <option>ResponseOnly</option>
            <option>Off</option>
          </select>
        </label>
        <label class="flex flex-col gap-1 text-xs">
          Translator
          <select class="input" v-model="form.translatorId">
            <option :value="null">— default —</option>
            <option value="deepl">deepl</option>
            <option value="llm">llm</option>
            <option value="passthrough">passthrough</option>
            <option value="fake-brackets">fake-brackets (testing)</option>
          </select>
        </label>

        <div class="col-span-2 border-t border-surface-200 pt-3 mt-1 text-xs text-surface-700 font-500">
          Bindings — select which glossary, style rule, and proxy rule apply when this route's token is used.
        </div>

        <label class="flex flex-col gap-1 text-xs">
          Glossary
          <select class="input" v-model="form.glossaryId">
            <option :value="null">— none —</option>
            <option v-for="g in glossaries" :key="g.id" :value="g.id">
              {{ g.id }} · {{ g.name }}
            </option>
          </select>
        </label>
        <label class="flex flex-col gap-1 text-xs">
          Request style (user → LLM)
          <select class="input" v-model="form.requestStyleRuleId">
            <option :value="null">— none —</option>
            <option v-for="s in styleRules" :key="s.id" :value="s.id">
              {{ s.id }} · {{ s.name }} ({{ s.language }})
            </option>
          </select>
          <span class="text-surface-500">Applied when translating the user's message into the LLM language. Usually a neutral style.</span>
        </label>
        <label class="flex flex-col gap-1 text-xs">
          Response style (LLM → user)
          <select class="input" v-model="form.responseStyleRuleId">
            <option :value="null">— none —</option>
            <option v-for="s in styleRules" :key="s.id" :value="s.id">
              {{ s.id }} · {{ s.name }} ({{ s.language }})
            </option>
          </select>
          <span class="text-surface-500">Applied when translating the LLM's reply back to the user. This is where brand voice lives.</span>
        </label>
        <label class="flex flex-col gap-1 text-xs col-span-2">
          Proxy rule
          <select class="input" v-model="form.proxyRuleId">
            <option :value="null">— none —</option>
            <option v-for="p in proxyRules" :key="p.id" :value="p.id">
              {{ p.id }} · {{ p.name }} · priority {{ p.priority }}
            </option>
          </select>
        </label>

        <div class="col-span-2 flex items-center gap-2 justify-end">
          <span v-if="formError" class="text-xs text-red-700 mr-auto">{{ formError }}</span>
          <button type="button" class="btn" @click="cancel">Cancel</button>
          <button type="submit" class="btn-primary" :disabled="submitting">
            {{ editingId ? 'Save' : 'Create' }}
          </button>
        </div>
      </form>
    </div>

    <div v-if="issuedToken" class="card border-brand-500 border">
      <div class="card-body flex flex-col gap-2">
        <div class="font-500">Route token for <span class="chip">{{ issuedToken.routeId }}</span></div>
        <pre class="select-all">{{ issuedToken.token }}</pre>
        <div class="text-xs text-surface-700">
          Copy this now — it will not be shown again. Paste into your SDK's <code>base_url</code>.
        </div>
        <div>
          <button class="btn" @click="issuedToken = null">Dismiss</button>
        </div>
      </div>
    </div>

    <div class="card">
      <table>
        <thead>
          <tr>
            <th>ID</th>
            <th>Kind</th>
            <th>User → LLM</th>
            <th>Direction</th>
            <th>Translator</th>
            <th />
          </tr>
        </thead>
        <tbody>
          <tr v-for="r in store.routes" :key="r.id">
            <td class="font-mono text-xs">{{ r.id }}</td>
            <td><span class="chip-primary">{{ r.kind }}</span></td>
            <td>{{ r.userLanguage }} → {{ r.llmLanguage }}</td>
            <td>
              <span class="chip">{{ r.direction }}</span>
            </td>
            <td>{{ r.translatorId ?? '—' }}</td>
            <td class="text-right whitespace-nowrap">
              <button class="btn" @click="startEdit(r)">Edit</button>
              <button class="btn ml-2" @click="issueToken(r.id)">Issue token</button>
              <button class="btn btn-danger ml-2" @click="remove(r.id)">Delete</button>
            </td>
          </tr>
          <tr v-if="store.routes.length === 0 && !store.loading">
            <td colspan="6" class="text-surface-700 text-center py-6">No routes yet.</td>
          </tr>
        </tbody>
      </table>
    </div>
  </div>
</template>
