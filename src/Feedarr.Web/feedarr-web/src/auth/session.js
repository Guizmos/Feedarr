// Session/CSRF state stays in memory. Never retain usernames, passwords or bearer cookies.
let snapshot = { phase: "checking", state: null, error: null };
const listeners = new Set();
let revision = 0;
let pending = null;
let channel;
const base = (import.meta.env?.BASE_URL || "/").replace(/\/$/, "");
const authUrl = (action) => `${base}/api/auth/${action}`;
export const getSessionSnapshot = () => snapshot;
export const subscribeSession = (listener) => { listeners.add(listener); return () => listeners.delete(listener); };
function publish(next) { snapshot = next; for (const listener of listeners) listener(); }

export function sessionRoute(state) {
  if (typeof state?.setupCompleted !== "boolean") return "checking";
  if (!state.setupCompleted) return "setup";
  if (["authRequired", "authConfigured", "authenticated"].some((key) => typeof state[key] !== "boolean")) return "checking";
  if (state.authRequired && state.authConfigured && !state.authenticated) return "login";
  if (state.authRequired && !state.authConfigured) return "configuration";
  return "app";
}

// Only the auth gate owns route transitions. Unknown state must never imply a route.
export function sessionRedirect(state, pathname, search = "", hash = "") {
  const route = sessionRoute(state);
  if (route === "setup" && pathname !== "/setup") return "/setup";
  if (route === "login" && pathname !== "/login") return `/login?returnTo=${encodeURIComponent(pathname + search + hash)}`;
  if (route === "app" && pathname === "/setup") return "/library";
  return null;
}

export function authDenialKind(status, payload = {}) {
  const code = payload.error || payload.code || payload.title;
  if (code === "setup_required") return "setup";
  if (code === "security_setup_required") return "configuration";
  if (status === 401) return "unauthenticated";
  return "forbidden";
}

async function recheckAfterDenial(kind) {
  // Setup APIs may legitimately reject requests pending bootstrap/credentials.
  // Do not lock/remount known incomplete setup for expected API denials.
  if (snapshot.state?.setupCompleted === false) {
    // Revoke stale session/configuration state in place, without unmounting setup.
    if ((kind === "unauthenticated" && snapshot.state.authenticated) ||
        (kind === "configuration" && snapshot.state.authConfigured)) {
      try { await refreshSession(); } catch { /* Stable error/retry state. */ }
    }
    return;
  }
  if (snapshot.state?.setupCompleted !== true || kind === "forbidden") return;
  lockSession();
  try { await refreshSession({ deniedKind: kind }); }
  catch { /* refreshSession exposes the stable retry/error state. */ }
}

export function safeDestination(value) {
  if (typeof value !== "string" || !value.startsWith("/") || value.startsWith("//") || value.includes("\\") || Array.from(value).some((char) => char.charCodeAt(0) < 32)) return "/library";
  try {
    const url = new URL(value, "https://feedarr.invalid");
    const decoded = decodeURIComponent(url.pathname);
    if (url.origin !== "https://feedarr.invalid" || decoded.startsWith("//") || decoded.includes("\\") || /^\/(login|setup)(\/|$)/i.test(decoded)) return "/library";
    return url.pathname + url.search + url.hash;
  } catch { return "/library"; }
}

export function appUrl(path) { return `${base}${path}`; }

export function lockSession() {
  revision += 1;
  pending = null;
  publish({ phase: "checking", state: null, error: null });
}

export async function refreshSession({ deniedKind } = {}) {
  if (pending) return pending;
  const started = revision;
  pending = (async () => {
    try {
      const response = await fetch(authUrl("session"), { credentials: "include", cache: "no-store", signal: AbortSignal.timeout(15000), headers: { Accept: "application/json", Authorization: "", "X-Feedarr-Web": "1" } });
      if (!response.ok) throw Object.assign(new Error("session_unavailable"), { status: response.status });
      const state = await response.json();
      if (["authRequired", "authConfigured", "authenticated", "setupCompleted"].some((key) => typeof state?.[key] !== "boolean")) throw new Error("invalid_session_response");
      const contradictsDenial = (deniedKind === "setup" && state.setupCompleted) ||
        (deniedKind === "configuration" && state.authConfigured) ||
        (deniedKind === "unauthenticated" && sessionRoute(state) === "app");
      if (contradictsDenial) throw new Error("inconsistent_authorization_state");
      if (started === revision) publish({ phase: "ready", state, error: null });
      return state;
    } catch (error) {
      if (started === revision) publish({ phase: "error", state: null, error });
      throw error;
    } finally { if (started === revision) pending = null; }
  })();
  return pending;
}

async function authPost(action, body) {
  const response = await fetch(authUrl(action), {
    method: "POST", credentials: "include", cache: "no-store", signal: AbortSignal.timeout(30000),
    headers: { Accept: "application/json", Authorization: "", "Content-Type": "application/json", "X-Feedarr-Web": "1", ...(snapshot.state?.csrfToken ? { "X-Feedarr-CSRF": snapshot.state.csrfToken } : {}) },
    body: JSON.stringify(body),
  });
  const payload = await response.json().catch(() => ({}));
  if (!response.ok) throw Object.assign(new Error(payload.code || payload.title || payload.error || "auth_failed"), { status: response.status, code: payload.code || payload.error, retryAfter: response.headers.get("Retry-After") || payload.retryAfterSeconds });
  return payload;
}

export async function login(username, password, rememberMe) {
  const state = await authPost("login", { username, password, rememberMe });
  revision += 1;
  publish({ phase: "ready", state, error: null });
  return state;
}

export async function logout() {
  await authPost("logout", {});
  lockSession();
  channel?.postMessage("logout");
  // Full navigation discards private React/module state; preferences are retained.
  window.location.replace(appUrl("/login"));
}

// Covers shared API helpers and legacy direct fetch callers without changing their contracts.
// Never attach session headers to external URLs, static resources or explicit Basic calls.
export function createSessionFetch(nativeFetch, origin, root = base) {
  return async (input, init = {}) => {
    const url = new URL(typeof input === "string" || input instanceof URL ? input : input.url, origin);
    const apiPath = `${root}/api/`;
    if (url.origin !== origin || !url.pathname.startsWith(apiPath)) return nativeFetch(input, init);
    const headers = new Headers(init.headers || (input instanceof Request ? input.headers : undefined));
    if (headers.get("Authorization")?.trim()) return nativeFetch(input, init);
    const action = url.pathname.slice(apiPath.length);
    const isAuth = action.startsWith("auth/");
    const method = (init.method || (input instanceof Request ? input.method : "GET")).toUpperCase();
    // An explicitly empty header prevents Fetch from adding cached HTTP Basic
    // credentials while still allowing the HttpOnly session cookie.
    headers.set("Authorization", "");
    headers.set("X-Feedarr-Web", "1");
    if (!["GET", "HEAD", "OPTIONS"].includes(method) && snapshot.state?.csrfToken) headers.set("X-Feedarr-CSRF", snapshot.state.csrfToken);
    const started = revision;
    const response = await nativeFetch(input, { ...init, headers });
    if (!isAuth && started !== revision) throw new DOMException("Session changed", "AbortError");
    if (!isAuth && (response.status === 401 || response.status === 403)) {
      const denial = await response.clone().json().catch(() => ({}));
      void recheckAfterDenial(authDenialKind(response.status, denial));
    } else if (!isAuth && response.ok && method !== "GET" && action === "settings/security") {
      // Keep the incomplete wizard mounted while its security form opens a session.
      if (snapshot.state?.setupCompleted !== false) lockSession();
      await refreshSession().catch(() => {});
    }
    return response;
  };
}

export function installSessionTransport() {
  window.fetch = createSessionFetch(window.fetch.bind(window), window.location.origin);
  if ("BroadcastChannel" in window) {
    channel = new BroadcastChannel("feedarr-auth");
    channel.onmessage = () => { lockSession(); void refreshSession().catch(() => {}); };
  }
  window.addEventListener("pagehide", () => {
    document.documentElement.classList.add("auth-locked");
    lockSession();
  });
  const revalidate = () => {
    lockSession();
    void refreshSession().finally(() => document.documentElement.classList.remove("auth-locked")).catch(() => {});
  };
  window.addEventListener("pageshow", (event) => { if (event.persisted) revalidate(); });
  document.addEventListener("visibilitychange", () => {
    if (document.visibilityState === "visible") revalidate();
    else { document.documentElement.classList.add("auth-locked"); lockSession(); }
  });
}
