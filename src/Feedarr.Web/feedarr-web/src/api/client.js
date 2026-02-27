const API_BASE = ((import.meta.env?.VITE_API_BASE) || "").replace(/\/+$/, "");

export function resolveApiUrl(path) {
  if (!path) return path;
  if (/^https?:\/\//i.test(path)) return path;
  if (path.startsWith("data:") || path.startsWith("blob:")) return path;
  if (!API_BASE) return path;
  if (path.startsWith("/")) return API_BASE + path;
  return API_BASE + "/" + path;
}

function extractExtensions(payload) {
  if (!payload || typeof payload !== "object") return {};
  const reserved = new Set(["type", "title", "status", "detail", "instance", "message", "error"]);
  const extensions = {};
  for (const [key, value] of Object.entries(payload)) {
    if (!reserved.has(key)) extensions[key] = value;
  }
  return extensions;
}

async function parseError(res) {
  let msg = `HTTP ${res.status}`;
  const ct = res.headers.get("content-type") || "";
  if (ct.includes("application/json")) {
    try {
      const data = await res.json();
      const detail = typeof data?.detail === "string" ? data.detail : "";
      const title = typeof data?.title === "string" ? data.title : "";
      const error = typeof data?.error === "string" ? data.error : "";
      msg = data?.message || error || detail || title || msg;
      return {
        message: msg,
        status: res.status,
        title,
        detail,
        error,
        requirements: data?.requirements,
        extensions: extractExtensions(data),
        payload: data,
      };
    } catch {}
    return { message: msg, status: res.status };
  }
  try {
    const text = await res.text();
    const cleaned = String(text || "").replace(/\s+/g, " ").trim();
    if (cleaned) {
      const snippet = cleaned.slice(0, 180);
      msg = `${msg} - ${snippet}`;
    }
  } catch {}
  return { message: msg, status: res.status };
}

function createApiError(parsed) {
  const error = new Error(parsed.message || "HTTP error");
  error.status = parsed.status;
  if (parsed.title) error.title = parsed.title;
  if (parsed.detail) error.detail = parsed.detail;
  if (parsed.error) error.error = parsed.error;
  if (parsed.requirements) error.requirements = parsed.requirements;
  if (parsed.extensions && Object.keys(parsed.extensions).length > 0) {
    error.extensions = parsed.extensions;
  }
  if (parsed.payload) error.payload = parsed.payload;
  return error;
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
    if (!res.ok) throw createApiError(await parseError(res));

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

    if (!res.ok) throw createApiError(await parseError(res));
    const ct = res.headers.get("content-type") || "";
    if (!ct.includes("application/json")) return null;
    return res.json();
  } finally {
    clear();
  }
}

export const apiPost   = (path, body, opts) => apiSend("POST", path, body, opts);
export const apiPut    = (path, body, opts) => apiSend("PUT", path, body, opts);
export const apiPatch  = (path, body, opts) => apiSend("PATCH", path, body, opts);
export const apiDelete = (path, opts)       => apiSend("DELETE", path, null, opts);
