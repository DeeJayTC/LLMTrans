<script setup lang="ts">
import { computed, ref } from 'vue';
import { RouterLink, useRouter } from 'vue-router';
import { api, friendlyError } from '../api';
import type { RouteKind } from '../types';

// First-run wizard. Shown at /wizard; the dashboard surfaces it as the
// recommended path when no routes exist yet. Walks a new user through the
// four pieces they actually need: provider, language pair, translator, and
// the resulting route token. Anything else (style rules, glossaries, proxy
// rules, profiles) stays out of the way — they can be wired up later.
const router = useRouter();

const step = ref<1 | 2 | 3 | 4>(1);

interface Wizard {
  kind: RouteKind;
  upstreamBaseUrl: string;
  userLanguage: string;
  llmLanguage: string;
  direction: 'Bidirectional' | 'RequestOnly' | 'ResponseOnly';
  translatorId: '' | 'passthrough' | 'deepl' | 'llm';
  routeId: string;
}

const w = ref<Wizard>({
  kind: 'OpenAiChat',
  upstreamBaseUrl: 'https://api.openai.com/',
  userLanguage: 'de',
  llmLanguage: 'en-US',
  direction: 'Bidirectional',
  translatorId: '',
  routeId: '',
});

const providers: Array<{ kind: RouteKind; label: string; upstream: string; description: string }> = [
  {
    kind: 'OpenAiChat',
    label: 'OpenAI',
    upstream: 'https://api.openai.com/',
    description: 'Chat completions, responses, embeddings.',
  },
  {
    kind: 'AnthropicMessages',
    label: 'Anthropic',
    upstream: 'https://api.anthropic.com/',
    description: 'Messages API, including streaming.',
  },
  {
    kind: 'Mcp',
    label: 'MCP server',
    upstream: 'https://mcp.example.com/',
    description: 'Remote Model Context Protocol server.',
  },
  {
    kind: 'Generic',
    label: 'Generic JSON',
    upstream: 'https://api.example.com/',
    description: 'Anything else — declare which JSON paths to translate.',
  },
];

function pickProvider(p: typeof providers[number]) {
  w.value.kind = p.kind;
  w.value.upstreamBaseUrl = p.upstream;
  step.value = 2;
}

const created = ref<{ routeId: string; token: string } | null>(null);
const submitting = ref(false);
const error = ref<string | null>(null);

async function finish() {
  submitting.value = true;
  error.value = null;
  try {
    const id = w.value.routeId || `r_${w.value.kind.toLowerCase()}_${Date.now().toString(36)}`;
    await api.routes.create({
      id,
      tenantId: 't_dev',
      kind: w.value.kind,
      upstreamBaseUrl: w.value.upstreamBaseUrl,
      userLanguage: w.value.userLanguage,
      llmLanguage: w.value.llmLanguage,
      direction: w.value.direction,
      translatorId: w.value.translatorId || null,
    });
    const token = await api.routes.issueToken(id);
    created.value = { routeId: id, token: token.plaintextToken };
    step.value = 4;
  } catch (e: unknown) {
    error.value = friendlyError(e);
  } finally {
    submitting.value = false;
  }
}

const baseUrlExample = computed(() => {
  if (!created.value) return '';
  // Show a copy-paste base URL the user can drop into their SDK config.
  // The path shape is what the proxy expects: /<token>/...
  return `${window.location.origin.replace(/:\\d+$/, '')}:8080/v1/${created.value.token}`;
});

function snippetFor(kind: RouteKind, baseUrl: string): string {
  switch (kind) {
    case 'OpenAiChat':
      return `from openai import OpenAI\nclient = OpenAI(\n  base_url="${baseUrl}",\n  api_key="<your-openai-key>",\n)\nresp = client.chat.completions.create(\n  model="gpt-4o-mini",\n  messages=[{"role":"user","content":"Hallo!"}],\n)`;
    case 'AnthropicMessages':
      return `from anthropic import Anthropic\nclient = Anthropic(\n  base_url="${baseUrl}",\n  api_key="<your-anthropic-key>",\n)`;
    case 'Mcp':
      return `// Drop the issued token into your MCP client config:\n// "url": "${baseUrl}"`;
    default:
      return `# Send any JSON-compatible POST to ${baseUrl}\n# adaptiveapi will translate the JSON paths you configured.`;
  }
}
</script>

<template>
  <div class="max-w-3xl mx-auto flex flex-col gap-4">
    <div class="card">
      <div class="card-header">
        <span>Set up your first route</span>
        <span class="text-xs text-surface-700">
          Step {{ step }} / 4 · cancel any time, this just creates one
          route — you can edit or delete it from
          <RouterLink to="/routes" class="text-brand-600 underline">Routes</RouterLink>
          afterwards.
        </span>
      </div>
    </div>

    <!-- ===== Step 1: pick provider ===== -->
    <div v-if="step === 1" class="grid grid-cols-2 gap-3">
      <button v-for="p in providers" :key="p.kind"
              class="card hover:border-brand-500 cursor-pointer text-left"
              @click="pickProvider(p)">
        <div class="card-body flex flex-col gap-1">
          <div class="font-500">{{ p.label }}</div>
          <div class="text-xs text-surface-700">{{ p.description }}</div>
          <div class="font-mono text-xs text-surface-700 truncate">{{ p.upstream }}</div>
        </div>
      </button>
    </div>

    <!-- ===== Step 2: language pair ===== -->
    <div v-if="step === 2" class="card">
      <div class="card-body flex flex-col gap-4">
        <div>
          <div class="text-sm font-500 mb-1">What language do your users speak?</div>
          <div class="text-xs text-surface-700 mb-2">
            Two-letter ISO code, optionally with a region (de, en-US, fr, …).
          </div>
          <div class="flex items-center gap-2">
            <input class="input w-24 text-center font-mono" v-model="w.userLanguage" />
            <select class="input w-40 text-center" v-model="w.direction">
              <option value="Bidirectional">⇄ Both directions</option>
              <option value="RequestOnly">→ Request only</option>
              <option value="ResponseOnly">← Response only</option>
            </select>
            <input class="input w-24 text-center font-mono" v-model="w.llmLanguage" />
            <span class="text-surface-500 text-xs">← LLM speaks this</span>
          </div>
        </div>

        <div class="flex justify-between">
          <button class="btn" @click="step = 1">Back</button>
          <button class="btn-primary" @click="step = 3">Next</button>
        </div>
      </div>
    </div>

    <!-- ===== Step 3: translator ===== -->
    <div v-if="step === 3" class="card">
      <div class="card-body flex flex-col gap-4">
        <div>
          <div class="text-sm font-500 mb-1">Which translator?</div>
          <div class="text-xs text-surface-700 mb-2">
            Translator engines run in the proxy. <strong>Passthrough</strong>
            is a no-op (forward bytes unchanged) — useful for routing
            without translation. DeepL and LLM need the corresponding API
            key set in <code>.env</code> at startup.
          </div>
          <div class="flex flex-col gap-2">
            <label v-for="t in [
                { id: 'passthrough', label: 'Passthrough — forward bytes unchanged' },
                { id: 'deepl', label: 'DeepL — needs DEEPL_API_KEY in .env' },
                { id: 'llm', label: 'LLM — uses an OpenAI-compatible model to translate' },
              ]" :key="t.id"
              class="flex items-start gap-2 cursor-pointer p-2 rounded hover:bg-surface-50">
              <input type="radio" :value="t.id" v-model="w.translatorId" class="mt-1" />
              <span class="text-sm">{{ t.label }}</span>
            </label>
          </div>
        </div>

        <label class="flex flex-col gap-1 text-xs">
          Route ID (optional — auto-generated if blank)
          <input class="input font-mono text-xs" v-model="w.routeId" placeholder="r_my_route" />
        </label>

        <div v-if="error" class="text-red-700 text-sm">{{ error }}</div>

        <div class="flex justify-between">
          <button class="btn" @click="step = 2">Back</button>
          <button class="btn-primary" :disabled="submitting" @click="finish">
            {{ submitting ? 'Creating…' : 'Create route' }}
          </button>
        </div>
      </div>
    </div>

    <!-- ===== Step 4: done ===== -->
    <div v-if="step === 4 && created" class="card border-brand-500 border">
      <div class="card-body flex flex-col gap-3">
        <div class="text-base font-500">Route created — here's your token.</div>
        <div class="text-xs text-surface-700">
          Copy this now; the host will not show it again. Use it as the
          <code>base_url</code> in your SDK or as the path token in raw HTTP
          calls.
        </div>
        <pre class="select-all">{{ created.token }}</pre>

        <div class="text-xs text-surface-700 mt-2">Example base URL</div>
        <pre class="select-all">{{ baseUrlExample }}</pre>

        <div class="text-xs text-surface-700 mt-2">SDK snippet</div>
        <pre>{{ snippetFor(w.kind, baseUrlExample) }}</pre>

        <div class="flex gap-2 mt-2">
          <button class="btn-primary" @click="router.push('/routes')">Open routes</button>
          <button class="btn" @click="step = 1; created = null">Create another</button>
        </div>
      </div>
    </div>
  </div>
</template>
