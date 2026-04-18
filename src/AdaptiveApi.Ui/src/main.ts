import { createApp } from 'vue';
import { createPinia } from 'pinia';
import '@unocss/reset/tailwind.css';
import 'virtual:uno.css';
import './styles.css';

import App from './App.vue';
import { router } from './router';

const app = createApp(App);
app.use(createPinia());
app.use(router);
app.mount('#app');
