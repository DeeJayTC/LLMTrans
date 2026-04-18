<script setup lang="ts">
import { computed, onMounted, ref } from 'vue';
import { api } from '../api';
import type { ProxyRule } from '../types';

const rules = ref<ProxyRule[]>([]);
const loading = ref(false);
const saving = ref(false);
const error = ref<string | null>(null);

const editing = ref<ProxyRule | null>(null);
const creating = ref(false);

interface EditorState {
  id: string;
  tenantId: string;
  name: string;
  priority: number;
  redactPii: boolean;
  formality: 'default' | 'more' | 'less' | 'prefer_more' | 'prefer_less' | '';
  requestPaths: string[];
  responsePaths: string[];
  extraToolArgKeys: string[];
}

const form = ref<EditorState>(blank());

function blank(): EditorState {
  return {
    id: '',
    tenantId: 't_dev',
    name: '',
    priority: 10,
    redactPii: false,
    formality: '',
    requestPaths: [],
    responsePaths: [],
    extraToolArgKeys: [],
  };
}

function loadInto(rule: ProxyRule) {
  let req: string[] = [];
  let resp: string[] = [];
  try {
    const al = JSON.parse(rule.allowlistJson ?? '{}');
    if (Array.isArray(al.request)) req = al.request as string[];
    if (Array.isArray(al.response)) resp = al.response as string[];
  } catch { /* leave empty */ }

  let extraKeys: string[] = [];
  try {
    const dl = JSON.parse(rule.denylistJson ?? '{}');
    if (Array.isArray(dl.toolArgKeys)) extraKeys = dl.toolArgKeys as string[];
  } catch { /* ignore */ }

  form.value = {
    id: rule.id,
    tenantId: rule.tenantId,
    name: rule.name,
    priority: rule.priority,
    redactPii: (rule as ProxyRule & { redactPii?: boolean }).redactPii ?? false,
    formality: (rule.formality ?? '') as EditorState['formality'],
    requestPaths: req,
    responsePaths: resp,
    extraToolArgKeys: extraKeys,
  };
  creating.value = false;
  editing.value = rule;
}

function startNew() {
  form.value = blank();
  creating.value = true;
  editing.value = null;
}

function addPath(which: 'request' | 'response') {
  const arr = which === 'request' ? form.value.requestPaths : form.value.responsePaths;
  arr.push('$.');
}

function removePath(which: 'request' | 'response', idx: number) {
  const arr = which === 'request' ? form.value.requestPaths : form.value.responsePaths;
  arr.splice(idx, 1);
}

function addDenylistKey() {
  form.value.extraToolArgKeys.push('');
}

function removeDenylistKey(idx: number) {
  form.value.extraToolArgKeys.splice(idx, 1);
}

const payload = computed(() => {
  const f = form.value;
  const allowlist: Record<string, string[]> = {};
  if (f.requestPaths.filter(Boolean).length) allowlist.request = f.requestPaths.filter(Boolean);
  if (f.responsePaths.filter(Boolean).length) allowlist.response = f.responsePaths.filter(Boolean);

  const denylist: Record<string, string[]> = {};
  if (f.extraToolArgKeys.filter(Boolean).length) denylist.toolArgKeys = f.extraToolArgKeys.filter(Boolean);

  return {
    id: f.id,
    tenantId: f.tenantId,
    name: f.name,
    scopeJson: '{}',
    allowlistJson: Object.keys(allowlist).length ? JSON.stringify(allowlist) : null,
    denylistJson: Object.keys(denylist).length ? JSON.stringify(denylist) : null,
    priority: f.priority,
    formality: f.formality || null,
    redactPii: f.redactPii,
  };
});

const payloadPreview = computed(() => JSON.stringify(payload.value, null, 2));

async function refresh() {
  loading.value = true;
  error.value = null;
  try { rules.value = await api.proxyRules.list(); }
  catch (e: unknown) { error.value = e instanceof Error ? e.message : String(e); }
  finally { loading.value = false; }
}

async function save() {
  saving.value = true;
  error.value = null;
  try {
    await api.proxyRules.create(payload.value);
    await refresh();
    creating.value = false;
    editing.value = null;
  } catch (e: unknown) { error.value = e instanceof Error ? e.message : String(e); }
  finally { saving.value = false; }
}

async function remove(id: string) {
  if (!confirm(`Delete proxy rule ${id}?`)) return;
  await api.proxyRules.delete(id);
  if (editing.value?.id === id) { editing.value = null; creating.value = false; }
  await refresh();
}

onMounted(refresh);
</script>

<template>
  <div class="grid grid-cols-[380px_1fr] gap-4" style="min-height: calc(100vh - 200px)">
    <div class="card flex flex-col overflow-hidden">
      <div class="card-header">
        <span>Rules</span>
        <button class="btn-primary" @click="startNew">
          <span class="i-carbon-add" /> New
        </button>
      </div>
      <div class="flex-1 overflow-auto">
        <div v-for="r in rules" :key="r.id"
             class="px-3 py-2 border-b border-surface-200 cursor-pointer hover:bg-surface-50"
             :class="editing?.id === r.id ? 'bg-brand-50' : ''"
             @click="loadInto(r)">
          <div class="flex items-center justify-between gap-2">
            <div class="font-mono text-xs truncate">{{ r.id }}</div>
            <span class="chip text-xs">prio {{ r.priority }}</span>
          </div>
          <div class="text-sm">{{ r.name }}</div>
          <div class="text-xs text-surface-700 mt-1">
            {{ r.allowlistJson ? 'allowlist' : '' }}
            {{ r.denylistJson ? '+ denylist' : '' }}
            {{ r.formality ? `· formality=${r.formality}` : '' }}
          </div>
        </div>
        <div v-if="rules.length === 0 && !loading" class="text-center p-6 text-surface-700 text-sm">
          No proxy rules yet.
        </div>
      </div>
    </div>

    <div class="flex flex-col gap-3">
      <div v-if="!creating && !editing" class="card flex-1">
        <div class="card-body text-sm text-surface-700">
          <p class="mb-2">Pick a rule on the left to edit, or create a new one.</p>
          <p>
            Proxy rules override the default allowlist and denylist on a per-route basis and
            optionally turn on <code>redactPii</code>. The editor builds the JSON payload as
            you type — the rendered preview on the right is what the admin API receives.
          </p>
        </div>
      </div>

      <template v-else>
        <div class="card">
          <div class="card-header">
            {{ creating ? 'New proxy rule' : `Edit ${form.id}` }}
          </div>
          <div class="card-body grid grid-cols-4 gap-3">
            <label class="flex flex-col gap-1 text-xs">
              ID
              <input class="input" v-model="form.id" :disabled="!creating" />
            </label>
            <label class="flex flex-col gap-1 text-xs">
              Tenant
              <input class="input" v-model="form.tenantId" />
            </label>
            <label class="flex flex-col gap-1 text-xs col-span-2">
              Name
              <input class="input" v-model="form.name" />
            </label>
            <label class="flex flex-col gap-1 text-xs">
              Priority
              <input class="input" type="number" v-model.number="form.priority" />
            </label>
            <label class="flex flex-col gap-1 text-xs">
              Formality
              <select class="input" v-model="form.formality">
                <option value="">(inherit)</option>
                <option value="default">default</option>
                <option value="more">more</option>
                <option value="less">less</option>
                <option value="prefer_more">prefer_more</option>
                <option value="prefer_less">prefer_less</option>
              </select>
            </label>
            <label class="flex items-center gap-2 text-xs col-span-2 pt-5">
              <input type="checkbox" v-model="form.redactPii" />
              Redact PII before upstream sees it
            </label>
          </div>
        </div>

        <div class="card">
          <div class="card-header">
            <span>Allowlist JSONPaths</span>
            <span class="text-xs text-surface-700">override the default per-provider allowlist</span>
          </div>
          <div class="card-body flex flex-col gap-3">
            <div>
              <div class="flex items-center justify-between text-xs text-surface-700 mb-1">
                <span class="font-500">Request</span>
                <button class="btn text-xs" @click="addPath('request')">+ path</button>
              </div>
              <div class="flex flex-col gap-1">
                <div v-for="(p, i) in form.requestPaths" :key="'req' + i" class="flex gap-1">
                  <input class="input font-mono text-xs flex-1" v-model="form.requestPaths[i]"
                         placeholder="$.messages[*].content" />
                  <button class="btn btn-danger text-xs" @click="removePath('request', i)">✕</button>
                </div>
              </div>
            </div>
            <div>
              <div class="flex items-center justify-between text-xs text-surface-700 mb-1">
                <span class="font-500">Response</span>
                <button class="btn text-xs" @click="addPath('response')">+ path</button>
              </div>
              <div class="flex flex-col gap-1">
                <div v-for="(p, i) in form.responsePaths" :key="'resp' + i" class="flex gap-1">
                  <input class="input font-mono text-xs flex-1" v-model="form.responsePaths[i]"
                         placeholder="$.choices[*].message.content" />
                  <button class="btn btn-danger text-xs" @click="removePath('response', i)">✕</button>
                </div>
              </div>
            </div>
          </div>
        </div>

        <div class="card">
          <div class="card-header">
            <span>Tool-arg denylist extensions</span>
            <button class="btn text-xs" @click="addDenylistKey">+ key</button>
          </div>
          <div class="card-body flex flex-col gap-1">
            <p class="text-xs text-surface-700 mb-2">
              Extra JSON keys whose string values must never reach the upstream translator.
              Added on top of the default denylist
              (<code>id</code>, <code>*_id</code>, <code>*_code</code>, etc.).
            </p>
            <div v-for="(k, i) in form.extraToolArgKeys" :key="'dk' + i" class="flex gap-1">
              <input class="input font-mono text-xs flex-1" v-model="form.extraToolArgKeys[i]"
                     placeholder="internal_note" />
              <button class="btn btn-danger text-xs" @click="removeDenylistKey(i)">✕</button>
            </div>
          </div>
        </div>

        <div class="card">
          <div class="card-header">Payload preview</div>
          <pre class="m-0 rounded-none text-xs">{{ payloadPreview }}</pre>
        </div>

        <div class="flex items-center gap-2">
          <button v-if="editing" class="btn btn-danger" @click="remove(editing.id)">Delete</button>
          <span class="ml-auto flex items-center gap-2">
            <span v-if="error" class="text-xs text-red-700">{{ error }}</span>
            <button class="btn" @click="editing = null; creating = false">Cancel</button>
            <button class="btn-primary" :disabled="saving" @click="save">
              {{ saving ? 'Saving…' : creating ? 'Create' : 'Replace' }}
            </button>
          </span>
        </div>
      </template>
    </div>
  </div>
</template>
