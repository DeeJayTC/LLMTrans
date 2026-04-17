<script setup lang="ts">
import { onMounted, ref } from 'vue';
import { api } from '../api';

const stats = ref({
  tenants: 0,
  routes: 0,
  mcpServers: 0,
  glossaries: 0,
  styleRules: 0,
});
const status = ref<'healthy' | 'degraded' | 'unknown'>('unknown');
const error = ref<string | null>(null);

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
    error.value = e instanceof Error ? e.message : String(e);
    status.value = 'degraded';
  }
});
</script>

<template>
  <div class="flex flex-col gap-4">
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
