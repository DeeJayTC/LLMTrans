<script setup lang="ts">
import { onMounted, ref } from 'vue';
import { api } from '../api';
import type { Tenant } from '../types';

const tenants = ref<Tenant[]>([]);
const health = ref<string>('unknown');

onMounted(async () => {
  tenants.value = await api.tenants.list();
  const h = await api.health();
  health.value = h.status;
});
</script>

<template>
  <div class="grid grid-cols-2 gap-4">
    <div class="card">
      <div class="card-header">Tenants</div>
      <div class="card-body">
        <table v-if="tenants.length > 0">
          <thead><tr><th>ID</th><th>Name</th><th>Created</th></tr></thead>
          <tbody>
            <tr v-for="t in tenants" :key="t.id">
              <td class="font-mono text-xs">{{ t.id }}</td>
              <td>{{ t.name }}</td>
              <td class="text-xs">{{ new Date(t.createdAt).toLocaleString() }}</td>
            </tr>
          </tbody>
        </table>
        <div v-else class="text-sm text-surface-700">No tenants yet.</div>
      </div>
    </div>

    <div class="card">
      <div class="card-header">System</div>
      <div class="card-body flex flex-col gap-2 text-sm">
        <div>API health: <span class="chip">{{ health }}</span></div>
        <div class="text-xs text-surface-700">
          Translator keys, DeepL server region, KMS integration and SaaS billing move here in M8.
        </div>
      </div>
    </div>
  </div>
</template>
