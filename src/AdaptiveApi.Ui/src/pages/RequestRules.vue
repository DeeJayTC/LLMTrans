<script setup lang="ts">
import { onMounted, reactive, ref, watch } from 'vue';
import { api, friendlyError, type RequestRule } from '../api';

// UI-managed regex rules. Same idea as PII rules but generic-purpose: scope
// (where to look), regex (what to match), action (what to do). Backed by a
// built-in hook so the rules apply to every route in every adapter without
// touching code.
const tenantId = ref('t_dev');
const rules = ref<RequestRule[]>([]);
const loading = ref(false);
const error = ref<string | null>(null);

interface Form {
  id: string;
  routeId: string;
  name: string;
  description: string;
  scope: RequestRule['scope'];
  headerName: string;
  pattern: string;
  caseInsensitive: boolean;
  multiline: boolean;
  action: RequestRule['action'];
  blockStatus: number;
  actionPayload: string;
  actionHeader: string;
  priority: number;
  enabled: boolean;
}

function blank(): Form {
  return {
    id: '',
    routeId: '',
    name: '',
    description: '',
    scope: 'request-body',
    headerName: '',
    pattern: '',
    caseInsensitive: false,
    multiline: false,
    action: 'log',
    blockStatus: 403,
    actionPayload: '',
    actionHeader: '',
    priority: 100,
    enabled: true,
  };
}

const form = ref<Form>(blank());
const editing = ref<RequestRule | null>(null);
const creating = ref(false);
const saving = ref(false);

async function refresh() {
  loading.value = true;
  error.value = null;
  try {
    rules.value = await api.requestRules.list(tenantId.value);
  } catch (e: unknown) {
    error.value = friendlyError(e);
  } finally {
    loading.value = false;
  }
}

function startNew() {
  form.value = blank();
  creating.value = true;
  editing.value = null;
}

function startEdit(r: RequestRule) {
  let flags = { caseInsensitive: false, multiline: false };
  if (r.flagsJson) {
    try { Object.assign(flags, JSON.parse(r.flagsJson)); } catch {}
  }
  form.value = {
    id: r.id,
    routeId: r.routeId ?? '',
    name: r.name,
    description: r.description ?? '',
    scope: r.scope,
    headerName: r.headerName ?? '',
    pattern: r.pattern,
    caseInsensitive: flags.caseInsensitive,
    multiline: flags.multiline,
    action: r.action,
    blockStatus: r.blockStatus ?? 403,
    actionPayload: r.actionPayload ?? '',
    actionHeader: r.actionHeader ?? '',
    priority: r.priority,
    enabled: r.enabled,
  };
  editing.value = r;
  creating.value = false;
}

function cancel() {
  creating.value = false;
  editing.value = null;
  error.value = null;
}

async function save() {
  saving.value = true;
  error.value = null;
  try {
    const flagsObj: Record<string, boolean> = {};
    if (form.value.caseInsensitive) flagsObj.caseInsensitive = true;
    if (form.value.multiline) flagsObj.multiline = true;
    const flagsJson = Object.keys(flagsObj).length ? JSON.stringify(flagsObj) : null;

    const payload = {
      routeId: form.value.routeId || null,
      name: form.value.name,
      description: form.value.description || null,
      scope: form.value.scope,
      headerName: form.value.headerName || null,
      pattern: form.value.pattern,
      flagsJson,
      action: form.value.action,
      blockStatus: form.value.action === 'block' ? form.value.blockStatus : null,
      actionPayload: form.value.actionPayload || null,
      actionHeader: form.value.action === 'set-header' ? (form.value.actionHeader || null) : null,
      priority: form.value.priority,
      enabled: form.value.enabled,
    };

    if (creating.value) {
      const id = form.value.id || `rule_${Date.now().toString(36)}`;
      await api.requestRules.create({ ...payload, id, tenantId: tenantId.value });
    } else if (editing.value) {
      await api.requestRules.update(editing.value.id, payload);
    }
    await refresh();
    cancel();
  } catch (e: unknown) {
    error.value = friendlyError(e);
  } finally {
    saving.value = false;
  }
}

async function remove(id: string) {
  if (!confirm(`Delete rule ${id}?`)) return;
  await api.requestRules.delete(id);
  if (editing.value?.id === id) cancel();
  await refresh();
}

async function toggle(r: RequestRule) {
  await api.requestRules.update(r.id, { enabled: !r.enabled });
  await refresh();
}

// Live tester next to the editor — same shape as the PII tester.
const tester = reactive({
  sample: 'POST /v1/chat/completions\n{"model":"gpt-4o","messages":[{"role":"user","content":"Hello"}]}',
  replacement: '',
  result: null as { match: boolean; replaced: string | null; error: string | null } | null,
});
let testTimer: ReturnType<typeof setTimeout> | null = null;
async function runTest() {
  if (!form.value.pattern) { tester.result = null; return; }
  const flagsObj: Record<string, boolean> = {};
  if (form.value.caseInsensitive) flagsObj.caseInsensitive = true;
  if (form.value.multiline) flagsObj.multiline = true;
  try {
    tester.result = await api.requestRules.test({
      pattern: form.value.pattern,
      flagsJson: Object.keys(flagsObj).length ? JSON.stringify(flagsObj) : null,
      sampleText: tester.sample,
      replacement: form.value.action === 'replace' ? (form.value.actionPayload || null) : (tester.replacement || null),
    });
  } catch (e: unknown) {
    tester.result = { match: false, replaced: null, error: friendlyError(e) };
  }
}
watch([
  () => form.value.pattern,
  () => form.value.caseInsensitive,
  () => form.value.multiline,
  () => form.value.action,
  () => form.value.actionPayload,
  () => tester.sample,
  () => tester.replacement,
], () => {
  if (testTimer) clearTimeout(testTimer);
  testTimer = setTimeout(runTest, 250);
});

onMounted(refresh);
</script>

<template>
  <div class="flex flex-col gap-4">
    <div class="card">
      <div class="card-header flex items-center gap-2">
        <span>Request rules</span>
        <span class="text-xs text-surface-700 flex-1">
          UI-managed regex rules. Each rule picks a scope (where to look),
          a pattern, and an action. Built into the proxy — no DLL build /
          deploy / restart cycle. For the C# escape hatch see the
          Plugins page.
        </span>
        <button class="btn-primary" @click="startNew">
          <span class="i-carbon-add" /> New rule
        </button>
      </div>
      <div v-if="error" class="card-body text-red-700 text-sm">{{ error }}</div>
    </div>

    <div v-if="creating || editing" class="grid grid-cols-[1fr_400px] gap-4">
      <div class="card">
        <div class="card-header">{{ creating ? 'New rule' : `Edit ${form.id}` }}</div>
        <form class="card-body grid grid-cols-2 gap-3" @submit.prevent="save">
          <label v-if="creating" class="flex flex-col gap-1 text-xs">
            ID
            <input class="input font-mono text-xs" v-model="form.id" placeholder="auto" />
          </label>
          <label class="flex flex-col gap-1 text-xs" :class="creating ? '' : 'col-span-2'">
            Name
            <input class="input" v-model="form.name" required placeholder="block credit cards in body" />
          </label>
          <label class="flex flex-col gap-1 text-xs col-span-2">
            Description
            <input class="input" v-model="form.description" placeholder="Optional notes for the team" />
          </label>

          <label class="flex flex-col gap-1 text-xs">
            Scope
            <select class="input" v-model="form.scope">
              <option value="request-body">Request body</option>
              <option value="response-body">Response body</option>
              <option value="request-header">Request header</option>
              <option value="response-header">Response header</option>
              <option value="path">Request path</option>
            </select>
          </label>
          <label class="flex flex-col gap-1 text-xs"
                 v-if="form.scope === 'request-header' || form.scope === 'response-header'">
            Header name
            <input class="input font-mono text-xs" v-model="form.headerName" placeholder="X-Forwarded-User" />
          </label>
          <label class="flex flex-col gap-1 text-xs">
            Route ID (optional)
            <input class="input font-mono text-xs" v-model="form.routeId"
                   placeholder="empty = all routes in tenant" />
          </label>

          <label class="flex flex-col gap-1 text-xs col-span-2">
            Pattern (.NET regex)
            <input class="input font-mono text-xs" v-model="form.pattern"
                   placeholder="\bACME-\d{6,8}\b" required />
          </label>
          <label class="flex items-center gap-2 text-xs">
            <input type="checkbox" v-model="form.caseInsensitive" /> case-insensitive
          </label>
          <label class="flex items-center gap-2 text-xs">
            <input type="checkbox" v-model="form.multiline" /> multiline
          </label>

          <label class="flex flex-col gap-1 text-xs">
            Action
            <select class="input" v-model="form.action">
              <option value="log">Log — record a structured log line</option>
              <option value="replace">Replace — substitute matched groups</option>
              <option value="set-header">Set header on the response</option>
              <option value="block">Block — short-circuit with status + body</option>
            </select>
          </label>
          <label class="flex flex-col gap-1 text-xs" v-if="form.action === 'block'">
            Block status code
            <input type="number" class="input" v-model.number="form.blockStatus" min="400" max="599" />
          </label>
          <label class="flex flex-col gap-1 text-xs col-span-2"
                 v-if="form.action === 'replace'">
            Replacement (regex syntax — $1, $2 capture groups)
            <input class="input font-mono text-xs" v-model="form.actionPayload"
                   placeholder="[redacted-acme-id]" />
          </label>
          <label class="flex flex-col gap-1 text-xs col-span-2"
                 v-if="form.action === 'block'">
            Block body (JSON; sent as application/json)
            <textarea class="input font-mono text-xs" rows="3" v-model="form.actionPayload"
                      placeholder='{"error":"forbidden by policy"}'></textarea>
          </label>
          <template v-if="form.action === 'set-header'">
            <label class="flex flex-col gap-1 text-xs">
              Header name
              <input class="input font-mono text-xs" v-model="form.actionHeader"
                     placeholder="X-Adaptive-Tag" />
            </label>
            <label class="flex flex-col gap-1 text-xs">
              Header value
              <input class="input font-mono text-xs" v-model="form.actionPayload"
                     placeholder="touched" />
            </label>
          </template>

          <label class="flex flex-col gap-1 text-xs">
            Priority (lower runs first)
            <input type="number" class="input" v-model.number="form.priority" />
          </label>
          <label class="flex items-center gap-2 text-xs mt-4">
            <input type="checkbox" v-model="form.enabled" /> enabled
          </label>

          <div class="col-span-2 flex justify-end gap-2">
            <button type="button" class="btn" @click="cancel">Cancel</button>
            <button type="submit" class="btn-primary" :disabled="saving">
              {{ saving ? 'Saving…' : creating ? 'Create' : 'Save' }}
            </button>
          </div>
        </form>
      </div>

      <!-- ===== Live tester ===== -->
      <div class="card">
        <div class="card-header">
          <span>Live tester</span>
          <span class="text-xs text-surface-700">runs your pattern against the sample</span>
        </div>
        <div class="card-body flex flex-col gap-2">
          <label class="flex flex-col gap-1 text-xs">
            Sample text
            <textarea class="input font-mono text-xs" rows="6" v-model="tester.sample"></textarea>
          </label>
          <label class="flex flex-col gap-1 text-xs" v-if="form.action !== 'replace'">
            Replacement (preview only)
            <input class="input font-mono text-xs" v-model="tester.replacement" />
          </label>
          <div v-if="tester.result?.error" class="text-xs text-red-700 font-mono">
            {{ tester.result.error }}
          </div>
          <div v-else-if="tester.result?.match === false" class="text-xs text-surface-700">
            No match.
          </div>
          <div v-else-if="tester.result?.match" class="text-xs">
            <div class="text-green-700 font-500 mb-1">Pattern matches.</div>
            <pre v-if="tester.result.replaced !== null"
                 class="bg-green-50 px-2 py-1 rounded">{{ tester.result.replaced }}</pre>
          </div>
        </div>
      </div>
    </div>

    <div v-if="rules.length === 0 && !loading && !creating && !editing" class="card">
      <div class="card-body text-center py-10 flex flex-col items-center gap-3">
        <div class="i-carbon-rule text-4xl text-surface-400" />
        <div class="text-base font-500">No request rules yet</div>
        <div class="text-sm text-surface-700 max-w-md">
          Useful examples: block requests containing internal customer IDs,
          stamp an audit header on all incoming bodies, or redact a custom
          token format the PII pack doesn't cover.
        </div>
        <button class="btn-primary mt-2" @click="startNew">
          <span class="i-carbon-add" /> Create your first rule
        </button>
      </div>
    </div>

    <div v-else-if="rules.length > 0" class="card">
      <table>
        <thead>
          <tr>
            <th class="w-20">Enabled</th>
            <th>ID</th>
            <th>Name</th>
            <th>Scope</th>
            <th>Action</th>
            <th>Pattern</th>
            <th>Priority</th>
            <th />
          </tr>
        </thead>
        <tbody>
          <tr v-for="r in rules" :key="r.id">
            <td>
              <label class="inline-flex items-center gap-2">
                <input type="checkbox" :checked="r.enabled" @change="toggle(r)" />
                <span class="text-xs">{{ r.enabled ? 'on' : 'off' }}</span>
              </label>
            </td>
            <td class="font-mono text-xs">{{ r.id }}</td>
            <td>{{ r.name }}</td>
            <td><span class="chip text-xs">{{ r.scope }}</span></td>
            <td><span class="chip text-xs">{{ r.action }}</span></td>
            <td class="font-mono text-xs truncate max-w-xs">{{ r.pattern }}</td>
            <td class="text-xs">{{ r.priority }}</td>
            <td class="text-right whitespace-nowrap">
              <button class="btn" @click="startEdit(r)">Edit</button>
              <button class="btn btn-danger ml-2" @click="remove(r.id)">Delete</button>
            </td>
          </tr>
        </tbody>
      </table>
    </div>
  </div>
</template>
