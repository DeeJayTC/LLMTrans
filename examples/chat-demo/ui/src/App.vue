<script setup lang="ts">
import { computed, nextTick, onMounted, ref, watch } from 'vue';

interface LanguageInfo { code: string; name: string; }
interface ConfigResponse {
  adaptiveapi: { baseUrl: string };
  openAiModel: string;
  languages: LanguageInfo[];
  hasServerKey: boolean;
  includePayloads: boolean;
}
interface DebugTracePair { source: string | null; target: string | null; }
interface DebugTranslatorCall {
  direction: string;
  sourceLanguage: string;
  targetLanguage: string;
  pairs: DebugTracePair[];
}
interface DebugBundle {
  bodies?: {
    requestPreTranslation?: string | null;
    requestPostTranslation?: string | null;
    upstreamResponse?: string | null;
    finalResponse?: string | null;
  };
  translatorCalls?: DebugTranslatorCall[];
}
interface RouteInfo {
  id: string;
  kind: string;
  userLanguage: string;
  llmLanguage: string;
  direction: string;
  translatorId: string | null;
  upstreamBaseUrl: string;
  tokenMasked: string;
}
interface PipelineEntry {
  step: string;
  timestampIso: string;
  sinceStartMs: number;
  metadata: Record<string, unknown>;
}
interface ChatTurn {
  role: 'user' | 'assistant';
  content: string;
  pending?: boolean;
  pipeline?: PipelineEntry[];
}

const config = ref<ConfigResponse | null>(null);
const routes = ref<RouteInfo[]>([]);
const routeId = ref<string>('');
const language = ref('de');
const strategy = ref<'sentence' | 'progressive' | 'none'>('sentence');
const stream = ref(true);
const input = ref('');
const sending = ref(false);
const error = ref<string | null>(null);
const log = ref<string[]>([]);
const history = ref<ChatTurn[]>([]);
const transcript = ref<HTMLElement | null>(null);

const selectedRoute = computed(() =>
  routes.value.find((r) => r.id === routeId.value) ?? null,
);

// --- OpenAI key handling ---
const STORAGE_KEY = 'adaptiveapi-demo.openai-key';
const userKey = ref<string>('');
const keyDraft = ref<string>('');
const keyPanelOpen = ref(false);
const revealKey = ref(false);

const keyAvailable = computed(() => !!userKey.value || (config.value?.hasServerKey ?? false));
const keySource = computed<'browser' | 'server' | 'none'>(() => {
  if (userKey.value) return 'browser';
  if (config.value?.hasServerKey) return 'server';
  return 'none';
});
const keyMasked = computed(() =>
  userKey.value.length <= 10
    ? '.'.repeat(userKey.value.length)
    : userKey.value.slice(0, 5) + '.'.repeat(Math.max(4, userKey.value.length - 9)) + userKey.value.slice(-4),
);

function loadSavedKey() {
  try {
    const v = localStorage.getItem(STORAGE_KEY);
    if (v) userKey.value = v;
  } catch { /* private mode — silent */ }
}
function saveKey() {
  const v = keyDraft.value.trim();
  if (!v) return;
  userKey.value = v;
  try { localStorage.setItem(STORAGE_KEY, v); } catch { /* ignore */ }
  keyDraft.value = '';
  keyPanelOpen.value = false;
  appendLog('OpenAI key saved to browser storage');
}
function clearKey() {
  userKey.value = '';
  try { localStorage.removeItem(STORAGE_KEY); } catch { /* ignore */ }
  appendLog('OpenAI key cleared');
}

// --- derived copy ---
const placeholder = computed(() => {
  const hint: Record<string, string> = {
    en: 'Ask something in English…',
    'en-US': 'Ask something in English…',
    'en-GB': 'Ask something in English…',
    de: 'Stelle eine Frage auf Deutsch…',
    fr: 'Posez une question en français…',
    es: 'Haz una pregunta en español…',
    it: 'Fai una domanda in italiano…',
    nl: 'Stel een vraag in het Nederlands…',
    'pt-BR': 'Faça uma pergunta em português…',
    ja: '日本語で質問してください…',
    ko: '한국어로 질문하세요…',
    zh: '用中文提问…',
  };
  return hint[language.value] ?? 'Type a message…';
});

function formatMeta(meta: Record<string, unknown>): string {
  return Object.entries(meta)
    .filter(([, v]) => v !== null && v !== undefined && v !== '')
    .filter(([k]) => k !== 'body' && k !== 'pairs') // large fields rendered separately
    .map(([k, v]) => `${k}=${typeof v === 'string' ? truncate(String(v), 140) : JSON.stringify(v)}`)
    .join(' · ');
}

function truncate(s: string, max: number): string {
  return s.length <= max ? s : s.slice(0, max) + '…';
}

function debugBundleToPipelineEntries(bundle: DebugBundle, baseMs: number): PipelineEntry[] {
  const entries: PipelineEntry[] = [];
  const now = new Date().toISOString();

  const pre = bundle.bodies?.requestPreTranslation ?? null;
  const post = bundle.bodies?.requestPostTranslation ?? null;
  const upstream = bundle.bodies?.upstreamResponse ?? null;
  const final = bundle.bodies?.finalResponse ?? null;

  if (pre)
    entries.push({ step: 'adaptiveapi_debug_request_pre', timestampIso: now, sinceStartMs: baseMs,
      metadata: { body: pre, bytes: pre.length } });
  if (post && post !== pre)
    entries.push({ step: 'adaptiveapi_debug_request_to_openai', timestampIso: now, sinceStartMs: baseMs,
      metadata: { body: post, bytes: post.length } });
  if (upstream)
    entries.push({ step: 'adaptiveapi_debug_openai_response', timestampIso: now, sinceStartMs: baseMs,
      metadata: { body: upstream, bytes: upstream.length } });
  if (final && final !== upstream)
    entries.push({ step: 'adaptiveapi_debug_final_to_user', timestampIso: now, sinceStartMs: baseMs,
      metadata: { body: final, bytes: final.length } });

  (bundle.translatorCalls ?? []).forEach((call, i) => {
    entries.push({
      step: `adaptiveapi_debug_translator_${i + 1}`,
      timestampIso: now,
      sinceStartMs: baseMs,
      metadata: {
        direction: call.direction,
        langPair: `${call.sourceLanguage} → ${call.targetLanguage}`,
        pairs: call.pairs,
      },
    });
  });

  return entries;
}

function pipelinePayload(entry: PipelineEntry): string | null {
  const body = entry.metadata.body;
  if (typeof body !== 'string') return null;
  try { return JSON.stringify(JSON.parse(body), null, 2); }
  catch { return body; }
}

function pipelineTranslatorPairs(entry: PipelineEntry): DebugTracePair[] | null {
  const pairs = entry.metadata.pairs;
  if (!Array.isArray(pairs)) return null;
  return pairs as DebugTracePair[];
}

// --- lifecycle ---
onMounted(async () => {
  loadSavedKey();
  try {
    const resp = await fetch('/api/config');
    config.value = await resp.json();
    if (!keyAvailable.value) keyPanelOpen.value = true;
  } catch {
    error.value = 'Could not reach the demo backend on /api/config.';
    return;
  }
  try {
    const resp = await fetch('/api/routes');
    routes.value = await resp.json();
    if (routes.value.length > 0) routeId.value = routes.value[0]!.id;
    if (routes.value.length === 0) error.value = 'No OpenAI-chat routes found in adaptiveapi admin. Create one in the admin UI first.';
  } catch {
    error.value = 'Could not load routes from /api/routes.';
  }
});

watch(history, () => {
  nextTick(() => {
    if (transcript.value) transcript.value.scrollTop = transcript.value.scrollHeight;
  });
}, { deep: true });

function appendLog(line: string) {
  log.value.push(`${new Date().toLocaleTimeString()}  ${line}`);
  if (log.value.length > 400) log.value.shift();
}

// --- send ---
function authHeaders(): Record<string, string> {
  return userKey.value ? { 'X-Demo-OpenAI-Key': userKey.value } : {};
}

function logPipelineEntries(direction: 'sent' | 'recv', entries: PipelineEntry[]) {
  for (const entry of entries) {
    const meta = formatMeta(entry.metadata);
    appendLog(`[${direction}] +${String(entry.sinceStartMs).padStart(4, ' ')}ms  ${entry.step.padEnd(30)} ${meta}`);
  }
}

async function send() {
  const text = input.value.trim();
  if (!text || sending.value) return;
  if (!keyAvailable.value) {
    error.value = 'Add an OpenAI key first — open the key panel above.';
    keyPanelOpen.value = true;
    return;
  }
  if (!routeId.value) {
    error.value = 'Pick a route first.';
    return;
  }
  input.value = '';

  const userTurn: ChatTurn = { role: 'user', content: text };
  history.value.push(userTurn);
  const assistant: ChatTurn = { role: 'assistant', content: '', pending: true, pipeline: [] };
  history.value.push(assistant);

  sending.value = true;
  error.value = null;

  const body = {
    message: text,
    language: language.value,
    routeId: routeId.value,
    streamStrategy: strategy.value === 'none' ? null : strategy.value,
    history: history.value
      .slice(0, -2)
      .filter((t) => !t.pending)
      .map((t) => ({ role: t.role, content: t.content })),
  };

  try {
    if (stream.value) await sendStreaming(body, assistant);
    else await sendOnce(body, assistant);
    assistant.pending = false;
  } catch (e: unknown) {
    error.value = e instanceof Error ? e.message : String(e);
    assistant.pending = false;
    assistant.content = assistant.content || '(failed)';
  } finally {
    sending.value = false;
  }
}

async function sendOnce(body: unknown, assistant: ChatTurn) {
  appendLog(`POST /api/chat  route=${routeId.value}  lang=${language.value}  key=${keySource.value}`);
  const resp = await fetch('/api/chat', {
    method: 'POST',
    headers: { 'content-type': 'application/json', ...authHeaders() },
    body: JSON.stringify(body),
  });
  if (!resp.ok) {
    if (resp.status === 428) throw new Error('OpenAI key missing or rejected.');
    const payload = await resp.json().catch(() => null) as Record<string, unknown> | null;
    if (payload?.pipeline) logPipelineEntries('recv', payload.pipeline as PipelineEntry[]);
    throw new Error(`HTTP ${resp.status}`);
  }
  const data = await resp.json() as Record<string, unknown>;
  const choices = data.choices as Array<{ message?: { content?: string } }> | undefined;
  assistant.content = choices?.[0]?.message?.content ?? '(no content)';

  const pipeline = (data._pipeline as PipelineEntry[] | undefined) ?? [];
  assistant.pipeline = pipeline;
  logPipelineEntries('recv', pipeline);
  appendLog(`← ${assistant.content.length} chars`);
}

async function sendStreaming(body: unknown, assistant: ChatTurn) {
  appendLog(`POST /api/chat/stream  route=${routeId.value}  lang=${language.value}  strategy=${strategy.value}  key=${keySource.value}`);
  const resp = await fetch('/api/chat/stream', {
    method: 'POST',
    headers: { 'content-type': 'application/json', ...authHeaders() },
    body: JSON.stringify(body),
  });
  if (!resp.ok) {
    if (resp.status === 428) throw new Error('OpenAI key missing or rejected.');
    throw new Error(`HTTP ${resp.status}`);
  }
  if (!resp.body) throw new Error('no stream body');

  const reader = resp.body.getReader();
  const decoder = new TextDecoder();
  let buffer = '';

  while (true) {
    const { value, done } = await reader.read();
    if (done) break;
    buffer += decoder.decode(value, { stream: true });
    let idx: number;
    while ((idx = buffer.indexOf('\n\n')) >= 0) {
      const block = buffer.slice(0, idx);
      buffer = buffer.slice(idx + 2);
      handleSseBlock(block, assistant);
    }
  }
  appendLog(`← ${assistant.content.length} chars streamed`);
}

function handleSseBlock(block: string, assistant: ChatTurn) {
  let eventName = 'message';
  const dataLines: string[] = [];
  for (const line of block.split('\n')) {
    if (line.startsWith('event: ')) eventName = line.slice(7).trim();
    else if (line.startsWith('data: ')) dataLines.push(line.slice(6));
  }
  const data = dataLines.join('\n');
  if (!data) return;

  if (eventName === 'pipeline') {
    try {
      const entries = JSON.parse(data) as PipelineEntry[];
      assistant.pipeline = [...(assistant.pipeline ?? []), ...entries];
      logPipelineEntries('recv', entries);
    } catch { /* ignore bad pipeline payload */ }
    return;
  }

  // Trailing debug event with raw OpenAI + DeepL payloads (opt-in via config).
  if (eventName === 'x-adaptiveapi-debug') {
    try {
      const bundle = JSON.parse(data) as DebugBundle;
      const baseMs = assistant.pipeline?.at(-1)?.sinceStartMs ?? 0;
      const entries = debugBundleToPipelineEntries(bundle, baseMs);
      assistant.pipeline = [...(assistant.pipeline ?? []), ...entries];
      logPipelineEntries('recv', entries);
    } catch { /* ignore bad debug payload */ }
    return;
  }

  // adaptiveapi emits this at the end of a translated SSE stream with the per-stage
  // timings that couldn't fit into the Server-Timing header (which was flushed
  // before the body began). We flatten them into the pipeline timeline so the
  // user sees translate-request / translate-response-stream alongside the
  // demo-observed steps.
  if (eventName === 'x-adaptiveapi-timing') {
    try {
      const parsed = JSON.parse(data) as {
        entries: Array<{ name: string; durationMs: number; desc: string | null }>;
        stream: { strategy: string; eventsIn: number; eventsOut: number;
                  charsTranslated: number; integrityFailures: number };
      };
      const baseMs = assistant.pipeline?.at(-1)?.sinceStartMs ?? 0;
      const entries: PipelineEntry[] = parsed.entries.map((e) => ({
        step: `adaptiveapi_${e.name}`,
        timestampIso: new Date().toISOString(),
        sinceStartMs: baseMs,
        metadata: {
          durationMs: e.durationMs,
          desc: e.desc ?? undefined,
        },
      }));
      entries.push({
        step: 'adaptiveapi_stream_metrics',
        timestampIso: new Date().toISOString(),
        sinceStartMs: baseMs,
        metadata: parsed.stream as unknown as Record<string, unknown>,
      });
      assistant.pipeline = [...(assistant.pipeline ?? []), ...entries];
      logPipelineEntries('recv', entries);
    } catch { /* ignore bad trailer */ }
    return;
  }

  if (data === '[DONE]') return;
  try {
    const event = JSON.parse(data) as { choices?: Array<{ delta?: { content?: string } }> };
    const delta = event.choices?.[0]?.delta?.content;
    if (typeof delta === 'string') assistant.content += delta;
  } catch { /* non-JSON data line */ }
}

function clearHistory() {
  history.value = [];
  log.value = [];
}
</script>

<template>
  <div style="display: grid; grid-template-rows: auto 1fr auto; height: 100vh; max-width: 980px; margin: 0 auto; padding: 16px;">
    <!-- Header -->
    <header style="padding-bottom: 12px; border-bottom: 1px solid #e4e8ee;">
      <div style="display: flex; align-items: center; gap: 12px; flex-wrap: wrap;">
        <div style="font-size: 18px; font-weight: 600; color: #1d47e8;">adaptiveapi · chat demo</div>
        <div v-if="config" style="font-size: 12px; color: #4a5463;">
          via <code>{{ config.adaptiveapi.baseUrl }}</code> · model <code>{{ config.openAiModel }}</code>
        </div>
        <span
          v-if="config?.includePayloads"
          title="DEMO_INCLUDE_PAYLOADS is on — pipeline log shows raw OpenAI and DeepL request/response bodies. Turn off before sharing this with anyone else."
          style="padding: 2px 8px; border-radius: 10px; background: #fee2e2; color: #b91c1c; font-size: 11px; font-weight: 500;"
        >
          debug payloads on
        </span>
        <button
          @click="keyPanelOpen = !keyPanelOpen"
          style="margin-left: auto;"
          :style="{
            borderColor: keyAvailable ? '#16a34a' : '#b91c1c',
            color: keyAvailable ? '#166534' : '#b91c1c',
          }"
        >
          <span v-if="keySource === 'browser'">key · browser</span>
          <span v-else-if="keySource === 'server'">key · server</span>
          <span v-else>⚠ key missing</span>
        </button>
      </div>

      <!-- Key panel -->
      <div
        v-if="keyPanelOpen"
        style="margin-top: 12px; padding: 14px 16px; border: 1px solid #dfe9ff; background: #f0f6ff; border-radius: 8px;"
      >
        <div style="font-weight: 500; margin-bottom: 6px;">OpenAI API key</div>
        <p style="font-size: 12px; color: #4a5463; margin: 0 0 10px;">
          Stored in this browser's <code>localStorage</code> and sent as
          <code>X-Demo-OpenAI-Key</code> on every request. The backend forwards it
          verbatim through adaptiveapi to OpenAI; adaptiveapi does not persist it.
          <strong style="color: #b91c1c;">Localhost-only.</strong>
        </p>

        <div v-if="!userKey" style="display: flex; gap: 8px; align-items: center;">
          <input
            :type="revealKey ? 'text' : 'password'"
            v-model="keyDraft"
            placeholder="sk-…"
            autocomplete="off"
            spellcheck="false"
            style="flex: 1; padding: 8px 12px; border: 1px solid #e4e8ee; border-radius: 6px; background: #fff; font-family: 'JetBrains Mono', monospace; font-size: 12px;"
            @keydown.enter="saveKey"
          />
          <button @click="revealKey = !revealKey">{{ revealKey ? 'hide' : 'show' }}</button>
          <button class="primary" @click="saveKey" :disabled="!keyDraft.trim()">Save</button>
        </div>
        <div v-else style="display: flex; gap: 8px; align-items: center; font-family: 'JetBrains Mono', monospace; font-size: 12px;">
          <code style="flex: 1; padding: 8px 12px; background: #fff; border: 1px solid #e4e8ee; border-radius: 6px;">{{ keyMasked }}</code>
          <button @click="clearKey">Clear</button>
        </div>
      </div>

      <!-- Chat controls -->
      <div style="display: flex; align-items: center; gap: 12px; margin-top: 10px; flex-wrap: wrap;">
        <label style="display: flex; align-items: center; gap: 6px; font-size: 12px;">
          Route
          <select v-model="routeId" style="padding: 5px 8px; border: 1px solid #e4e8ee; border-radius: 6px; background: #fff;">
            <option v-for="r in routes" :key="r.id" :value="r.id">
              {{ r.id }} · {{ r.userLanguage }} ↔ {{ r.llmLanguage }}
            </option>
            <option v-if="routes.length === 0" disabled value="">(no routes discovered)</option>
          </select>
        </label>
        <span v-if="selectedRoute" style="font-size: 11px; color: #4a5463;">
          token <code>{{ selectedRoute.tokenMasked }}</code> · translator
          <code>{{ selectedRoute.translatorId ?? 'default' }}</code>
        </span>
        <label style="display: flex; align-items: center; gap: 6px; font-size: 12px;">
          Language
          <select v-model="language" style="padding: 5px 8px; border: 1px solid #e4e8ee; border-radius: 6px; background: #fff;">
            <option v-for="l in config?.languages ?? []" :key="l.code" :value="l.code">
              {{ l.name }} ({{ l.code }})
            </option>
          </select>
        </label>
        <label style="display: flex; align-items: center; gap: 6px; font-size: 12px;">
          <input type="checkbox" v-model="stream" /> stream
        </label>
        <label v-if="stream" style="display: flex; align-items: center; gap: 6px; font-size: 12px;">
          Strategy
          <select v-model="strategy" style="padding: 5px 8px; border: 1px solid #e4e8ee; border-radius: 6px; background: #fff;">
            <option value="sentence">sentence-boundary</option>
            <option value="progressive">progressive</option>
            <option value="none">default (no header)</option>
          </select>
        </label>
        <button @click="clearHistory" :disabled="sending" style="margin-left: auto;">Clear</button>
      </div>
    </header>

    <!-- Transcript -->
    <main ref="transcript" style="overflow-y: auto; padding: 16px 0; display: flex; flex-direction: column; gap: 14px;">
      <div v-if="history.length === 0" style="color: #4a5463; text-align: center; padding: 48px 0;">
        <div v-if="!keyAvailable" style="color: #b91c1c;">
          Add an OpenAI API key above to start the demo.
        </div>
        <div v-else-if="routes.length === 0" style="color: #b91c1c;">
          No OpenAI-chat routes are configured on the adaptiveapi admin yet.
        </div>
        <div v-else>
          Type a message. Pick a language and a route. Each reply shows a pipeline
          timeline beneath it so you can see every stage.
        </div>
      </div>

      <div
        v-for="(turn, i) in history"
        :key="i"
        style="display: flex; flex-direction: column; gap: 6px;"
        :style="{ alignItems: turn.role === 'user' ? 'flex-end' : 'flex-start' }"
      >
        <div
          :style="{
            maxWidth: '80%',
            background: turn.role === 'user' ? '#2d5bff' : '#fff',
            color: turn.role === 'user' ? '#fff' : '#171c24',
            border: turn.role === 'user' ? 'none' : '1px solid #e4e8ee',
            borderRadius: '10px',
            padding: '10px 14px',
            whiteSpace: 'pre-wrap',
            wordBreak: 'break-word',
          }"
        >
          <span>{{ turn.content }}</span>
          <span v-if="turn.pending" style="display: inline-block; margin-left: 4px; opacity: 0.6;">▌</span>
        </div>

        <details v-if="turn.role === 'assistant' && turn.pipeline && turn.pipeline.length" style="max-width: 80%; width: 100%;">
          <summary style="cursor: pointer; font-size: 11px; color: #4a5463;">
            Pipeline · {{ turn.pipeline.length }} step{{ turn.pipeline.length === 1 ? '' : 's' }} · total {{ turn.pipeline[turn.pipeline.length - 1]?.sinceStartMs ?? 0 }}ms
          </summary>
          <div style="margin-top: 6px; display: flex; flex-direction: column; gap: 3px;">
            <div
              v-for="(entry, idx) in turn.pipeline"
              :key="idx"
              style="font-family: 'JetBrains Mono', monospace; font-size: 11px;"
            >
              <div style="display: grid; grid-template-columns: 70px 230px 1fr; gap: 8px; align-items: baseline;">
                <span style="color: #4a5463;">+{{ String(entry.sinceStartMs).padStart(4, ' ') }}ms</span>
                <span :style="{ color: entry.step.startsWith('adaptiveapi_debug_') ? '#8a2be2' : '#1d47e8' }">
                  {{ entry.step }}
                </span>
                <span style="color: #4a5463; word-break: break-word;">{{ formatMeta(entry.metadata) }}</span>
              </div>
              <details v-if="pipelinePayload(entry)" style="margin: 2px 0 6px 78px;">
                <summary style="cursor: pointer; color: #4a5463;">payload · {{ entry.metadata.bytes }} bytes</summary>
                <pre style="margin: 4px 0 0 0; max-height: 220px; overflow: auto; background: #0f1419; color: #e4e8ee; padding: 8px; border-radius: 4px;">{{ pipelinePayload(entry) }}</pre>
              </details>
              <details v-else-if="pipelineTranslatorPairs(entry)" style="margin: 2px 0 6px 78px;">
                <summary style="cursor: pointer; color: #4a5463;">translator I/O · {{ pipelineTranslatorPairs(entry)?.length }} pair{{ pipelineTranslatorPairs(entry)?.length === 1 ? '' : 's' }}</summary>
                <div style="margin-top: 4px; display: flex; flex-direction: column; gap: 4px;">
                  <div
                    v-for="(pair, pi) in pipelineTranslatorPairs(entry) ?? []"
                    :key="pi"
                    style="border-left: 2px solid #dfe9ff; padding: 3px 8px;"
                  >
                    <div style="color: #1d47e8;">source · {{ entry.metadata.langPair }}</div>
                    <div style="white-space: pre-wrap; word-break: break-word; color: #171c24;">{{ pair.source }}</div>
                    <div style="color: #16a34a; margin-top: 4px;">target</div>
                    <div style="white-space: pre-wrap; word-break: break-word; color: #171c24;">{{ pair.target }}</div>
                  </div>
                </div>
              </details>
            </div>
          </div>
        </details>
      </div>
    </main>

    <!-- Input + log -->
    <footer style="padding-top: 12px; border-top: 1px solid #e4e8ee;">
      <div style="display: flex; gap: 8px;">
        <textarea
          v-model="input"
          :placeholder="placeholder"
          :disabled="sending"
          rows="2"
          style="flex: 1; padding: 8px 12px; border: 1px solid #e4e8ee; border-radius: 6px; background: #fff; resize: vertical;"
          @keydown.enter.exact.prevent="send"
        />
        <button class="primary" @click="send" :disabled="sending || !input.trim()" style="min-width: 90px;">
          {{ sending ? 'Sending…' : 'Send' }}
        </button>
      </div>
      <div v-if="error" style="margin-top: 8px; color: #b91c1c; font-size: 12px;">{{ error }}</div>
      <details v-if="log.length" style="margin-top: 10px;">
        <summary style="cursor: pointer; font-size: 12px; color: #4a5463;">
          Combined log · {{ log.length }} entr{{ log.length === 1 ? 'y' : 'ies' }} (metadata only, no message bodies)
        </summary>
        <pre style="margin-top: 6px; max-height: 200px; overflow-y: auto;">{{ log.join('\n') }}</pre>
      </details>
    </footer>
  </div>
</template>
