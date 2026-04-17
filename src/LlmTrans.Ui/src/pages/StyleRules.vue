<script setup lang="ts">
import { onMounted, ref } from 'vue';
import { RouterLink } from 'vue-router';
import { api } from '../api';
import type { StyleRule } from '../types';

const rules = ref<StyleRule[]>([]);
const error = ref<string | null>(null);
const showCreate = ref(false);
const form = ref({
  id: '',
  tenantId: 't_dev',
  name: '',
  language: 'de',
  deeplStyleId: '',
  rulesJson: '{}',
});

async function refresh() {
  try { rules.value = await api.styleRules.list(); }
  catch (e: unknown) { error.value = e instanceof Error ? e.message : String(e); }
}

async function create() {
  if (!form.value.id || !form.value.name) return;
  try {
    await api.styleRules.create({
      id: form.value.id,
      tenantId: form.value.tenantId,
      name: form.value.name,
      language: form.value.language,
      deeplStyleId: form.value.deeplStyleId || null,
      rulesJson: form.value.rulesJson,
    });
    showCreate.value = false;
    form.value.id = '';
    form.value.name = '';
    await refresh();
  } catch (e: unknown) { error.value = e instanceof Error ? e.message : String(e); }
}

onMounted(refresh);
</script>

<template>
  <div class="flex flex-col gap-4">
    <div class="flex items-center justify-between">
      <div class="text-sm text-surface-700">
        {{ rules.length }} style rule{{ rules.length === 1 ? '' : 's' }}
      </div>
      <button class="btn-primary" @click="showCreate = !showCreate">
        <span class="i-carbon-add" /> New style rule
      </button>
    </div>

    <div v-if="showCreate" class="card">
      <div class="card-header">New style rule</div>
      <form class="card-body grid grid-cols-4 gap-3" @submit.prevent="create">
        <label class="flex flex-col gap-1 text-xs">
          ID
          <input class="input" v-model="form.id" required />
        </label>
        <label class="flex flex-col gap-1 text-xs">
          Tenant ID
          <input class="input" v-model="form.tenantId" required />
        </label>
        <label class="flex flex-col gap-1 text-xs col-span-2">
          Name
          <input class="input" v-model="form.name" placeholder="Business formal" required />
        </label>
        <label class="flex flex-col gap-1 text-xs">
          Language
          <input class="input" v-model="form.language" />
        </label>
        <label class="flex flex-col gap-1 text-xs col-span-3">
          DeepL style id (optional)
          <input class="input" v-model="form.deeplStyleId" />
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
          <tr><th>ID</th><th>Name</th><th>Language</th><th>Version</th><th /></tr>
        </thead>
        <tbody>
          <tr v-for="r in rules" :key="r.id">
            <td class="font-mono text-xs">
              <RouterLink :to="`/style-rules/${r.id}`" class="text-brand-600 hover:underline">
                {{ r.id }}
              </RouterLink>
            </td>
            <td>{{ r.name }}</td>
            <td><span class="chip">{{ r.language }}</span></td>
            <td class="text-xs">v{{ r.version }}</td>
            <td class="text-right">
              <RouterLink :to="`/style-rules/${r.id}`" class="btn">Edit instructions</RouterLink>
            </td>
          </tr>
          <tr v-if="rules.length === 0">
            <td colspan="5" class="text-center py-6 text-surface-700">No style rules yet.</td>
          </tr>
        </tbody>
      </table>
    </div>

    <div v-if="error" class="text-xs text-red-700">{{ error }}</div>
  </div>
</template>
