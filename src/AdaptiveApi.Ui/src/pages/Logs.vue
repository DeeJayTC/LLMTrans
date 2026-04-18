<script setup lang="ts">
import { onMounted, ref } from 'vue';
import { api, type AuditEvent } from '../api';

const events = ref<AuditEvent[]>([]);
const nextBefore = ref(0);
const loading = ref(false);
const error = ref<string | null>(null);
const selected = ref<AuditEvent | null>(null);

async function refresh() {
  loading.value = true;
  error.value = null;
  try {
    const page = await api.logs.list({ limit: 50 });
    events.value = page.items;
    nextBefore.value = page.nextBefore;
  } catch (e: unknown) {
    error.value = e instanceof Error ? e.message : String(e);
  } finally {
    loading.value = false;
  }
}

async function loadMore() {
  if (!nextBefore.value) return;
  loading.value = true;
  try {
    const page = await api.logs.list({ before: nextBefore.value, limit: 50 });
    events.value.push(...page.items);
    nextBefore.value = page.nextBefore;
  } finally {
    loading.value = false;
  }
}

function statusClass(s: number): string {
  if (s >= 500) return 'text-red-700 font-500';
  if (s >= 400) return 'text-orange-700 font-500';
  if (s >= 300) return 'text-surface-700';
  return 'text-green-700';
}

onMounted(refresh);
</script>

<template>
  <div class="grid grid-cols-[1fr_400px] gap-4" style="height: calc(100vh - 200px)">
    <div class="card overflow-hidden flex flex-col">
      <div class="card-header">
        <span>Audit events</span>
        <button class="btn" @click="refresh" :disabled="loading">Refresh</button>
      </div>
      <div class="flex-1 overflow-auto">
        <table>
          <thead>
            <tr>
              <th>Time</th>
              <th>Status</th>
              <th>Method</th>
              <th>Path</th>
              <th>Tenant</th>
              <th>Langs</th>
              <th class="text-right">Chars</th>
              <th class="text-right">ms</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="e in events" :key="e.id"
                class="cursor-pointer"
                :class="selected?.id === e.id ? 'bg-brand-50' : ''"
                @click="selected = e">
              <td class="text-xs font-mono whitespace-nowrap">
                {{ new Date(e.createdAt).toLocaleString() }}
              </td>
              <td :class="statusClass(e.status)">{{ e.status }}</td>
              <td class="font-mono text-xs">{{ e.method }}</td>
              <td class="font-mono text-xs truncate max-w-xs">{{ e.path }}</td>
              <td class="text-xs">{{ e.tenantId }}</td>
              <td class="text-xs">{{ e.userLanguage }} → {{ e.llmLanguage }}</td>
              <td class="text-right text-xs font-mono">{{ e.requestChars + e.responseChars }}</td>
              <td class="text-right text-xs font-mono">{{ e.durationMs }}</td>
            </tr>
            <tr v-if="events.length === 0 && !loading">
              <td colspan="8" class="text-center py-6 text-surface-700">No audit events yet.</td>
            </tr>
          </tbody>
        </table>
      </div>
      <div class="border-t border-surface-200 px-4 py-2 flex items-center justify-between text-xs text-surface-700">
        <span>{{ events.length }} event{{ events.length === 1 ? '' : 's' }}</span>
        <button class="btn" :disabled="!nextBefore || loading" @click="loadMore">Load older</button>
      </div>
    </div>

    <div class="card overflow-auto">
      <div class="card-header">Details</div>
      <div class="card-body">
        <div v-if="!selected" class="text-sm text-surface-700">
          Click an event to see its full breakdown.
        </div>
        <dl v-else class="grid grid-cols-[130px_1fr] gap-x-3 gap-y-2 text-sm">
          <dt class="text-surface-700">ID</dt>
          <dd class="font-mono text-xs">{{ selected.id }}</dd>
          <dt class="text-surface-700">Created</dt>
          <dd class="font-mono text-xs">{{ new Date(selected.createdAt).toISOString() }}</dd>
          <dt class="text-surface-700">Status</dt>
          <dd :class="statusClass(selected.status)">{{ selected.status }}</dd>
          <dt class="text-surface-700">Path</dt>
          <dd class="font-mono text-xs break-all">{{ selected.method }} {{ selected.path }}</dd>
          <dt class="text-surface-700">Tenant</dt>
          <dd>{{ selected.tenantId }}</dd>
          <dt class="text-surface-700">Route</dt>
          <dd class="font-mono text-xs">{{ selected.routeId ?? '—' }}</dd>
          <dt class="text-surface-700">Direction</dt>
          <dd>
            <span class="chip">{{ selected.direction }}</span>
            <span class="ml-1 text-xs">{{ selected.userLanguage }} ↔ {{ selected.llmLanguage }}</span>
          </dd>
          <dt class="text-surface-700">Translator</dt>
          <dd>{{ selected.translatorId ?? '—' }}</dd>
          <dt class="text-surface-700">Glossary</dt>
          <dd class="font-mono text-xs">{{ selected.glossaryId ?? '—' }}</dd>
          <dt class="text-surface-700">Style rule</dt>
          <dd class="font-mono text-xs">{{ selected.styleRuleId ?? '—' }}</dd>
          <dt class="text-surface-700">Request chars</dt>
          <dd class="font-mono text-xs">{{ selected.requestChars }}</dd>
          <dt class="text-surface-700">Response chars</dt>
          <dd class="font-mono text-xs">{{ selected.responseChars }}</dd>
          <dt class="text-surface-700">Integrity fails</dt>
          <dd class="font-mono text-xs" :class="selected.integrityFailures > 0 ? 'text-red-700' : ''">
            {{ selected.integrityFailures }}
          </dd>
          <dt class="text-surface-700">Duration</dt>
          <dd class="font-mono text-xs">{{ selected.durationMs }} ms</dd>
        </dl>
      </div>
    </div>
  </div>
  <div v-if="error" class="text-red-700 text-xs mt-2">{{ error }}</div>
</template>
