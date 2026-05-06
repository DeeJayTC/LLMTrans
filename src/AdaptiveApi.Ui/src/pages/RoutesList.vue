<script setup lang="ts">
import { computed, onMounted, ref } from 'vue';
import { RouterLink } from 'vue-router';
import { useRoutesStore } from '../stores/routes';
import { api, friendlyError } from '../api';
import type { Glossary, ProxyRule, Route, StyleRule } from '../types';
import type { RouteProfile } from '../api';

const store = useRoutesStore();
const glossaries = ref<Glossary[]>([]);
const styleRules = ref<StyleRule[]>([]);
const proxyRules = ref<ProxyRule[]>([]);
const profiles = ref<RouteProfile[]>([]);

onMounted(async () => {
  await store.refresh();
  try {
    [glossaries.value, styleRules.value, proxyRules.value, profiles.value] = await Promise.all([
      api.glossaries.list(),
      api.styleRules.list(),
      api.proxyRules.list(),
      api.routeProfiles.list(),
    ]);
  } catch { /* ignore load errors — form still works without dropdowns */ }
});

const showCreate = ref(false);
const editingId = ref<string | null>(null);
const form = ref<Partial<Route>>(emptyForm());

const showAdvancedBindings = ref(false);

const directionArrow = computed(() => {
  switch (form.value.direction) {
    case 'RequestOnly': return '→';
    case 'ResponseOnly': return '←';
    case 'Off': return '·';
    default: return '⇄';
  }
});

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
    profileId: null,
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
      profileId: form.value.profileId || null,
    };
    if (editingId.value) {
      await api.routes.update(editingId.value, normalised);
    } else {
      await api.routes.create(normalised as Route);
    }
    await store.refresh();
    cancel();
  } catch (e: unknown) {
    formError.value = friendlyError(e);
  } finally {
    submitting.value = false;
  }
}

async function issueToken(id: string) {
  try {
    const res = await api.routes.issueToken(id);
    issuedToken.value = { routeId: id, token: res.plaintextToken };
  } catch (e: unknown) {
    formError.value = friendlyError(e);
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
          Bindings below control what adaptiveapi applies when this route's token is used.
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
        <div class="col-span-2 flex flex-col gap-1 text-xs">
          <span>Language pair</span>
          <div class="flex items-center gap-2">
            <input class="input w-24 text-center font-mono" v-model="form.userLanguage" placeholder="de"
                   :title="'Your users speak this'" />
            <select class="input w-32 text-center" v-model="form.direction" :title="'Direction'">
              <option value="Bidirectional">{{ '⇄' }} Both</option>
              <option value="RequestOnly">{{ '→' }} Request only</option>
              <option value="ResponseOnly">{{ '←' }} Response only</option>
              <option value="Off">{{ '·' }} Off</option>
            </select>
            <input class="input w-24 text-center font-mono" v-model="form.llmLanguage" placeholder="en-US"
                   :title="'LLM speaks this'" />
            <span class="text-surface-500 ml-2">
              {{ form.userLanguage || 'user' }} {{ directionArrow }} {{ form.llmLanguage || 'llm' }}
            </span>
          </div>
          <span class="text-surface-500">
            Your users speak the left side; the LLM works in the right side.
            "Both" translates each direction; "Off" disables translation
            (useful for routing without translating).
          </span>
        </div>
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

        <div class="col-span-2 border-t border-surface-200 pt-3 mt-1 flex flex-col gap-1">
          <label class="flex flex-col gap-1 text-xs">
            Profile
            <select class="input" v-model="form.profileId">
              <option :value="null">— no profile (configure each binding below) —</option>
              <option v-for="p in profiles" :key="p.id" :value="p.id">
                {{ p.name }} · <span class="font-mono">{{ p.id }}</span>
              </option>
            </select>
            <span class="text-surface-500 text-xs">
              Profiles bundle the glossary + styles + proxy rule for reuse
              across routes. Per-route overrides below still win when set.
              Manage profiles on the
              <RouterLink to="/route-profiles" class="text-brand-600 underline">Profiles</RouterLink>
              page.
            </span>
          </label>
          <button type="button" class="text-xs text-brand-600 self-start"
                  @click="showAdvancedBindings = !showAdvancedBindings">
            {{ showAdvancedBindings ? 'Hide' : 'Show' }} per-route bindings
            (override profile)
          </button>
        </div>

        <template v-if="showAdvancedBindings || !form.profileId">
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
        </template>

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

    <div v-if="store.routes.length === 0 && !store.loading && !showCreate" class="card">
      <div class="card-body text-center py-10 flex flex-col items-center gap-3">
        <div class="i-carbon-flow text-4xl text-surface-400" />
        <div class="text-base font-500">No routes yet</div>
        <div class="text-sm text-surface-700 max-w-md">
          A route is the unit your SDK points at — pick an LLM provider and a
          language pair, and you get back a token to use as your
          <code>base_url</code>.
        </div>
        <div class="flex gap-2 mt-2">
          <RouterLink to="/wizard" class="btn-primary">
            <span class="i-carbon-rocket" /> Setup wizard
          </RouterLink>
          <button class="btn" @click="startNew">
            <span class="i-carbon-add" /> Create manually
          </button>
        </div>
      </div>
    </div>

    <div v-else class="card">
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
        </tbody>
      </table>
    </div>
  </div>
</template>
