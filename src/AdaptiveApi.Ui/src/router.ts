import { createRouter, createWebHistory, type RouteRecordRaw } from 'vue-router';

const routes: RouteRecordRaw[] = [
  { path: '/', redirect: '/dashboard' },
  { path: '/dashboard', name: 'dashboard', component: () => import('./pages/Dashboard.vue'),
    meta: { title: 'Dashboard' } },
  { path: '/wizard', name: 'wizard', component: () => import('./pages/Wizard.vue'),
    meta: { title: 'Set up your first route' } },
  { path: '/routes', name: 'routes', component: () => import('./pages/RoutesList.vue'),
    meta: { title: 'Routes' } },
  { path: '/route-profiles', name: 'route-profiles', component: () => import('./pages/RouteProfiles.vue'),
    meta: { title: 'Route profiles' } },
  { path: '/mcp', name: 'mcp', component: () => import('./pages/Mcp.vue'),
    meta: { title: 'MCP' } },
  // Legacy URLs from when servers / catalog were separate pages — keep
  // working so bookmarks and external doc links don't break.
  { path: '/mcp/servers', redirect: '/mcp' },
  { path: '/mcp/catalog', redirect: '/mcp' },
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
  { path: '/pii-rules', name: 'pii-rules', component: () => import('./pages/PiiRules.vue'),
    meta: { title: 'PII rules' } },
  { path: '/request-rules', name: 'request-rules', component: () => import('./pages/RequestRules.vue'),
    meta: { title: 'Request rules' } },
  { path: '/plugins', name: 'plugins', component: () => import('./pages/Plugins.vue'),
    meta: { title: 'Plugins' } },
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
