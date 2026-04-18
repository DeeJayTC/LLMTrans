import { build } from 'esbuild';

await build({
  entryPoints: ['src/cli.ts'],
  bundle: true,
  platform: 'node',
  target: 'node18',
  format: 'esm',
  outfile: 'dist/cli.js',
  sourcemap: true,
  minify: false,
  banner: {
    // Keeps `npx @adaptiveapi/mcp-bridge` ergonomic without a separate shebang script.
    js: '#!/usr/bin/env node',
  },
});

console.log('bridge bundled → dist/cli.js');
