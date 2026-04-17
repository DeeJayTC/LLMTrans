<script setup lang="ts">
import { onMounted, ref } from 'vue';
import { RouterLink } from 'vue-router';
import { api } from '../api';
import type { Glossary } from '../types';

const glossaries = ref<Glossary[]>([]);
const loading = ref(false);
const error = ref<string | null>(null);
const showCreate = ref(false);
const form = ref({ id: '', tenantId: 't_dev', name: '', deeplGlossaryId: '' });

async function refresh() {
  loading.value = true;
  try { glossaries.value = await api.glossaries.list(); }
  catch (e: unknown) { error.value = e instanceof Error ? e.message : String(e); }
  finally { loading.value = false; }
}

async function create() {
  if (!form.value.id || !form.value.name) return;
  try {
    await api.glossaries.create({
      id: form.value.id,
      tenantId: form.value.tenantId,
      name: form.value.name,
      deeplGlossaryId: form.value.deeplGlossaryId || null,
    });
    form.value = { id: '', tenantId: 't_dev', name: '', deeplGlossaryId: '' };
    showCreate.value = false;
    await refresh();
  } catch (e: unknown) { error.value = e instanceof Error ? e.message : String(e); }
}

async function remove(id: string) {
  if (!confirm(`Delete glossary ${id}?`)) return;
  await api.glossaries.delete(id);
  await refresh();
}

onMounted(refresh);
</script>

<template>
  <div class="flex flex-col gap-4">
    <div class="flex items-center justify-between">
      <div class="text-sm text-surface-700">
        {{ glossaries.length }} glossar{{ glossaries.length === 1 ? 'y' : 'ies' }}
      </div>
      <button class="btn-primary" @click="showCreate = !showCreate">
        <span class="i-carbon-add" /> New glossary
      </button>
    </div>

    <div v-if="showCreate" class="card">
      <div class="card-header">New glossary</div>
      <form class="card-body grid grid-cols-4 gap-3" @submit.prevent="create">
        <label class="flex flex-col gap-1 text-xs">
          ID
          <input class="input" v-model="form.id" placeholder="gl_products" required />
        </label>
        <label class="flex flex-col gap-1 text-xs">
          Tenant ID
          <input class="input" v-model="form.tenantId" required />
        </label>
        <label class="flex flex-col gap-1 text-xs">
          Name
          <input class="input" v-model="form.name" placeholder="Product terms" required />
        </label>
        <label class="flex flex-col gap-1 text-xs">
          DeepL glossary id (optional)
          <input class="input" v-model="form.deeplGlossaryId" />
        </label>
        <div class="col-span-4 text-right">
          <button type="button" class="btn mr-2" @click="showCreate = false">Cancel</button>
          <button type="submit" class="btn-primary">Create</button>
        </div>
      </form>
    </div>

    <div class="card">
      <table>
        <thead>
          <tr><th>ID</th><th>Name</th><th>DeepL glossary</th><th>Updated</th><th /></tr>
        </thead>
        <tbody>
          <tr v-for="g in glossaries" :key="g.id">
            <td class="font-mono text-xs">
              <RouterLink :to="`/glossaries/${g.id}`" class="text-brand-600 hover:underline">
                {{ g.id }}
              </RouterLink>
            </td>
            <td>{{ g.name }}</td>
            <td class="font-mono text-xs">{{ g.deeplGlossaryId ?? '—' }}</td>
            <td class="text-xs">{{ new Date(g.updatedAt).toLocaleString() }}</td>
            <td class="text-right whitespace-nowrap">
              <RouterLink :to="`/glossaries/${g.id}`" class="btn">Edit entries</RouterLink>
              <button class="btn btn-danger ml-2" @click="remove(g.id)">Delete</button>
            </td>
          </tr>
          <tr v-if="glossaries.length === 0 && !loading">
            <td colspan="5" class="text-center py-6 text-surface-700">No glossaries yet.</td>
          </tr>
        </tbody>
      </table>
    </div>

    <div v-if="error" class="text-xs text-red-700">{{ error }}</div>
  </div>
</template>
