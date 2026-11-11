// Only public, anonymous build assets live in this cache. API/private responses never do.
const BUILD = new URL(self.location.href).searchParams.get("v") || "unversioned";
const CACHE_NAME = `feedarr-pwa-auth-v6-${BUILD}`;
const SCOPE_PATH = new URL(self.registration.scope).pathname.replace(/\/$/, "");
const scopedPath = (path) => `${SCOPE_PATH}/${path.replace(/^\//, "")}`;
const PUBLIC_FILES = [
  "index.html", "manifest.webmanifest?v=9d1d5be2bd5c", "favicon.png?v=9d1d5be2bd5c",
  "favicon-32.png?v=9d1d5be2bd5c", "favicon-ios.png?v=9d1d5be2bd5c",
  "icon-192.png?v=9d1d5be2bd5c", "icon-512.png?v=9d1d5be2bd5c",
  "icon-maskable-512.png?v=9d1d5be2bd5c", "feedarr-logo.png?v=9d1d5be2bd5c",
].map(scopedPath);
const PUBLIC_PATHS = new Set(PUBLIC_FILES.map((path) => new URL(path, self.location.origin).pathname));
function cacheable(response) {
  return response?.ok && !response.redirected && !/no-store|private/i.test(response.headers.get("Cache-Control") || "");
}
async function publicFetch(path) {
  return fetch(path, { credentials: "omit", cache: "no-store", redirect: "error" });
}
self.addEventListener("install", (event) => {
  event.waitUntil((async () => {
    const manifest = await publicFetch(scopedPath("pwa-assets.json"));
    if (!manifest.ok) throw new Error("Public build manifest unavailable");
    const assets = await manifest.json();
    if (!Array.isArray(assets) || assets.some((path) => !/^assets\/[A-Za-z0-9_.-]+\.(js|css|woff2?|png|svg)$/.test(path))) throw new Error("Invalid public build manifest");
    const cache = await caches.open(CACHE_NAME);
    await Promise.all([...PUBLIC_FILES, ...assets.map(scopedPath)].map(async (path) => {
      const response = await publicFetch(path);
      if (!cacheable(response) || (path === scopedPath("index.html") && response.headers.get("X-Feedarr-Shell") !== "public-v1")) throw new Error("Non-public precache response");
      await cache.put(path, response);
    }));
    await self.skipWaiting();
  })());
});
self.addEventListener("activate", (event) => {
  event.waitUntil((async () => {
    await Promise.all((await caches.keys()).filter((key) => key.startsWith("feedarr-pwa-") && key !== CACHE_NAME).map((key) => caches.delete(key)));
    await self.clients.claim();
  })());
});
self.addEventListener("fetch", (event) => {
  const { request } = event;
  if (request.method !== "GET") return;
  const url = new URL(request.url);
  if (url.origin !== self.location.origin || url.pathname.startsWith("/api/") || url.pathname === "/api" || url.pathname.startsWith(scopedPath("api/")) || url.pathname === scopedPath("api")) return;
  if (request.mode === "navigate") {
    event.respondWith((async () => {
      try {
        // HTML is neutral. Never send cookies/Basic or cache a navigation response.
        return await publicFetch(request);
      } catch {
        const cache = await caches.open(CACHE_NAME);
        const shell = await cache.match(scopedPath("index.html"));
        if (shell) return shell;
        return new Response("<!doctype html><html lang='en'><meta name='viewport' content='width=device-width'><title>Feedarr</title><h1>Feedarr</h1><p>Hors ligne / Offline. Reconnectez-vous puis actualisez / Reconnect and reload.</p></html>", { status: 503, headers: { "Content-Type": "text/html; charset=utf-8", "Cache-Control": "no-store" } });
      }
    })());
    return;
  }
  if (!url.pathname.startsWith(scopedPath("assets/")) && !PUBLIC_PATHS.has(url.pathname)) return;
  event.respondWith((async () => {
    const cache = await caches.open(CACHE_NAME);
    try {
      const response = await publicFetch(request);
      if (cacheable(response) && (url.pathname !== scopedPath("index.html") || response.headers.get("X-Feedarr-Shell") === "public-v1")) await cache.put(request, response.clone());
      return response;
    } catch {
      return await cache.match(request) || Response.error();
    }
  })());
});
