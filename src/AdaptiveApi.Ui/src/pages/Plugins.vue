<script setup lang="ts">
import { onMounted, ref } from 'vue';
import { api, friendlyError } from '../api';
import type { PluginInfo, PluginList } from '../api';

const data = ref<PluginList>({ loaded: [], disabled: [] });
const loading = ref(false);
const error = ref<string | null>(null);

const editing = ref<PluginInfo | null>(null);
const editorJson = ref('');
const editorError = ref<string | null>(null);
const editorSaving = ref(false);

async function refresh() {
  loading.value = true;
  error.value = null;
  try {
    data.value = await api.plugins.list();
  } catch (e: unknown) {
    error.value = friendlyError(e);
  } finally {
    loading.value = false;
  }
}

async function toggle(p: PluginInfo) {
  try {
    if (p.enabled) await api.plugins.disable(p.id);
    else await api.plugins.enable(p.id);
    await refresh();
  } catch (e: unknown) {
    error.value = friendlyError(e);
  }
}

async function openEditor(p: PluginInfo) {
  editing.value = p;
  editorError.value = null;
  try {
    const s = await api.plugins.getSettings(p.id);
    editorJson.value = formatJson(s.settingsJson);
  } catch (e: unknown) {
    editorError.value = friendlyError(e);
    editorJson.value = '{}';
  }
}

function closeEditor() {
  editing.value = null;
  editorJson.value = '';
  editorError.value = null;
}

async function saveSettings() {
  if (!editing.value) return;
  editorSaving.value = true;
  editorError.value = null;
  try {
    // Validate locally first so the user sees the parse error in-place rather
    // than as a 400 round-trip.
    JSON.parse(editorJson.value);
    await api.plugins.updateSettings(editing.value.id, editorJson.value);
    closeEditor();
  } catch (e: unknown) {
    editorError.value = friendlyError(e);
  } finally {
    editorSaving.value = false;
  }
}

function formatJson(s: string): string {
  try { return JSON.stringify(JSON.parse(s), null, 2); }
  catch { return s; }
}

onMounted(refresh);
</script>

<template>
  <div class="flex flex-col gap-4">
    <div class="card">
      <div class="card-header flex items-center gap-3">
        <span>Plugins</span>
        <span class="text-xs text-surface-700">
          Modules discovered at startup. Toggle to enable / disable; click
          <strong>Settings</strong> for plugin-owned JSON configuration.
        </span>
        <button class="btn ml-auto" @click="refresh">
          <span class="i-carbon-renew" /> Refresh
        </button>
      </div>
      <div v-if="error" class="card-body text-red-700 text-sm">{{ error }}</div>
    </div>

    <div v-if="data.loaded.length === 0 && !loading" class="card">
      <div class="card-body text-center py-8 text-sm text-surface-700">
        No plugins loaded. Drop an <code>AdaptiveApi.*.dll</code> next to the
        host or mount it under <code>plugins/</code>.
      </div>
    </div>

    <div v-else class="card">
      <table>
        <thead>
          <tr>
            <th class="w-20">Enabled</th>
            <th>ID</th>
            <th>Version</th>
            <th>Category</th>
            <th>Description</th>
            <th>Tags</th>
            <th />
          </tr>
        </thead>
        <tbody>
          <tr v-for="p in data.loaded" :key="p.id">
            <td>
              <label class="inline-flex items-center gap-2">
                <input type="checkbox" :checked="p.enabled" @change="toggle(p)" />
                <span class="text-xs">{{ p.enabled ? 'on' : 'off' }}</span>
              </label>
            </td>
            <td>
              <div class="font-mono text-xs">{{ p.id }}</div>
              <div class="text-xs text-surface-700">{{ p.name }}</div>
            </td>
            <td class="text-xs">{{ p.version }}</td>
            <td><span class="chip">{{ p.category }}</span></td>
            <td class="text-xs text-surface-700">{{ p.description }}</td>
            <td>
              <span v-if="p.metrics" class="chip text-xs"
                    :title="`avg ${p.metrics.avgMs.toFixed(2)}ms · last ${p.metrics.lastMs.toFixed(2)}ms · ${p.metrics.failures}/${p.metrics.calls} failed`">
                {{ p.metrics.avgMs.toFixed(1) }}ms · {{ p.metrics.calls }} calls
              </span>
              <span v-if="p.metrics && p.metrics.failures > 0"
                    class="chip text-xs bg-red-50 text-red-700"
                    :title="`${p.metrics.failures} hook invocations threw`">
                {{ p.metrics.failures }} fail
              </span>
              <span v-if="p.hasEndpoints" class="chip text-xs" title="exposes endpoints under /plugins/{id}">routes</span>
              <span v-if="p.allowAnonymousEndpoints"
                    class="chip text-xs bg-yellow-100 text-yellow-900"
                    title="anonymous /plugins/{id} routes — bypasses admin auth">
                anon
              </span>
              <span v-for="d in p.dependencies" :key="d" class="chip text-xs"
                    :title="`depends on ${d}`">deps: {{ d }}</span>
            </td>
            <td class="text-right whitespace-nowrap">
              <button v-if="p.hasSettings" class="btn" @click="openEditor(p)">
                <span class="i-carbon-settings" /> Settings
              </button>
            </td>
          </tr>
        </tbody>
      </table>
    </div>

    <div v-if="data.disabled.length > 0" class="card border-yellow-300 border">
      <div class="card-header">
        <span>Disabled at startup</span>
        <span class="text-xs text-surface-700">
          The loader rejected these modules. Fix the underlying issue and
          restart to bring them online.
        </span>
      </div>
      <table>
        <thead>
          <tr>
            <th>Plugin ID</th>
            <th>Version</th>
            <th>Reason</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="d in data.disabled" :key="`${d.pluginId}@${d.version}`">
            <td class="font-mono text-xs">{{ d.pluginId }}</td>
            <td class="text-xs">{{ d.version }}</td>
            <td class="text-xs">{{ d.reason }}</td>
          </tr>
        </tbody>
      </table>
    </div>

    <!-- ===== settings editor ===== -->
    <div v-if="editing" class="card border-brand-300 border">
      <div class="card-header">
        <span>Settings — <code class="font-mono">{{ editing.id }}</code></span>
        <span class="text-xs text-surface-700">
          Opaque JSON; the schema is owned by the plugin. The host validates
          syntax on save.
        </span>
      </div>
      <div class="card-body flex flex-col gap-2">
        <textarea class="input font-mono text-xs" rows="14" v-model="editorJson"></textarea>
        <div v-if="editorError" class="text-red-700 text-xs">{{ editorError }}</div>
        <div class="flex justify-end gap-2">
          <button class="btn" @click="closeEditor">Cancel</button>
          <button class="btn-primary" :disabled="editorSaving" @click="saveSettings">
            {{ editorSaving ? 'Saving…' : 'Save' }}
          </button>
        </div>
      </div>
    </div>
  </div>
</template>
