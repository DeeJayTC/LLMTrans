<script setup lang="ts">
import { computed, onMounted, onBeforeUnmount, ref, watch, nextTick } from 'vue';
import { useRouter } from 'vue-router';
import { api } from '../api';

// Cmd+K palette: one modal that searches across the things an admin
// regularly hops between (routes, profiles, glossaries, style rules,
// PII rules, request rules, plugins). Fast in/out — pure client-side filter
// over the listings, refreshed when the modal opens.
const router = useRouter();
const open = ref(false);
const query = ref('');
const inputRef = ref<HTMLInputElement | null>(null);
const cursor = ref(0);

interface Item {
  id: string;          // unique key
  label: string;       // primary line
  detail: string;      // secondary line
  group: string;       // category
  to: string;          // route
}

const items = ref<Item[]>([]);

async function refresh() {
  try {
    const [routes, profiles, glossaries, styles, pii, request, plugins] = await Promise.all([
      api.routes.list().catch(() => []),
      api.routeProfiles.list().catch(() => []),
      api.glossaries.list().catch(() => []),
      api.styleRules.list().catch(() => []),
      api.piiRules.list().catch(() => []),
      api.requestRules.list().catch(() => []),
      api.plugins.list().catch(() => ({ loaded: [], disabled: [] })),
    ]);
    items.value = [
      ...routes.map(r => ({
        id: `route:${r.id}`, label: r.id,
        detail: `${r.kind} · ${r.userLanguage} → ${r.llmLanguage}`,
        group: 'Routes', to: '/routes',
      })),
      ...profiles.map(p => ({
        id: `profile:${p.id}`, label: p.name,
        detail: `${p.id}`, group: 'Profiles', to: '/route-profiles',
      })),
      ...glossaries.map(g => ({
        id: `glossary:${g.id}`, label: g.name,
        detail: g.id, group: 'Glossaries', to: `/glossaries/${g.id}`,
      })),
      ...styles.map(s => ({
        id: `style:${s.id}`, label: s.name,
        detail: `${s.id} · ${s.language}`, group: 'Style rules', to: `/style-rules/${s.id}`,
      })),
      ...pii.map(p => ({
        id: `pii:${p.id}`, label: p.name,
        detail: p.pattern, group: 'PII rules', to: '/pii-rules',
      })),
      ...request.map(r => ({
        id: `req:${r.id}`, label: r.name,
        detail: `${r.scope} · ${r.action}`, group: 'Request rules', to: '/request-rules',
      })),
      ...plugins.loaded.map(p => ({
        id: `plugin:${p.id}`, label: p.name,
        detail: `${p.id} · v${p.version}`, group: 'Plugins', to: '/plugins',
      })),
      // Static admin destinations so Cmd+K also navigates to pages.
      { id: 'page:wizard', label: 'Setup wizard', detail: 'Create your first route', group: 'Pages', to: '/wizard' },
      { id: 'page:dashboard', label: 'Dashboard', detail: '', group: 'Pages', to: '/dashboard' },
      { id: 'page:settings', label: 'Settings', detail: 'Production readiness', group: 'Pages', to: '/settings' },
      { id: 'page:logs', label: 'Logs', detail: '', group: 'Pages', to: '/logs' },
    ];
  } catch { /* best-effort */ }
}

const filtered = computed(() => {
  const q = query.value.trim().toLowerCase();
  if (!q) return items.value.slice(0, 50);
  // Simple substring match over label+detail+group; no fuzzy yet.
  return items.value.filter(i =>
    i.label.toLowerCase().includes(q) ||
    i.detail.toLowerCase().includes(q) ||
    i.group.toLowerCase().includes(q)).slice(0, 50);
});

function go(item: Item) {
  router.push(item.to);
  close();
}

function close() {
  open.value = false;
  query.value = '';
  cursor.value = 0;
}

async function onOpen() {
  open.value = true;
  await refresh();
  await nextTick();
  inputRef.value?.focus();
}

function onKeydown(e: KeyboardEvent) {
  // Global trigger.
  if ((e.metaKey || e.ctrlKey) && e.key.toLowerCase() === 'k') {
    e.preventDefault();
    if (open.value) close(); else onOpen();
    return;
  }
  if (!open.value) return;
  if (e.key === 'Escape') { close(); return; }
  if (e.key === 'ArrowDown') {
    e.preventDefault();
    cursor.value = Math.min(cursor.value + 1, filtered.value.length - 1);
  } else if (e.key === 'ArrowUp') {
    e.preventDefault();
    cursor.value = Math.max(cursor.value - 1, 0);
  } else if (e.key === 'Enter') {
    e.preventDefault();
    const item = filtered.value[cursor.value];
    if (item) go(item);
  }
}

watch(query, () => { cursor.value = 0; });

onMounted(() => window.addEventListener('keydown', onKeydown));
onBeforeUnmount(() => window.removeEventListener('keydown', onKeydown));
</script>

<template>
  <div v-if="open"
       class="fixed inset-0 z-50 flex items-start justify-center pt-24 bg-black/30"
       @click.self="close">
    <div class="bg-white rounded-lg shadow-2xl w-[640px] max-h-[70vh] flex flex-col overflow-hidden">
      <input ref="inputRef" v-model="query"
             placeholder="Search routes, rules, plugins, settings…"
             class="px-4 py-3 text-base outline-none border-b border-surface-200" />
      <div class="overflow-y-auto flex-1">
        <div v-if="filtered.length === 0" class="px-4 py-6 text-center text-sm text-surface-700">
          No matches.
        </div>
        <div v-for="(item, idx) in filtered" :key="item.id"
             class="px-4 py-2 cursor-pointer flex items-center gap-3 border-b border-surface-100"
             :class="idx === cursor ? 'bg-brand-50' : 'hover:bg-surface-50'"
             @mouseenter="cursor = idx"
             @click="go(item)">
          <div class="flex-1">
            <div class="text-sm">{{ item.label }}</div>
            <div class="text-xs text-surface-700 truncate">{{ item.detail }}</div>
          </div>
          <span class="chip text-xs">{{ item.group }}</span>
        </div>
      </div>
      <div class="border-t border-surface-200 px-4 py-2 text-xs text-surface-500 flex items-center gap-3">
        <span>↑↓ navigate</span>
        <span>↵ open</span>
        <span>esc close</span>
        <span class="ml-auto">Cmd+K to toggle</span>
      </div>
    </div>
  </div>
</template>
