// Theme toggle: prefers stored choice, falls back to system preference.
(function () {
  const root = document.documentElement;
  const stored = localStorage.getItem('adaptiveapi-theme');
  const prefersDark = window.matchMedia('(prefers-color-scheme: dark)').matches;
  const initial = stored || (prefersDark ? 'dark' : 'light');
  root.setAttribute('data-theme', initial);

  const toggle = document.querySelector('[data-theme-toggle]');
  if (toggle) {
    toggle.addEventListener('click', function () {
      const current = root.getAttribute('data-theme') === 'dark' ? 'dark' : 'light';
      const next = current === 'dark' ? 'light' : 'dark';
      root.setAttribute('data-theme', next);
      localStorage.setItem('adaptiveapi-theme', next);
    });
  }
})();

// Code tabs.
(function () {
  document.querySelectorAll('[data-tabs]').forEach(function (root) {
    const tabs = root.querySelectorAll('[data-tab]');
    const panels = root.querySelectorAll('[data-panel]');
    tabs.forEach(function (tab) {
      tab.addEventListener('click', function () {
        const target = tab.getAttribute('data-tab');
        tabs.forEach(function (t) { t.classList.toggle('is-active', t === tab); });
        panels.forEach(function (p) {
          p.classList.toggle('is-active', p.getAttribute('data-panel') === target);
        });
      });
    });
  });
})();
