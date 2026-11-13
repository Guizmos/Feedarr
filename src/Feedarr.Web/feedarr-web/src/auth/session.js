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
function publish(next) {
  snapshot = next;
  for (const listener of listeners) listener();
  // A verified visible session also releases a mask left by an earlier lifecycle.
  // Error/retry screens are outside the private boundary and stay visible regardless.
  if (next.phase === "ready" && typeof document !== "undefined" && document.visibilityState === "visible") {
    document.documentElement.classList.remove("auth-locked");
  }
}

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

function invalidatePendingSession() {
  revision += 1;
  const previous = pending;
  pending = null;
  previous?.controller.abort(new DOMException("Session changed", "AbortError"));
}

// Bound both fetch and response-body consumption, even if an adapter ignores abort.
async function authDeadline(action, timeoutMs, controller = new AbortController()) {
  let aborted;
  const abort = new Promise((_, reject) => {
    aborted = () => reject(controller.signal.reason);
    controller.signal.addEventListener("abort", aborted, { once: true });
    if (controller.signal.aborted) aborted();
  });
  const timer = setTimeout(() => controller.abort(new DOMException("La vérification de connexion a dépassé le délai. Réessayez.", "TimeoutError")), timeoutMs);
  try { return await Promise.race([Promise.resolve().then(() => action(controller.signal)), abort]); }
  finally { clearTimeout(timer); controller.signal.removeEventListener("abort", aborted); }
}

export function lockSession() {
  invalidatePendingSession();
  publish({ phase: "checking", state: null, error: null });
}

export function refreshSession({ deniedKind } = {}) {
  if (pending) return pending.promise;
  const started = revision;
  const request = { controller: new AbortController(), promise: null };
  pending = request;
  request.promise = (async () => {
    try {
      const state = await authDeadline(async signal => {
        const response = await fetch(authUrl("session"), { credentials: "include", cache: "no-store", signal, headers: { Accept: "application/json", Authorization: "", "X-Feedarr-Web": "1" } });
        if (!response.ok) throw Object.assign(new Error("session_unavailable"), { status: response.status });
        return await response.json();
      }, 15000, request.controller);
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
    } finally { if (pending === request) pending = null; }
  })();
  return request.promise;
}

async function authPost(action, body) {
  return authDeadline(async signal => {
    const response = await fetch(authUrl(action), {
      method: "POST", credentials: "include", cache: "no-store", signal,
      headers: { Accept: "application/json", Authorization: "", "Content-Type": "application/json", "X-Feedarr-Web": "1", ...(snapshot.state?.csrfToken ? { "X-Feedarr-CSRF": snapshot.state.csrfToken } : {}) },
      body: JSON.stringify(body),
    });
    const payload = await response.json().catch(() => ({}));
    if (!response.ok) throw Object.assign(new Error(payload.code || payload.title || payload.error || "auth_failed"), { status: response.status, code: payload.code || payload.error, retryAfter: response.headers.get("Retry-After") || payload.retryAfterSeconds });
    return payload;
  }, 30000);
}

export async function login(username, password, rememberMe) {
  const started = revision;
  const state = await authPost("login", { username, password, rememberMe });
  if (started !== revision) throw new DOMException("Session changed", "AbortError");
  if (["authRequired", "authConfigured", "authenticated", "setupCompleted"].some(key => typeof state?.[key] !== "boolean")) throw new Error("invalid_session_response");
  invalidatePendingSession();
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
      // Setup owns the next operation: login for new credentials, one check for Open.
      // Never nest a session request inside its PUT or swallow verification failures.
      if (snapshot.state?.setupCompleted !== false) {
        lockSession();
        await refreshSession();
      }
    }
    return response;
  };
}

export function installSessionTransport() {
  window.fetch = createSessionFetch(window.fetch.bind(window), window.location.origin);
  const hasPrivateState = () => snapshot.state?.authenticated || snapshot.state?.setupCompleted !== false;
  const conceal = () => {
    if (!hasPrivateState()) return; // Anonymous setup contains no authenticated private state.
    document.documentElement.classList.add("auth-locked");
    lockSession();
  };
  const revalidate = () => {
    // pageshow and visibilitychange often arrive together. Share one verification.
    if (snapshot.phase !== "checking" && hasPrivateState()) lockSession();
    void refreshSession().catch(() => {});
  };
  if ("BroadcastChannel" in window) {
    channel = new BroadcastChannel("feedarr-auth");
    channel.onmessage = () => { conceal(); revalidate(); };
  }
  window.addEventListener("pagehide", conceal);
  window.addEventListener("pageshow", (event) => { if (event.persisted) revalidate(); });
  document.addEventListener("visibilitychange", () => {
    if (document.visibilityState === "visible") revalidate();
    else conceal();
  });
}
