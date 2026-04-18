import { defineStore } from 'pinia';
import { ref } from 'vue';
import { api } from '../api';
import type { Route } from '../types';

export const useRoutesStore = defineStore('routes', () => {
  const routes = ref<Route[]>([]);
  const loading = ref(false);
  const error = ref<string | null>(null);

  async function refresh() {
    loading.value = true;
    error.value = null;
    try {
      routes.value = await api.routes.list();
    } catch (e: unknown) {
      error.value = e instanceof Error ? e.message : String(e);
    } finally {
      loading.value = false;
    }
  }

  async function remove(id: string) {
    await api.routes.delete(id);
    routes.value = routes.value.filter((r) => r.id !== id);
  }

  return { routes, loading, error, refresh, remove };
});
