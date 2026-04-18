import { createRouter, createWebHistory, type RouteRecordRaw } from 'vue-router';

const routes: RouteRecordRaw[] = [
  { path: '/', redirect: '/dashboard' },
  { path: '/dashboard', name: 'dashboard', component: () => import('./pages/Dashboard.vue'),
    meta: { title: 'Dashboard' } },
  { path: '/routes', name: 'routes', component: () => import('./pages/RoutesList.vue'),
    meta: { title: 'Routes' } },
  { path: '/mcp', name: 'mcp', component: () => import('./pages/McpServers.vue'),
    meta: { title: 'MCP servers' } },
  { path: '/mcp/catalog', name: 'mcp-catalog', component: () => import('./pages/McpCatalog.vue'),
    meta: { title: 'MCP catalog' } },
  { path: '/glossaries', name: 'glossaries', component: () => import('./pages/Glossaries.vue'),
    meta: { title: 'Glossaries' } },
  { path: '/glossaries/:id', name: 'glossary-detail', component: () => import('./pages/GlossaryDetail.vue'),
    meta: { title: 'Glossary' } },
  { path: '/style-rules', name: 'style-rules', component: () => import('./pages/StyleRules.vue'),
    meta: { title: 'Style rules' } },
  { path: '/style-rules/:id', name: 'style-rule-detail', component: () => import('./pages/StyleRuleDetail.vue'),
    meta: { title: 'Style rule' } },
  { path: '/proxy-rules', name: 'proxy-rules', component: () => import('./pages/ProxyRules.vue'),
    meta: { title: 'Proxy rules' } },
  { path: '/playground', name: 'playground', component: () => import('./pages/Playground.vue'),
    meta: { title: 'Playground' } },
  { path: '/logs', name: 'logs', component: () => import('./pages/Logs.vue'),
    meta: { title: 'Logs' } },
  { path: '/settings', name: 'settings', component: () => import('./pages/Settings.vue'),
    meta: { title: 'Settings' } },
];

export const router = createRouter({
  history: createWebHistory(),
  routes,
});
