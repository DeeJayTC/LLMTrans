<script setup lang="ts">
import { computed, onMounted, onBeforeUnmount, ref } from 'vue';
import { RouterLink } from 'vue-router';
import { api, friendlyError, type AuditEvent } from '../api';

const stats = ref({
  tenants: 0,
  routes: 0,
  mcpServers: 0,
  glossaries: 0,
  styleRules: 0,
});
const status = ref<'healthy' | 'degraded' | 'unknown'>('unknown');
const error = ref<string | null>(null);

const isFreshInstall = computed(() =>
  stats.value.routes === 0 &&
  stats.value.mcpServers === 0 &&
  stats.value.glossaries === 0 &&
  stats.value.styleRules === 0);

// Poll-based live tail of the audit log so the dashboard shows recent
// activity without leaving the page. Cheap (one query every 5s) and stops
// when the page unmounts.
const recent = ref<AuditEvent[]>([]);
const livePaused = ref(false);
let pollTimer: ReturnType<typeof setInterval> | null = null;

async function refreshRecent() {
  if (livePaused.value) return;
  try {
    const r = await api.logs.list({ limit: 10 });
    recent.value = r.items;
  } catch { /* best-effort */ }
}

function statusColor(code: number): string {
  if (code >= 500) return 'text-red-700';
  if (code >= 400) return 'text-yellow-700';
  return 'text-green-700';
}

onMounted(async () => {
  try {
    const [t, r, m, g, s] = await Promise.all([
      api.tenants.list(),
      api.routes.list(),
      api.mcp.listServers(),
      api.glossaries.list(),
      api.styleRules.list(),
    ]);
    stats.value = {
      tenants: t.length,
      routes: r.length,
      mcpServers: m.length,
      glossaries: g.length,
      styleRules: s.length,
    };
    await api.health();
    status.value = 'healthy';
  } catch (e: unknown) {
    error.value = friendlyError(e);
    status.value = 'degraded';
  }

  await refreshRecent();
  pollTimer = setInterval(refreshRecent, 5000);
});

onBeforeUnmount(() => {
  if (pollTimer) clearInterval(pollTimer);
});
</script>

<template>
  <div class="flex flex-col gap-4">
    <div v-if="isFreshInstall && status === 'healthy'" class="card border-brand-300 border">
      <div class="card-body flex items-center gap-4 py-5">
        <div class="i-carbon-rocket text-3xl text-brand-600" />
        <div class="flex-1">
          <div class="font-500 text-base">Welcome to AdaptiveAPI</div>
          <div class="text-sm text-surface-700">
            Nothing's configured yet. Create a route to point your SDK at —
            it issues a token you swap into your client's <code>base_url</code>.
          </div>
        </div>
        <RouterLink to="/wizard" class="btn-primary">
          <span class="i-carbon-rocket" /> Start the setup wizard
        </RouterLink>
      </div>
    </div>

    <div class="grid grid-cols-5 gap-3">
      <div class="card">
        <div class="card-body">
          <div class="text-xs text-surface-700">Tenants</div>
          <div class="mt-1 text-2xl font-600">{{ stats.tenants }}</div>
        </div>
      </div>
      <div class="card">
        <div class="card-body">
          <div class="text-xs text-surface-700">Routes</div>
          <div class="mt-1 text-2xl font-600">{{ stats.routes }}</div>
        </div>
      </div>
      <div class="card">
        <div class="card-body">
          <div class="text-xs text-surface-700">MCP servers</div>
          <div class="mt-1 text-2xl font-600">{{ stats.mcpServers }}</div>
        </div>
      </div>
      <div class="card">
        <div class="card-body">
          <div class="text-xs text-surface-700">Glossaries</div>
          <div class="mt-1 text-2xl font-600">{{ stats.glossaries }}</div>
        </div>
      </div>
      <div class="card">
        <div class="card-body">
          <div class="text-xs text-surface-700">Style rules</div>
          <div class="mt-1 text-2xl font-600">{{ stats.styleRules }}</div>
        </div>
      </div>
    </div>

    <div class="card">
      <div class="card-header">
        <span>Recent activity</span>
        <span class="text-xs text-surface-700">live tail of the audit log (5s poll)</span>
        <button class="btn ml-auto text-xs" @click="livePaused = !livePaused; if (!livePaused) refreshRecent();">
          {{ livePaused ? 'Resume' : 'Pause' }}
        </button>
        <RouterLink to="/logs" class="btn text-xs">Open logs</RouterLink>
      </div>
      <table v-if="recent.length > 0">
        <thead>
          <tr>
            <th>When</th>
            <th>Status</th>
            <th>Route</th>
            <th>Method · Path</th>
            <th>Direction</th>
            <th class="text-right">Duration</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="e in recent" :key="e.id">
            <td class="text-xs whitespace-nowrap">{{ new Date(e.createdAt).toLocaleTimeString() }}</td>
            <td class="text-xs font-mono" :class="statusColor(e.status)">{{ e.status }}</td>
            <td class="font-mono text-xs">{{ e.routeId ?? '—' }}</td>
            <td class="text-xs"><span class="chip">{{ e.method }}</span> {{ e.path }}</td>
            <td class="text-xs">{{ e.userLanguage }} → {{ e.llmLanguage }}</td>
            <td class="text-xs text-right">{{ e.durationMs }}ms</td>
          </tr>
        </tbody>
      </table>
      <div v-else class="card-body text-center py-6 text-sm text-surface-700">
        No recent activity. Make a request through the proxy and it'll show up here.
      </div>
    </div>

    <div class="card">
      <div class="card-header">System status</div>
      <div class="card-body flex items-center gap-3">
        <span
          class="inline-block h-2 w-2 rounded-full"
          :class="status === 'healthy' ? 'bg-green-500' : status === 'degraded' ? 'bg-red-500' : 'bg-surface-300'"
        />
        <span class="font-500 capitalize">{{ status }}</span>
        <span v-if="error" class="text-xs text-red-700">{{ error }}</span>
      </div>
    </div>
  </div>
</template>
