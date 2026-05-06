<script setup lang="ts">
import { onMounted, ref } from 'vue';
import { useMcpStore } from '../stores/mcp';
import { api, friendlyError } from '../api';

const store = useMcpStore();
onMounted(() => store.refresh());

// MCP Servers + Catalog used to be separate pages — they're the same
// concept (MCP route configuration), just different starting points: the
// catalog is curated entries you can register in one click, the servers
// list is the active set. One page, two tabs.
const tab = ref<'servers' | 'catalog'>('servers');

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
    formError.value = friendlyError(e);
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

/// Click-to-prefill from catalog: jumps to the Servers tab with the form
/// open, hydrated from the selected catalog entry. The user just adjusts
/// languages / id and clicks Create.
function quickAddFromCatalog(e: typeof store.catalog[number]) {
  form.value = {
    id: '',
    tenantId: 't_dev',
    name: e.displayName,
    transport: e.transport === 'stdio-local' ? 'stdio-local' : 'remote',
    remoteUpstreamUrl: e.upstreamUrl ?? '',
    userLanguage: 'de',
    llmLanguage: 'en',
    translatorId: '',
    catalogEntryId: e.id,
  };
  showCreate.value = true;
  tab.value = 'servers';
}
</script>

<template>
  <div class="flex flex-col gap-4">
    <div class="flex items-center gap-2 border-b border-surface-200">
      <button class="px-4 py-2 text-sm border-b-2 -mb-px"
              :class="tab === 'servers' ? 'border-brand-500 text-brand-700 font-500' : 'border-transparent text-surface-700 hover:text-surface-900'"
              @click="tab = 'servers'">
        Servers ({{ store.servers.length }})
      </button>
      <button class="px-4 py-2 text-sm border-b-2 -mb-px"
              :class="tab === 'catalog' ? 'border-brand-500 text-brand-700 font-500' : 'border-transparent text-surface-700 hover:text-surface-900'"
              @click="tab = 'catalog'">
        Catalog ({{ store.catalog.length }})
      </button>
      <div class="ml-auto">
        <button v-if="tab === 'servers'" class="btn-primary" @click="showCreate = !showCreate">
          <span class="i-carbon-add" /> Add server
        </button>
      </div>
    </div>

    <!-- ===== Servers tab ===== -->
    <template v-if="tab === 'servers'">
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
            <span class="text-surface-700">stdio-local — your upstream command stays on your machine; only message bodies flow through adaptiveapi.</span>
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

      <div v-if="store.servers.length === 0 && !store.loading && !showCreate" class="card">
        <div class="card-body text-center py-8 flex flex-col items-center gap-3">
          <div class="i-carbon-plug text-3xl text-surface-400" />
          <div class="text-sm text-surface-700 max-w-md">
            No MCP servers configured yet. Pick one from the
            <button class="text-brand-600 underline" @click="tab = 'catalog'">catalog tab</button>
            to one-click-register, or click <strong>Add server</strong> to wire a custom one.
          </div>
        </div>
      </div>

      <div v-else class="card">
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
          </tbody>
        </table>
      </div>
    </template>

    <!-- ===== Catalog tab ===== -->
    <template v-else>
      <p class="text-sm text-surface-700">
        Curated MCP servers you can register in one click. Picking an entry
        pre-fills the new-server form with the catalog slug — your
        credentials stay on your machine (for stdio) or in your MCP client's
        <code>headers</code> (for remote).
      </p>

      <div class="grid grid-cols-3 gap-3">
        <div v-for="e in store.catalog" :key="e.id"
             class="card hover:border-brand-500 cursor-pointer"
             @click="quickAddFromCatalog(e)">
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
              <a v-if="e.docsUrl" :href="e.docsUrl" target="_blank" class="text-xs text-brand-600 hover:underline"
                 @click.stop>
                docs →
              </a>
            </div>
          </div>
        </div>
        <div v-if="store.catalog.length === 0" class="col-span-3 text-center py-8 text-surface-700">
          No catalog entries loaded. Make sure
          <code>catalog/mcp-servers.json</code> is reachable at API startup.
        </div>
      </div>
    </template>
  </div>
</template>
