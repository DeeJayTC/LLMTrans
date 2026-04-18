<script setup lang="ts">
import { computed, onMounted, ref } from 'vue';
import { api } from '../api';
import type { Route } from '../types';

const routes = ref<Route[]>([]);
const selectedRouteId = ref<string>('');
const tokenInput = ref('rt_dev_LOCALDEMO');
const targetLang = ref('de');
const sourceLang = ref('en');
const mode = ref<'bidirectional' | 'request-only' | 'response-only' | 'off'>('bidirectional');
const bodyInput = ref('{"model":"gpt-4o-mini","messages":[{"role":"user","content":"Hello, friend. Explain pull requests."}]}');

const sending = ref(false);
const requestPreview = ref('');
const responseBody = ref('');
const responseStatus = ref<number | null>(null);
const errorMsg = ref<string | null>(null);

const selectedRoute = computed(() =>
  routes.value.find((r) => r.id === selectedRouteId.value) ?? null,
);

const proxyPath = computed(() => {
  const route = selectedRoute.value;
  if (!route) return '/v1/<token>/chat/completions';
  switch (route.kind) {
    case 'OpenAiChat':         return `/v1/${tokenInput.value}/chat/completions`;
    case 'AnthropicMessages':  return `/anthropic/v1/${tokenInput.value}/messages`;
    case 'Generic':            return `/generic/${tokenInput.value}/`;
    case 'Mcp':                return `/mcp/${tokenInput.value}`;
    default:                   return `/v1/${tokenInput.value}/chat/completions`;
  }
});

async function send() {
  errorMsg.value = null;
  responseBody.value = '';
  responseStatus.value = null;
  sending.value = true;
  try {
    // Persist the pretty-printed request so users can see exactly what they sent.
    try { requestPreview.value = JSON.stringify(JSON.parse(bodyInput.value), null, 2); }
    catch { requestPreview.value = bodyInput.value; }

    const headers: Record<string, string> = { 'Content-Type': 'application/json' };
    if (targetLang.value) headers['X-AdaptiveApi-Target-Lang'] = targetLang.value;
    if (sourceLang.value) headers['X-AdaptiveApi-Source-Lang'] = sourceLang.value;
    headers['X-AdaptiveApi-Mode'] = mode.value;

    const resp = await fetch(proxyPath.value, {
      method: 'POST',
      headers,
      body: bodyInput.value,
    });
    responseStatus.value = resp.status;

    const text = await resp.text();
    try { responseBody.value = JSON.stringify(JSON.parse(text), null, 2); }
    catch { responseBody.value = text; }
  } catch (e: unknown) {
    errorMsg.value = e instanceof Error ? e.message : String(e);
  } finally {
    sending.value = false;
  }
}

onMounted(async () => {
  try { routes.value = await api.routes.list(); } catch { /* no routes yet */ }
});
</script>

<template>
  <div class="flex flex-col gap-3" style="height: calc(100vh - 170px)">
    <!-- Controls -->
    <div class="card">
      <div class="card-body grid grid-cols-6 gap-3 items-end">
        <label class="flex flex-col gap-1 text-xs col-span-2">
          Route
          <select class="input" v-model="selectedRouteId">
            <option value="">— ad-hoc —</option>
            <option v-for="r in routes" :key="r.id" :value="r.id">
              {{ r.id }} · {{ r.kind }}
            </option>
          </select>
        </label>
        <label class="flex flex-col gap-1 text-xs col-span-2">
          Route token
          <input class="input font-mono" v-model="tokenInput" placeholder="rt_dev_LOCALDEMO" />
        </label>
        <label class="flex flex-col gap-1 text-xs">
          Target (user)
          <input class="input" v-model="targetLang" placeholder="de" />
        </label>
        <label class="flex flex-col gap-1 text-xs">
          Source (llm)
          <input class="input" v-model="sourceLang" placeholder="en" />
        </label>
        <label class="flex flex-col gap-1 text-xs col-span-2">
          Direction
          <select class="input" v-model="mode">
            <option value="bidirectional">Bidirectional</option>
            <option value="request-only">Request only</option>
            <option value="response-only">Response only</option>
            <option value="off">Off</option>
          </select>
        </label>
        <div class="col-span-2 text-xs text-surface-700 font-mono truncate">
          POST {{ proxyPath }}
        </div>
        <div class="col-span-2 text-right">
          <button class="btn-primary" @click="send" :disabled="sending">
            {{ sending ? 'Sending…' : 'Send' }}
          </button>
        </div>
      </div>
    </div>

    <!-- 4-pane view -->
    <div class="grid grid-cols-2 gap-3 flex-1 overflow-hidden">
      <div class="card flex flex-col overflow-hidden">
        <div class="card-header">Request (editable)</div>
        <textarea
          class="flex-1 w-full p-3 font-mono text-xs outline-none border-0 resize-none bg-surface-0"
          v-model="bodyInput"
          spellcheck="false"
        />
      </div>

      <div class="card flex flex-col overflow-hidden">
        <div class="card-header">
          <span>Response</span>
          <span v-if="responseStatus !== null" class="chip"
                :class="responseStatus >= 400 ? 'text-red-700' : 'text-green-700'">
            HTTP {{ responseStatus }}
          </span>
        </div>
        <pre class="flex-1 overflow-auto m-0 rounded-none">{{ responseBody || 'No response yet.' }}</pre>
      </div>

      <div class="card flex flex-col overflow-hidden">
        <div class="card-header">What was sent</div>
        <pre class="flex-1 overflow-auto m-0 rounded-none">{{ requestPreview || 'Send a request to see the normalized body.' }}</pre>
      </div>

      <div class="card flex flex-col overflow-hidden">
        <div class="card-header">Notes</div>
        <div class="card-body text-sm text-surface-700">
          <ul class="list-disc pl-5 space-y-1">
            <li>The <span class="font-mono">X-AdaptiveApi-Target-Lang</span> and
              <span class="font-mono">X-AdaptiveApi-Source-Lang</span> headers override your route's
              configured languages per call.</li>
            <li>When <span class="font-mono">Mode</span> is <code>off</code>, the proxy forwards
              your bytes verbatim — a useful way to confirm routing works before debugging
              translation quality.</li>
            <li>For OpenAI and Anthropic you can paste the same body your SDK would send.
              For Generic routes, remember to set the body shape your JSONPath config expects.</li>
            <li>Anything above a 4xx on the response side is the <em>upstream</em>'s rejection.
              Check the <router-link to="/logs" class="text-brand-600">Logs</router-link> page
              for the audit record.</li>
          </ul>
          <div v-if="errorMsg" class="text-xs text-red-700 mt-3">{{ errorMsg }}</div>
        </div>
      </div>
    </div>
  </div>
</template>
