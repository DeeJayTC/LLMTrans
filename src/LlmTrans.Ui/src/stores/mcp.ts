import { defineStore } from 'pinia';
import { ref } from 'vue';
import { api } from '../api';
import type { McpCatalogEntry, McpServer } from '../types';

export const useMcpStore = defineStore('mcp', () => {
  const servers = ref<McpServer[]>([]);
  const catalog = ref<McpCatalogEntry[]>([]);
  const loading = ref(false);
  const error = ref<string | null>(null);

  async function refresh() {
    loading.value = true;
    error.value = null;
    try {
      [servers.value, catalog.value] = await Promise.all([
        api.mcp.listServers(),
        api.mcp.listCatalog(),
      ]);
    } catch (e: unknown) {
      error.value = e instanceof Error ? e.message : String(e);
    } finally {
      loading.value = false;
    }
  }

  return { servers, catalog, loading, error, refresh };
});
