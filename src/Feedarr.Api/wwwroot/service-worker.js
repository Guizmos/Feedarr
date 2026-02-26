const CACHE_NAME = "feedarr-pwa-v3";
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
  scopedPath("/manifest.webmanifest"),
  scopedPath("/favicon.png"),
  scopedPath("/favicon-32.png"),
  scopedPath("/favicon-ios.png"),
  scopedPath("/icon-192.png"),
  scopedPath("/icon-512.png"),
];

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
    APP_SHELL.includes(url.pathname);
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
