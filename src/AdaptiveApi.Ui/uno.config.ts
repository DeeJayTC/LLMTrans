import { defineConfig, presetUno, presetIcons } from 'unocss';

export default defineConfig({
  presets: [presetUno(), presetIcons()],
  theme: {
    colors: {
      brand: {
        50:  '#f0f6ff',
        100: '#dfe9ff',
        500: '#2d5bff',
        600: '#1d47e8',
        700: '#1636b8',
      },
      surface: {
        0:   '#ffffff',
        50:  '#fafbfc',
        100: '#f1f3f7',
        200: '#e4e8ee',
        300: '#c7cfdb',
        700: '#4a5463',
        900: '#171c24',
      },
    },
    fontFamily: {
      sans: '"Inter","SF Pro Text","Segoe UI",sans-serif',
      mono: '"JetBrains Mono",ui-monospace,monospace',
    },
  },
  shortcuts: {
    'btn': 'inline-flex items-center justify-center gap-2 rounded-md border border-surface-200 bg-surface-0 px-3 py-1.5 text-sm font-medium text-surface-900 hover:bg-surface-50 disabled:opacity-50 disabled:cursor-not-allowed',
    'btn-primary': 'inline-flex items-center justify-center gap-2 rounded-md bg-brand-500 px-3 py-1.5 text-sm font-medium text-white hover:bg-brand-600 disabled:opacity-50 disabled:cursor-not-allowed',
    'btn-danger': 'inline-flex items-center justify-center gap-2 rounded-md border border-red-200 bg-white px-3 py-1.5 text-sm font-medium text-red-700 hover:bg-red-50',
    'input': 'block w-full rounded-md border border-surface-200 bg-surface-0 px-3 py-1.5 text-sm outline-none focus:border-brand-500 focus:ring-2 focus:ring-brand-100',
    'card': 'rounded-lg border border-surface-200 bg-surface-0 shadow-sm',
    'card-header': 'flex items-center justify-between border-b border-surface-200 px-4 py-3 font-medium',
    'card-body': 'p-4',
    'chip': 'inline-flex items-center rounded-full bg-surface-100 px-2 py-0.5 text-xs font-medium text-surface-700',
    'chip-primary': 'inline-flex items-center rounded-full bg-brand-50 px-2 py-0.5 text-xs font-medium text-brand-700',
  },
});
