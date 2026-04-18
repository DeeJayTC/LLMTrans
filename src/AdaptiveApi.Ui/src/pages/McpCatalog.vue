<script setup lang="ts">
import { onMounted } from 'vue';
import { useMcpStore } from '../stores/mcp';

const store = useMcpStore();
onMounted(() => store.refresh());
</script>

<template>
  <div class="flex flex-col gap-4">
    <p class="text-sm text-surface-700">
      Curated MCP servers you can register in one click. Picking an entry pre-fills the catalog slug
      — your own credentials stay on your machine (for stdio) or in your MCP client's <code>headers</code>
      (for remote).
    </p>

    <div class="grid grid-cols-3 gap-3">
      <div v-for="e in store.catalog" :key="e.id" class="card hover:border-brand-500 cursor-pointer">
        <div class="card-body flex flex-col gap-2">
          <div class="flex items-center justify-between">
            <div class="flex items-center gap-2">
              <span class="font-500">{{ e.displayName }}</span>
              <span v-if="e.verified" class="chip-primary">verified</span>
            </div>
            <span class="chip">{{ e.transport }}</span>
          </div>
          <div class="text-xs text-surface-700 line-clamp-3">{{ e.description }}</div>
          <div v-if="e.upstreamUrl" class="text-xs font-mono text-surface-700 truncate">
            {{ e.upstreamUrl }}
          </div>
          <div v-else-if="e.upstreamCommandHint" class="text-xs font-mono text-surface-700 truncate">
            {{ e.upstreamCommandHint }}
          </div>
          <div class="flex items-center justify-between mt-1">
            <span class="text-xs text-surface-700">{{ e.publisher }}</span>
            <a v-if="e.docsUrl" :href="e.docsUrl" target="_blank" class="text-xs text-brand-600 hover:underline">
              docs →
            </a>
          </div>
        </div>
      </div>
      <div v-if="store.catalog.length === 0" class="col-span-3 text-center py-8 text-surface-700">
        No catalog entries loaded. Make sure <code>catalog/mcp-servers.json</code> is reachable at API startup.
      </div>
    </div>
  </div>
</template>
