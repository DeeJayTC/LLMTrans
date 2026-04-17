<script setup lang="ts">
import { computed } from 'vue';
import { RouterLink, RouterView, useRoute } from 'vue-router';

const route = useRoute();
const pageTitle = computed(() => (route.meta.title as string) ?? 'llmtrans');

const nav = [
  { to: '/dashboard', label: 'Dashboard', icon: 'i-carbon-dashboard' },
  { to: '/routes', label: 'Routes', icon: 'i-carbon-flow' },
  { to: '/mcp', label: 'MCP servers', icon: 'i-carbon-plug' },
  { to: '/mcp/catalog', label: 'MCP catalog', icon: 'i-carbon-catalog' },
  { to: '/glossaries', label: 'Glossaries', icon: 'i-carbon-book' },
  { to: '/style-rules', label: 'Style rules', icon: 'i-carbon-pen' },
  { to: '/proxy-rules', label: 'Proxy rules', icon: 'i-carbon-rule' },
  { to: '/playground', label: 'Playground', icon: 'i-carbon-play' },
  { to: '/logs', label: 'Logs', icon: 'i-carbon-list-boxes' },
  { to: '/settings', label: 'Settings', icon: 'i-carbon-settings' },
];
</script>

<template>
  <div class="min-h-screen grid grid-cols-[240px_1fr]">
    <aside class="border-r border-surface-200 bg-surface-0 px-3 py-4 flex flex-col gap-1">
      <div class="px-3 pb-4 flex items-center gap-2 text-brand-600 font-600 text-lg">
        <span class="i-carbon-translate text-xl" />
        <span>llmtrans</span>
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
        <div class="text-xs text-surface-700">
          <span class="chip">dev</span>
        </div>
      </header>
      <div class="p-6 flex-1">
        <RouterView />
      </div>
    </main>
  </div>
</template>
