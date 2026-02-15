const API_BASE = ((import.meta.env?.VITE_API_BASE) || "").replace(/\/+$/, "");

export function resolveApiUrl(path) {
  if (!path) return path;
  if (/^https?:\/\//i.test(path)) return path;
  if (path.startsWith("data:") || path.startsWith("blob:")) return path;
  if (!API_BASE) return path;
  if (path.startsWith("/")) return API_BASE + path;
  return API_BASE + "/" + path;
}

async function parseError(res) {
  let msg = `HTTP ${res.status}`;
  const ct = res.headers.get("content-type") || "";
  if (ct.includes("application/json")) {
    try {
      const data = await res.json();
      msg = data?.message || data?.error || data?.title || data?.detail || msg;
    } catch {}
    return msg;
  }
  try {
    const text = await res.text();
    const cleaned = String(text || "").replace(/\s+/g, " ").trim();
    if (cleaned) {
      const snippet = cleaned.slice(0, 180);
      msg = `${msg} - ${snippet}`;
    }
  } catch {}
  return msg;
}

const DEFAULT_TIMEOUT_MS = 30_000;

function withTimeout(externalSignal, timeoutMs) {
  const ms = timeoutMs > 0 ? timeoutMs : DEFAULT_TIMEOUT_MS;
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), ms);

  if (externalSignal) {
    if (externalSignal.aborted) { clearTimeout(timer); controller.abort(); }
    else externalSignal.addEventListener("abort", () => { clearTimeout(timer); controller.abort(); }, { once: true });
  }

  return { signal: controller.signal, clear: () => clearTimeout(timer) };
}

async function apiSend(method, path, body, { signal, timeoutMs } = {}) {
  const { signal: merged, clear } = withTimeout(signal, timeoutMs);
  try {
    const res = await fetch(resolveApiUrl(path), {
      method,
      headers: {
        Accept: "application/json",
        "Content-Type": "application/json",
      },
      credentials: "include",
      body: body ? JSON.stringify(body) : null,
      signal: merged,
    });

    if (res.status === 204) return null;
    if (!res.ok) throw new Error(await parseError(res));

    const ct = res.headers.get("content-type") || "";
    if (!ct.includes("application/json")) return null;
    return res.json();
  } finally {
    clear();
  }
}

export async function apiGet(path, { signal, timeoutMs } = {}) {
  const { signal: merged, clear } = withTimeout(signal, timeoutMs);
  try {
    const res = await fetch(resolveApiUrl(path), {
      headers: { Accept: "application/json" },
      credentials: "include",
      signal: merged,
    });

    if (!res.ok) throw new Error(await parseError(res));
    const ct = res.headers.get("content-type") || "";
    if (!ct.includes("application/json")) return null;
    return res.json();
  } finally {
    clear();
  }
}

export const apiPost   = (path, body, opts) => apiSend("POST", path, body, opts);
export const apiPut    = (path, body, opts) => apiSend("PUT", path, body, opts);
export const apiDelete = (path, opts)       => apiSend("DELETE", path, null, opts);
