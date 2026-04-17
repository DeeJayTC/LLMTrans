import { defineConfig } from 'vite';
import vue from '@vitejs/plugin-vue';

export default defineConfig({
  plugins: [vue()],
  server: {
    port: 5174,
    proxy: {
      '/api': 'http://localhost:5100',
      '/healthz': 'http://localhost:5100',
    },
  },
  build: {
    outDir: 'dist',
    sourcemap: true,
  },
});
