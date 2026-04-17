<script setup lang="ts">
import { onMounted, ref } from 'vue';
import { useMcpStore } from '../stores/mcp';
import { api } from '../api';

const store = useMcpStore();
onMounted(() => store.refresh());

const showCreate = ref(false);
const form = ref({
  id: '',
  tenantId: 't_dev',
  name: '',
  transport: 'remote' as 'remote' | 'stdio-local',
  remoteUpstreamUrl: '',
  userLanguage: 'de',
  llmLanguage: 'en',
  translatorId: '' as string,
  catalogEntryId: '' as string,
});
const issuedToken = ref<{ serverId: string; token: string } | null>(null);
const formError = ref<string | null>(null);
const snippet = ref<{ id: string; client: string; snippet: string } | null>(null);

async function create() {
  formError.value = null;
  try {
    const payload: Record<string, unknown> = { ...form.value };
    if (!form.value.translatorId) payload.translatorId = null;
    if (!form.value.catalogEntryId) payload.catalogEntryId = null;
    if (form.value.transport === 'stdio-local') payload.remoteUpstreamUrl = null;
    const res = await api.mcp.createServer(payload);
    issuedToken.value = { serverId: res.server.id, token: res.routeToken };
    await store.refresh();
    showCreate.value = false;
  } catch (e: unknown) {
    formError.value = e instanceof Error ? e.message : String(e);
  }
}

async function openSnippet(id: string, client: string) {
  const res = await api.mcp.snippet(id, client);
  snippet.value = { id, ...res };
}

async function remove(id: string) {
  if (!confirm(`Disable MCP server ${id}?`)) return;
  await api.mcp.deleteServer(id);
  await store.refresh();
}
</script>

<template>
  <div class="flex flex-col gap-4">
    <div class="flex items-center justify-between">
      <div class="text-sm text-surface-700">
        {{ store.servers.length }} server{{ store.servers.length === 1 ? '' : 's' }}
      </div>
      <button class="btn-primary" @click="showCreate = !showCreate">
        <span class="i-carbon-add" /> Add server
      </button>
    </div>

    <div v-if="showCreate" class="card">
      <div class="card-header">New MCP server</div>
      <form class="card-body grid grid-cols-2 gap-3" @submit.prevent="create">
        <label class="flex flex-col gap-1 text-xs">
          ID
          <input class="input" v-model="form.id" required placeholder="mcp_linear_de" />
        </label>
        <label class="flex flex-col gap-1 text-xs">
          Tenant ID
          <input class="input" v-model="form.tenantId" required />
        </label>
        <label class="flex flex-col gap-1 text-xs col-span-2">
          Name
          <input class="input" v-model="form.name" required />
        </label>
        <label class="flex flex-col gap-1 text-xs">
          Transport
          <select class="input" v-model="form.transport">
            <option value="remote">Remote (Flow A)</option>
            <option value="stdio-local">Stdio-local (bridged)</option>
          </select>
        </label>
        <label class="flex flex-col gap-1 text-xs" v-if="form.transport === 'remote'">
          Upstream URL
          <input class="input" v-model="form.remoteUpstreamUrl" placeholder="https://mcp.linear.app/" />
        </label>
        <label v-else class="flex flex-col gap-1 text-xs">
          <span class="text-surface-700">stdio-local — your upstream command stays on your machine; only message bodies flow through llmtrans.</span>
        </label>

        <label class="flex flex-col gap-1 text-xs">
          User language
          <input class="input" v-model="form.userLanguage" />
        </label>
        <label class="flex flex-col gap-1 text-xs">
          LLM / server language
          <input class="input" v-model="form.llmLanguage" />
        </label>
        <label class="flex flex-col gap-1 text-xs">
          Translator ID (optional)
          <input class="input" v-model="form.translatorId" placeholder="deepl / llm" />
        </label>
        <label class="flex flex-col gap-1 text-xs">
          Catalog entry ID (optional)
          <input class="input" v-model="form.catalogEntryId" placeholder="from catalog" />
        </label>

        <div class="col-span-2 flex items-center gap-2 justify-end">
          <span v-if="formError" class="text-xs text-red-700 mr-auto">{{ formError }}</span>
          <button type="button" class="btn" @click="showCreate = false">Cancel</button>
          <button type="submit" class="btn-primary">Create</button>
        </div>
      </form>
    </div>

    <div v-if="issuedToken" class="card border-brand-500 border">
      <div class="card-body flex flex-col gap-2">
        <div class="font-500">Route token for <span class="chip">{{ issuedToken.serverId }}</span></div>
        <pre class="select-all">{{ issuedToken.token }}</pre>
        <div class="text-xs text-surface-700">Copy this now — shown only once. Use it in the MCP client config snippet.</div>
        <button class="btn self-start" @click="issuedToken = null">Dismiss</button>
      </div>
    </div>

    <div v-if="snippet" class="card">
      <div class="card-header">
        Snippet for <span class="chip">{{ snippet.id }}</span> ({{ snippet.client }})
        <button class="btn" @click="snippet = null">Close</button>
      </div>
      <div class="card-body flex flex-col gap-2">
        <div class="flex gap-1">
          <button class="btn" v-for="c in ['claude-desktop','cursor','zed','vscode','continue','raw']" :key="c"
                  @click="openSnippet(snippet!.id, c)">
            {{ c }}
          </button>
        </div>
        <pre>{{ snippet.snippet }}</pre>
      </div>
    </div>

    <div class="card">
      <table>
        <thead>
          <tr>
            <th>ID</th>
            <th>Name</th>
            <th>Transport</th>
            <th>Upstream</th>
            <th>User → LLM</th>
            <th />
          </tr>
        </thead>
        <tbody>
          <tr v-for="s in store.servers" :key="s.id">
            <td class="font-mono text-xs">{{ s.id }}</td>
            <td>{{ s.name }}</td>
            <td>
              <span class="chip" :class="s.transport === 'remote' ? 'chip-primary' : ''">
                {{ s.transport }}
              </span>
            </td>
            <td class="font-mono text-xs">{{ s.remoteUpstreamUrl ?? '—' }}</td>
            <td>{{ s.userLanguage }} → {{ s.llmLanguage }}</td>
            <td class="text-right whitespace-nowrap">
              <button class="btn" @click="openSnippet(s.id, 'claude-desktop')">Snippet</button>
              <button class="btn btn-danger ml-2" @click="remove(s.id)">Disable</button>
            </td>
          </tr>
          <tr v-if="store.servers.length === 0 && !store.loading">
            <td colspan="6" class="text-surface-700 text-center py-6">No MCP servers yet.</td>
          </tr>
        </tbody>
      </table>
    </div>
  </div>
</template>
