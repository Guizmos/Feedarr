const CACHE_NAME = "feedarr-pwa-v5";
const SCOPE_PATH = (() => {
  const scopePath = new URL(self.registration.scope).pathname;
  return scopePath !== "/" && scopePath.endsWith("/")
    ? scopePath.slice(0, -1)
    : (scopePath === "/" ? "" : scopePath);
})();

function scopedPath(path) {
  const normalized = path.startsWith("/") ? path : `/${path}`;
  const full = `${SCOPE_PATH}${normalized}`;
  return full || "/";
}

const APP_SHELL = [
  scopedPath("/"),
  scopedPath("/index.html"),
  scopedPath("/manifest.webmanifest?v=9d1d5be2bd5c"),
  scopedPath("/favicon.png?v=9d1d5be2bd5c"),
  scopedPath("/favicon-32.png?v=9d1d5be2bd5c"),
  scopedPath("/favicon-ios.png?v=9d1d5be2bd5c"),
  scopedPath("/icon-192.png?v=9d1d5be2bd5c"),
  scopedPath("/icon-512.png?v=9d1d5be2bd5c"),
  scopedPath("/icon-maskable-512.png?v=9d1d5be2bd5c"),
  scopedPath("/feedarr-logo.png?v=9d1d5be2bd5c"),
];
const APP_SHELL_PATHS = new Set(APP_SHELL.map((path) => new URL(path, self.location.origin).pathname));

self.addEventListener("install", (event) => {
  event.waitUntil(
    caches
      .open(CACHE_NAME)
      .then((cache) => cache.addAll(APP_SHELL))
      .catch(() => undefined)
      .then(() => self.skipWaiting())
  );
});

self.addEventListener("activate", (event) => {
  event.waitUntil(
    caches
      .keys()
      .then((keys) =>
        Promise.all(
          keys
            .filter((key) => key.startsWith("feedarr-pwa-") && key !== CACHE_NAME)
            .map((key) => caches.delete(key))
        )
      )
      .then(() => self.clients.claim())
  );
});

self.addEventListener("fetch", (event) => {
  const { request } = event;
  if (request.method !== "GET") return;

  const url = new URL(request.url);
  if (url.origin !== self.location.origin) return;
  if (url.pathname.startsWith("/api/") || url.pathname.startsWith(scopedPath("/api/"))) return;

  if (request.mode === "navigate") {
    event.respondWith(
      fetch(request)
        .then((response) => {
          if (response && response.ok) {
            const responseClone = response.clone();
            caches.open(CACHE_NAME).then((cache) => cache.put(request, responseClone));
          }
          return response;
        })
        .catch(() => caches.match(request) || caches.match(scopedPath("/index.html")) || caches.match("/index.html"))
    );
    return;
  }

  const isStaticAsset =
    url.pathname.startsWith("/assets/") ||
    url.pathname.startsWith(scopedPath("/assets/")) ||
    APP_SHELL_PATHS.has(url.pathname);
  if (!isStaticAsset) return;

  event.respondWith(
    fetch(request)
      .then((response) => {
        if (response && response.ok) {
          const responseClone = response.clone();
          caches.open(CACHE_NAME).then((cache) => cache.put(request, responseClone));
        }
        return response;
      })
      .catch(() => caches.match(request))
  );
});
