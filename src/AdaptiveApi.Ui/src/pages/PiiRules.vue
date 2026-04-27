<script setup lang="ts">
import { computed, onMounted, reactive, ref, watch } from 'vue';
import { api } from '../api';
import type { PiiPack, PiiRule, PiiRuleFlags, PiiTestMatch } from '../types';

// Tenant context. The seeder always creates `t_dev`. Multi-tenant deployments
// will surface a picker; for now this matches the rest of the admin UI.
const tenantId = ref('t_dev');

// ---------- packs ----------
const packs = ref<PiiPack[]>([]);
const expandedPack = ref<string | null>(null);
const packLoading = ref(false);

// ---------- custom rules ----------
const rules = ref<PiiRule[]>([]);
const editing = ref<PiiRule | null>(null);
const creating = ref(false);
const ruleLoading = ref(false);
const ruleSaving = ref(false);
const ruleError = ref<string | null>(null);

interface RuleForm {
  id: string;
  name: string;
  description: string;
  pattern: string;
  replacement: string;
  enabled: boolean;
  flags: PiiRuleFlags;
}

function blankRule(): RuleForm {
  return {
    id: '',
    name: '',
    description: '',
    pattern: '',
    replacement: '[redacted]',
    enabled: true,
    flags: { caseInsensitive: false, multiline: false, luhnValidate: false },
  };
}

const form = ref<RuleForm>(blankRule());

function startNew() {
  form.value = blankRule();
  creating.value = true;
  editing.value = null;
}

function loadInto(rule: PiiRule) {
  form.value = {
    id: rule.id,
    name: rule.name,
    description: rule.description ?? '',
    pattern: rule.pattern,
    replacement: rule.replacement,
    enabled: rule.enabled,
    flags: { ...rule.flags },
  };
  creating.value = false;
  editing.value = rule;
}

async function refreshAll() {
  packLoading.value = true;
  ruleLoading.value = true;
  try {
    const [p, r] = await Promise.all([api.piiPacks.list(), api.piiRules.list(tenantId.value)]);
    packs.value = p;
    rules.value = r;
  } finally {
    packLoading.value = false;
    ruleLoading.value = false;
  }
}

async function saveRule() {
  ruleSaving.value = true;
  ruleError.value = null;
  try {
    if (creating.value) {
      const id = form.value.id || `pii_${Date.now().toString(36)}`;
      await api.piiRules.create({
        id,
        tenantId: tenantId.value,
        name: form.value.name || id,
        description: form.value.description || null,
        pattern: form.value.pattern,
        replacement: form.value.replacement,
        flags: form.value.flags,
        enabled: form.value.enabled,
      });
    } else if (editing.value) {
      await api.piiRules.update(editing.value.id, {
        name: form.value.name,
        description: form.value.description || null,
        pattern: form.value.pattern,
        replacement: form.value.replacement,
        flags: form.value.flags,
        enabled: form.value.enabled,
      });
    }
    await refreshAll();
    creating.value = false;
    editing.value = null;
  } catch (e: unknown) {
    ruleError.value = e instanceof Error ? e.message : String(e);
  } finally {
    ruleSaving.value = false;
  }
}

async function removeRule(id: string) {
  if (!confirm(`Delete custom rule ${id}?`)) return;
  await api.piiRules.delete(id);
  if (editing.value?.id === id) { editing.value = null; creating.value = false; }
  await refreshAll();
}

// ---------- tester ----------
const testState = reactive({
  text: 'Contact john.doe@example.com or call +1 (415) 555-1212. Card 4242 4242 4242 4242.',
  selectedPacks: new Set<string>(['pack_default']),
  selectedRules: new Set<string>(),
  disabledDetectors: new Set<string>(),
});
const testRunning = ref(false);
const testResult = ref<{ redactedText: string; matches: PiiTestMatch[]; errors: string[] } | null>(null);

function togglePack(slug: string) {
  if (testState.selectedPacks.has(slug)) testState.selectedPacks.delete(slug);
  else testState.selectedPacks.add(slug);
}

function toggleDetector(slug: string, kind: string) {
  const key = `${slug}:${kind}`;
  if (testState.disabledDetectors.has(key)) testState.disabledDetectors.delete(key);
  else testState.disabledDetectors.add(key);
}

function toggleRule(id: string) {
  if (testState.selectedRules.has(id)) testState.selectedRules.delete(id);
  else testState.selectedRules.add(id);
}

async function runTest() {
  testRunning.value = true;
  try {
    testResult.value = await api.piiRules.test({
      text: testState.text,
      packSlugs: [...testState.selectedPacks],
      ruleIds: [...testState.selectedRules],
      disabledDetectors: [...testState.disabledDetectors],
      tenantId: tenantId.value,
    });
  } finally {
    testRunning.value = false;
  }
}

// Live tester for the rule currently being edited.
const editorPreview = ref<{ matches: PiiTestMatch[]; error: string | null }>({
  matches: [], error: null,
});
let previewTimer: ReturnType<typeof setTimeout> | null = null;
async function refreshEditorPreview() {
  if (!form.value.pattern) {
    editorPreview.value = { matches: [], error: null };
    return;
  }
  try {
    const r = await api.piiRules.test({
      text: testState.text,
      adHocPattern: form.value.pattern,
      adHocReplacement: form.value.replacement || '[redacted]',
      adHocKind: form.value.name || 'custom',
      adHocFlags: form.value.flags,
    });
    editorPreview.value = {
      matches: r.matches.filter(m => m.kind === (form.value.name || 'custom')),
      error: r.errors[0] ?? null,
    };
  } catch (e: unknown) {
    editorPreview.value = { matches: [], error: e instanceof Error ? e.message : String(e) };
  }
}
watch(() => [form.value.pattern, form.value.replacement, form.value.name, form.value.flags], () => {
  if (previewTimer) clearTimeout(previewTimer);
  previewTimer = setTimeout(refreshEditorPreview, 250);
}, { deep: true });

watch(() => testState.text, () => {
  if (previewTimer) clearTimeout(previewTimer);
  previewTimer = setTimeout(refreshEditorPreview, 250);
});

onMounted(refreshAll);

const expandedPackData = computed(() =>
  expandedPack.value ? packs.value.find(p => p.slug === expandedPack.value) ?? null : null);
</script>

<template>
  <div class="flex flex-col gap-4">
    <div class="card">
      <div class="card-header">
        <span>PII rules</span>
        <span class="text-xs text-surface-700">
          Built-in packs ship with AdaptiveAPI. Toggle them per route via the proxy rule.
          Custom rules below extend the detector set with tenant-specific patterns.
        </span>
      </div>
    </div>

    <!-- ===== premade packs ===== -->
    <div class="card">
      <div class="card-header">
        <span>Premade packs</span>
        <span class="text-xs text-surface-700">
          Packs ship as part of AdaptiveAPI. Click a pack to expand its detectors.
          Use the per-detector checkboxes inside the tester to disable individual
          patterns inside an otherwise-selected pack.
        </span>
      </div>
      <div class="card-body flex flex-col gap-2">
        <div v-for="pack in packs" :key="pack.slug"
             class="border border-surface-200 rounded-md">
          <div class="px-3 py-2 flex items-center gap-3 cursor-pointer hover:bg-surface-50"
               @click="expandedPack = expandedPack === pack.slug ? null : pack.slug">
            <input type="checkbox" :checked="testState.selectedPacks.has(pack.slug)"
                   @click.stop="togglePack(pack.slug)" />
            <div class="flex-1">
              <div class="text-sm font-500">{{ pack.name }}</div>
              <div class="text-xs text-surface-700">{{ pack.description }}</div>
            </div>
            <span class="chip text-xs">{{ pack.detectors.length }} detectors</span>
            <span class="font-mono text-xs text-surface-700">{{ pack.slug }}</span>
            <span :class="expandedPack === pack.slug ? 'i-carbon-chevron-up' : 'i-carbon-chevron-down'" />
          </div>
          <div v-if="expandedPack === pack.slug" class="border-t border-surface-200 p-3 bg-surface-0">
            <table class="table w-full text-xs">
              <thead>
                <tr>
                  <th class="w-8"></th>
                  <th>Kind</th>
                  <th>Replacement</th>
                  <th>Pattern</th>
                  <th>Flags</th>
                </tr>
              </thead>
              <tbody>
                <tr v-for="d in pack.detectors" :key="d.kind">
                  <td>
                    <input type="checkbox"
                           :checked="!testState.disabledDetectors.has(`${pack.slug}:${d.kind}`)"
                           @change="toggleDetector(pack.slug, d.kind)"
                           :title="`Disable ${d.kind} inside ${pack.slug}`" />
                  </td>
                  <td class="font-mono">{{ d.kind }}</td>
                  <td><code>{{ d.replacement }}</code></td>
                  <td class="font-mono break-all">{{ d.pattern }}</td>
                  <td>
                    <span v-if="d.luhnValidate" class="chip text-xs">luhn</span>
                    <span v-for="f in (d.flags ?? [])" :key="f" class="chip text-xs">{{ f }}</span>
                  </td>
                </tr>
              </tbody>
            </table>
          </div>
        </div>
        <div v-if="!packLoading && packs.length === 0" class="text-sm text-surface-700 p-3">
          No packs found. The bootstrap seeder should populate these on first run.
        </div>
      </div>
    </div>

    <!-- ===== custom rules ===== -->
    <div class="grid grid-cols-[380px_1fr] gap-4">
      <div class="card flex flex-col overflow-hidden">
        <div class="card-header">
          <span>Custom rules</span>
          <button class="btn-primary" @click="startNew">
            <span class="i-carbon-add" /> New
          </button>
        </div>
        <div class="flex-1 overflow-auto">
          <div v-for="r in rules" :key="r.id"
               class="px-3 py-2 border-b border-surface-200 cursor-pointer hover:bg-surface-50"
               :class="editing?.id === r.id ? 'bg-brand-50' : ''"
               @click="loadInto(r)">
            <div class="flex items-center gap-2">
              <input type="checkbox" :checked="testState.selectedRules.has(r.id)"
                     @click.stop="toggleRule(r.id)"
                     :title="`Include in tester`" />
              <div class="flex-1">
                <div class="font-mono text-xs truncate">{{ r.id }}</div>
                <div class="text-sm">{{ r.name }}</div>
                <div class="text-xs text-surface-700 truncate">{{ r.pattern }}</div>
              </div>
              <span v-if="!r.enabled" class="chip text-xs">disabled</span>
            </div>
          </div>
          <div v-if="rules.length === 0 && !ruleLoading" class="text-center p-6 text-surface-700 text-sm">
            No custom rules yet. Click <strong>New</strong> to add one.
          </div>
        </div>
      </div>

      <div class="flex flex-col gap-3">
        <div v-if="!creating && !editing" class="card flex-1">
          <div class="card-body text-sm text-surface-700">
            <p class="mb-2">Pick a rule on the left to edit, or click <strong>New</strong> to add one.</p>
            <p>
              Custom rules are tenant-scoped regexes. Bind them to a route in the
              proxy-rule editor. Use the tester at the bottom to verify before saving.
            </p>
          </div>
        </div>

        <template v-else>
          <div class="card">
            <div class="card-header">
              {{ creating ? 'New custom rule' : `Edit ${form.id}` }}
            </div>
            <div class="card-body grid grid-cols-3 gap-3">
              <label v-if="creating" class="flex flex-col gap-1 text-xs">
                ID
                <input class="input font-mono text-xs" v-model="form.id"
                       placeholder="auto" />
              </label>
              <label class="flex flex-col gap-1 text-xs">
                Name (kind label)
                <input class="input font-mono text-xs" v-model="form.name"
                       placeholder="customer-id" />
              </label>
              <label class="flex flex-col gap-1 text-xs col-span-2">
                Description
                <input class="input" v-model="form.description"
                       placeholder="What does this match?" />
              </label>
              <label class="flex flex-col gap-1 text-xs col-span-2">
                Pattern (.NET regex)
                <input class="input font-mono text-xs" v-model="form.pattern"
                       placeholder="\bCUST-\d{6,8}\b" />
              </label>
              <label class="flex flex-col gap-1 text-xs">
                Replacement
                <input class="input font-mono text-xs" v-model="form.replacement"
                       placeholder="[redacted-customer-id]" />
              </label>
              <label class="flex items-center gap-2 text-xs">
                <input type="checkbox" v-model="form.flags.caseInsensitive" />
                case-insensitive
              </label>
              <label class="flex items-center gap-2 text-xs">
                <input type="checkbox" v-model="form.flags.multiline" />
                multiline
              </label>
              <label class="flex items-center gap-2 text-xs">
                <input type="checkbox" v-model="form.flags.luhnValidate" />
                Luhn-validate
              </label>
              <label class="flex items-center gap-2 text-xs col-span-3">
                <input type="checkbox" v-model="form.enabled" />
                enabled
              </label>
            </div>
          </div>

          <div class="card">
            <div class="card-header">
              <span>Live preview</span>
              <span class="text-xs text-surface-700">runs your pattern against the sample text</span>
            </div>
            <div class="card-body text-xs">
              <div v-if="editorPreview.error" class="text-red-700 font-mono">
                regex error: {{ editorPreview.error }}
              </div>
              <div v-else-if="editorPreview.matches.length === 0" class="text-surface-700">
                No matches in sample text. Edit the pattern or the test text below.
              </div>
              <div v-else class="flex flex-col gap-1">
                <div v-for="(m, i) in editorPreview.matches" :key="i" class="flex gap-2 items-center">
                  <span class="chip">{{ m.kind }}</span>
                  <code class="bg-yellow-100 px-1 rounded">{{ m.match }}</code>
                  <span class="text-surface-700">→</span>
                  <code class="bg-green-100 px-1 rounded">{{ m.replacement }}</code>
                  <span class="text-surface-700 ml-auto">offset {{ m.start }}</span>
                </div>
              </div>
            </div>
          </div>

          <div class="flex items-center gap-2">
            <button v-if="editing" class="btn btn-danger" @click="removeRule(editing.id)">Delete</button>
            <span class="ml-auto flex items-center gap-2">
              <span v-if="ruleError" class="text-xs text-red-700">{{ ruleError }}</span>
              <button class="btn" @click="editing = null; creating = false">Cancel</button>
              <button class="btn-primary" :disabled="ruleSaving" @click="saveRule">
                {{ ruleSaving ? 'Saving…' : creating ? 'Create' : 'Save' }}
              </button>
            </span>
          </div>
        </template>
      </div>
    </div>

    <!-- ===== tester ===== -->
    <div class="card">
      <div class="card-header">
        <span>Tester</span>
        <span class="text-xs text-surface-700">
          Run the selected packs and rules against sample text. Persists nothing.
          Pack and rule selection above feed in as checkboxes.
        </span>
      </div>
      <div class="card-body grid grid-cols-2 gap-3">
        <label class="flex flex-col gap-1 text-xs">
          Sample text
          <textarea class="input font-mono text-xs" rows="6" v-model="testState.text"></textarea>
        </label>
        <div class="flex flex-col gap-2">
          <div class="text-xs text-surface-700">
            {{ testState.selectedPacks.size }} pack(s),
            {{ testState.selectedRules.size }} rule(s),
            {{ testState.disabledDetectors.size }} detector(s) disabled
          </div>
          <button class="btn-primary" :disabled="testRunning" @click="runTest">
            <span class="i-carbon-play" /> {{ testRunning ? 'Running…' : 'Run tester' }}
          </button>
          <div v-if="testResult" class="border border-surface-200 rounded-md p-3 flex flex-col gap-2">
            <div class="text-xs text-surface-700">Redacted output</div>
            <pre class="text-xs font-mono whitespace-pre-wrap m-0">{{ testResult.redactedText }}</pre>
            <div v-if="testResult.errors.length" class="text-xs text-red-700">
              <div class="font-500">Errors:</div>
              <div v-for="(e, i) in testResult.errors" :key="i" class="font-mono">{{ e }}</div>
            </div>
            <div v-if="testResult.matches.length" class="text-xs">
              <div class="font-500 mb-1">{{ testResult.matches.length }} match(es)</div>
              <div class="flex flex-col gap-1">
                <div v-for="(m, i) in testResult.matches" :key="i" class="flex gap-2 items-center">
                  <span class="chip">{{ m.kind }}</span>
                  <code class="bg-yellow-100 px-1 rounded">{{ m.match }}</code>
                  <span class="text-surface-700">→</span>
                  <code class="bg-green-100 px-1 rounded">{{ m.replacement }}</code>
                  <span class="text-surface-700 ml-auto">offset {{ m.start }}</span>
                </div>
              </div>
            </div>
          </div>
        </div>
      </div>
    </div>
  </div>
</template>
