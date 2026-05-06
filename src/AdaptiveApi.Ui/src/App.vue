<script setup lang="ts">
import { computed, onMounted, ref } from 'vue';
import { RouterLink, RouterView, useRoute } from 'vue-router';
import { api } from './api';
import CommandPalette from './components/CommandPalette.vue';

const route = useRoute();
const pageTitle = computed(() => (route.meta.title as string) ?? 'adaptiveapi');

const nav = [
  { to: '/dashboard', label: 'Dashboard', icon: 'i-carbon-dashboard' },
  { to: '/routes', label: 'Routes', icon: 'i-carbon-flow' },
  { to: '/route-profiles', label: 'Profiles', icon: 'i-carbon-folder-details' },
  { to: '/mcp', label: 'MCP', icon: 'i-carbon-plug' },
  { to: '/glossaries', label: 'Glossaries', icon: 'i-carbon-book' },
  { to: '/style-rules', label: 'Style rules', icon: 'i-carbon-pen' },
  { to: '/proxy-rules', label: 'Proxy rules', icon: 'i-carbon-rule' },
  { to: '/pii-rules', label: 'PII rules', icon: 'i-carbon-shield' },
  { to: '/request-rules', label: 'Request rules', icon: 'i-carbon-policy' },
  { to: '/plugins', label: 'Plugins', icon: 'i-carbon-plug-filled' },
  { to: '/playground', label: 'Playground', icon: 'i-carbon-play' },
  { to: '/logs', label: 'Logs', icon: 'i-carbon-list-boxes' },
  { to: '/settings', label: 'Settings', icon: 'i-carbon-settings' },
];

// Auth-mode banner: when the host is running with `AdaptiveApi:Auth:Mode=none`
// (the default for local dev), anyone reaching the admin URL has full
// admin access. Show a permanent banner so an operator who exposes this
// instance to a network notices.
const authIsOpen = ref(false);
const authMode = ref('');
onMounted(async () => {
  try {
    const s = await api.system.authStatus();
    authIsOpen.value = s.isOpen;
    authMode.value = s.mode;
  } catch {
    // Endpoint missing or unauthorized — leave the banner off; the user
    // already knows something's wrong if other admin calls are also failing.
  }
});
</script>

<template>
  <div class="min-h-screen grid grid-cols-[240px_1fr]">
    <aside class="border-r border-surface-200 bg-surface-0 px-3 py-4 flex flex-col gap-1">
      <div class="px-3 pb-4 flex items-center gap-2 text-brand-600 font-600 text-lg">
        <span class="i-carbon-translate text-xl" />
        <span>adaptiveapi</span>
      </div>
      <RouterLink
        v-for="item in nav"
        :key="item.to"
        :to="item.to"
        class="px-3 py-2 rounded-md text-sm text-surface-700 hover:bg-surface-50 flex items-center gap-2"
        active-class="bg-brand-50 text-brand-700 font-500"
      >
        <span :class="item.icon" />
        {{ item.label }}
      </RouterLink>
    </aside>

    <main class="flex flex-col">
      <header class="border-b border-surface-200 bg-surface-0 px-6 py-3 flex items-center justify-between">
        <h1 class="text-base font-600">{{ pageTitle }}</h1>
        <div class="text-xs text-surface-700 flex items-center gap-2">
          <span class="font-mono px-1.5 py-0.5 border border-surface-200 rounded">Cmd K</span>
          <span class="chip">dev</span>
        </div>
      </header>
      <div
        v-if="authIsOpen"
        class="bg-yellow-50 border-b border-yellow-300 text-yellow-900 px-6 py-2 text-sm flex items-center gap-2"
      >
        <span class="i-carbon-warning-alt text-base" />
        <span>
          <strong>Authentication is off</strong> ({{ authMode }} mode) — anyone
          reaching this URL has admin access. Set
          <code class="font-mono">AdaptiveApi:Auth:Mode</code> to
          <code class="font-mono">oidc</code> before exposing this instance to
          a network.
        </span>
      </div>
      <div class="p-6 flex-1">
        <RouterView />
      </div>
    </main>
    <CommandPalette />
  </div>
</template>
