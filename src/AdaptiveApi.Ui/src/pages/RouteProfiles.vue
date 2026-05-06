<script setup lang="ts">
import { onMounted, ref } from 'vue';
import { api, friendlyError } from '../api';
import type { RouteProfile } from '../api';
import type { Glossary, ProxyRule, StyleRule } from '../types';

const profiles = ref<RouteProfile[]>([]);
const glossaries = ref<Glossary[]>([]);
const styleRules = ref<StyleRule[]>([]);
const proxyRules = ref<ProxyRule[]>([]);

const editing = ref<RouteProfile | null>(null);
const creating = ref(false);
const error = ref<string | null>(null);
const saving = ref(false);

interface Form {
  id: string;
  tenantId: string;
  name: string;
  description: string;
  glossaryId: string;
  requestStyleRuleId: string;
  responseStyleRuleId: string;
  proxyRuleId: string;
}

const form = ref<Form>(blank());

function blank(): Form {
  return {
    id: '',
    tenantId: 't_dev',
    name: '',
    description: '',
    glossaryId: '',
    requestStyleRuleId: '',
    responseStyleRuleId: '',
    proxyRuleId: '',
  };
}

async function refresh() {
  error.value = null;
  try {
    const [p, g, s, pr] = await Promise.all([
      api.routeProfiles.list(),
      api.glossaries.list(),
      api.styleRules.list(),
      api.proxyRules.list(),
    ]);
    profiles.value = p;
    glossaries.value = g;
    styleRules.value = s;
    proxyRules.value = pr;
  } catch (e: unknown) {
    error.value = friendlyError(e);
  }
}

function startNew() {
  form.value = blank();
  creating.value = true;
  editing.value = null;
}

function startEdit(p: RouteProfile) {
  form.value = {
    id: p.id,
    tenantId: p.tenantId,
    name: p.name,
    description: p.description ?? '',
    glossaryId: p.glossaryId ?? '',
    requestStyleRuleId: p.requestStyleRuleId ?? '',
    responseStyleRuleId: p.responseStyleRuleId ?? '',
    proxyRuleId: p.proxyRuleId ?? '',
  };
  editing.value = p;
  creating.value = false;
}

function cancel() {
  editing.value = null;
  creating.value = false;
  error.value = null;
}

async function save() {
  saving.value = true;
  error.value = null;
  try {
    const payload = {
      name: form.value.name,
      description: form.value.description || null,
      glossaryId: form.value.glossaryId || null,
      requestStyleRuleId: form.value.requestStyleRuleId || null,
      responseStyleRuleId: form.value.responseStyleRuleId || null,
      proxyRuleId: form.value.proxyRuleId || null,
    };
    if (creating.value) {
      await api.routeProfiles.create({
        ...payload,
        id: form.value.id,
        tenantId: form.value.tenantId,
      });
    } else if (editing.value) {
      await api.routeProfiles.update(editing.value.id, payload);
    }
    await refresh();
    cancel();
  } catch (e: unknown) {
    error.value = friendlyError(e);
  } finally {
    saving.value = false;
  }
}

async function remove(id: string) {
  if (!confirm(`Delete profile ${id}? Routes using it will keep their per-route bindings.`)) return;
  try {
    await api.routeProfiles.delete(id);
    await refresh();
  } catch (e: unknown) {
    error.value = friendlyError(e);
  }
}

onMounted(refresh);
</script>

<template>
  <div class="flex flex-col gap-4">
    <div class="card">
      <div class="card-header">
        <span>Route profiles</span>
        <span class="text-xs text-surface-700 flex-1">
          A profile bundles the glossary + style + proxy bindings a route
          typically needs into one named row, so the routes form picks one
          profile instead of four FKs. Per-route bindings still override
          the profile when set.
        </span>
        <button class="btn-primary" @click="startNew">
          <span class="i-carbon-add" /> New profile
        </button>
      </div>
      <div v-if="error" class="card-body text-red-700 text-sm">{{ error }}</div>
    </div>

    <div v-if="creating || editing" class="card">
      <div class="card-header">
        {{ creating ? 'New profile' : `Edit ${editing!.id}` }}
      </div>
      <form class="card-body grid grid-cols-2 gap-3" @submit.prevent="save">
        <label class="flex flex-col gap-1 text-xs">
          ID
          <input class="input font-mono text-xs" v-model="form.id" required
                 :disabled="!!editing" placeholder="profile_support_de" />
        </label>
        <label class="flex flex-col gap-1 text-xs">
          Tenant ID
          <input class="input" v-model="form.tenantId" required :disabled="!!editing" />
        </label>
        <label class="flex flex-col gap-1 text-xs col-span-2">
          Name
          <input class="input" v-model="form.name" required placeholder="Customer support — German" />
        </label>
        <label class="flex flex-col gap-1 text-xs col-span-2">
          Description
          <input class="input" v-model="form.description" placeholder="What does this bundle apply?" />
        </label>

        <label class="flex flex-col gap-1 text-xs">
          Glossary
          <select class="input" v-model="form.glossaryId">
            <option value="">— none —</option>
            <option v-for="g in glossaries" :key="g.id" :value="g.id">{{ g.id }} · {{ g.name }}</option>
          </select>
        </label>
        <label class="flex flex-col gap-1 text-xs">
          Proxy rule
          <select class="input" v-model="form.proxyRuleId">
            <option value="">— none —</option>
            <option v-for="p in proxyRules" :key="p.id" :value="p.id">{{ p.id }} · {{ p.name }}</option>
          </select>
        </label>

        <label class="flex flex-col gap-1 text-xs">
          Request style (user → LLM)
          <select class="input" v-model="form.requestStyleRuleId">
            <option value="">— none —</option>
            <option v-for="s in styleRules" :key="s.id" :value="s.id">
              {{ s.id }} · {{ s.name }} ({{ s.language }})
            </option>
          </select>
        </label>
        <label class="flex flex-col gap-1 text-xs">
          Response style (LLM → user)
          <select class="input" v-model="form.responseStyleRuleId">
            <option value="">— none —</option>
            <option v-for="s in styleRules" :key="s.id" :value="s.id">
              {{ s.id }} · {{ s.name }} ({{ s.language }})
            </option>
          </select>
        </label>

        <div class="col-span-2 flex items-center gap-2 justify-end">
          <button type="button" class="btn" @click="cancel">Cancel</button>
          <button type="submit" class="btn-primary" :disabled="saving">
            {{ saving ? 'Saving…' : creating ? 'Create' : 'Save' }}
          </button>
        </div>
      </form>
    </div>

    <div v-if="profiles.length === 0" class="card">
      <div class="card-body text-center py-10 flex flex-col items-center gap-3">
        <div class="i-carbon-folder-details text-4xl text-surface-400" />
        <div class="text-base font-500">No profiles yet</div>
        <div class="text-sm text-surface-700 max-w-md">
          Profiles are optional — routes still work without one. Create a
          profile when you want to reuse the same glossary / style / proxy
          combo across several routes.
        </div>
        <button class="btn-primary mt-2" @click="startNew">
          <span class="i-carbon-add" /> New profile
        </button>
      </div>
    </div>

    <div v-else class="card">
      <table>
        <thead>
          <tr>
            <th>ID</th>
            <th>Name</th>
            <th>Glossary</th>
            <th>Request style</th>
            <th>Response style</th>
            <th>Proxy rule</th>
            <th />
          </tr>
        </thead>
        <tbody>
          <tr v-for="p in profiles" :key="p.id">
            <td class="font-mono text-xs">{{ p.id }}</td>
            <td>{{ p.name }}</td>
            <td class="text-xs">{{ p.glossaryId ?? '—' }}</td>
            <td class="text-xs">{{ p.requestStyleRuleId ?? '—' }}</td>
            <td class="text-xs">{{ p.responseStyleRuleId ?? '—' }}</td>
            <td class="text-xs">{{ p.proxyRuleId ?? '—' }}</td>
            <td class="text-right whitespace-nowrap">
              <button class="btn" @click="startEdit(p)">Edit</button>
              <button class="btn btn-danger ml-2" @click="remove(p.id)">Delete</button>
            </td>
          </tr>
        </tbody>
      </table>
    </div>
  </div>
</template>
