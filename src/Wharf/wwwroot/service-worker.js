importScripts('/_service-worker-assets');

const CACHE = 'wharf-shell-v6';

const ASSETS = [...new Set((self.wharfAssets || []).map((url) => url.replace(/\\/g, '/')))].filter(
  (url) => !url.endsWith('/service-worker-assets.js'),
);

self.addEventListener('install', (event) => {
  event.waitUntil(
    (async () => {
      const cache = await caches.open(CACHE);

      // Cache each asset independently so one missing file cannot abort the
      // whole install.
      await Promise.all(
        ASSETS.map(async (url) => {
          try {
            const response = await fetch(url, { cache: 'reload' });
            if (response.ok) await cache.put(url, response);
          } catch {
            // A missing asset (for example a development-only file) is skipped.
          }
        }),
      );

      try {
        const shell = await fetch('/', { cache: 'reload' });
        if (shell.ok) await cache.put('/', shell);
      } catch {
      }

      await self.skipWaiting();
    })(),
  );
});

self.addEventListener('activate', (event) => {
  event.waitUntil(
    caches.keys()
      .then((keys) => Promise.all(
        keys.filter((key) => key !== CACHE).map((key) => caches.delete(key)),
      ))
      .then(() => self.clients.claim()),
  );
});

self.addEventListener('fetch', (event) => {
  const url = new URL(event.request.url);

  if (event.request.method !== 'GET') return;
  if (url.origin !== self.location.origin) return;
  if (url.pathname.startsWith('/api/')) return;

  // Navigations: network first, then the shell that was rendered last time.
  if (event.request.mode === 'navigate') {
    event.respondWith(
      fetch(event.request)
        .then((response) => {
          const copy = response.clone();
          caches.open(CACHE).then((cache) => cache.put('/', copy)).catch(() => {});
          return response;
        })
        .catch(() => caches.match('/')),
    );
    return;
  }

  // Framework files and static assets: cache first, fill on first use.
  event.respondWith(
    caches.match(event.request).then((hit) => {
      if (hit) return hit;

      return fetch(event.request).then((response) => {
        if (response.ok) {
          const copy = response.clone();
          caches.open(CACHE).then((cache) => cache.put(event.request, copy)).catch(() => {});
        }
        return response;
      });
    }),
  );
});
