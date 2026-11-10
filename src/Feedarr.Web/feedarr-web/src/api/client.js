import { isSsrfBlocked, ssrfBlockedMessage } from "../utils/ssrfErrors.js";

const API_BASE = ((import.meta.env?.VITE_API_BASE) || "").replace(/\/+$/, "");
const TRUSTED_REQUEST_HEADER = "X-Feedarr-Request";

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

async function parseError(res, signal) {
  let msg = `HTTP ${res.status}`;
  const ct = res.headers.get("content-type") || "";
  if (ct.includes("application/json") || ct.includes("+json")) {
    try {
      const data = await untilAborted(res.json(), signal);
      const detail = typeof data?.detail === "string" ? data.detail : "";
      const title = typeof data?.title === "string" ? data.title : "";
      const error = typeof data?.error === "string" ? data.error : "";
      // Prefer problem title for user-facing message, keep detail on the error object.
      msg = data?.message || title || error || detail || msg;
      return {
        message: msg,
        status: res.status,
        title,
        detail,
        error,
        code: typeof data?.code === "string" ? data.code : undefined,
        requirements: data?.requirements,
        extensions: extractExtensions(data),
        payload: data,
      };
    } catch (error) { if (signal.aborted) throw error; }
    return { message: msg, status: res.status };
  }
  try {
    const text = await untilAborted(res.text(), signal);
    const cleaned = String(text || "").replace(/\s+/g, " ").trim();
    if (cleaned) {
      const snippet = cleaned.slice(0, 180);
      msg = `${msg} - ${snippet}`;
    }
  } catch (error) { if (signal.aborted) throw error; }
  return { message: msg, status: res.status };
}

function createApiError(parsed) {
  const error = new Error(parsed.message || "HTTP error");
  error.status = parsed.status;
  if (parsed.code) error.code = parsed.code;
  if (parsed.title) error.title = parsed.title;
  if (parsed.detail) error.detail = parsed.detail;
  if (parsed.error) error.error = parsed.error;
  if (parsed.requirements) error.requirements = parsed.requirements;
  if (parsed.extensions && Object.keys(parsed.extensions).length > 0) {
    error.extensions = parsed.extensions;
  }
  if (parsed.payload) error.payload = parsed.payload;
  if (isSsrfBlocked(error)) error.message = ssrfBlockedMessage();
  return error;
}

const DEFAULT_TIMEOUT_MS = 30_000;

function untilAborted(promise, signal) {
  return new Promise((resolve, reject) => {
    const cleanup = () => signal.removeEventListener("abort", aborted);
    const aborted = () => { cleanup(); reject(signal.reason); };
    if (signal.aborted) {
      Promise.resolve(promise).catch(() => {});
      reject(signal.reason);
      return;
    }
    signal.addEventListener("abort", aborted, { once: true });
    Promise.resolve(promise).then(
      value => { cleanup(); resolve(value); },
      error => { cleanup(); reject(error); }
    );
  });
}

function withTimeout(externalSignal, timeoutMs) {
  const ms = timeoutMs > 0 ? timeoutMs : DEFAULT_TIMEOUT_MS;
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(new DOMException("API request timed out", "TimeoutError")), ms);
  const aborted = () => {
    clearTimeout(timer);
    controller.abort(externalSignal.reason);
  };
  if (externalSignal?.aborted) aborted();
  else externalSignal?.addEventListener("abort", aborted, { once: true });
  return {
    signal: controller.signal,
    clear: () => {
      clearTimeout(timer);
      externalSignal?.removeEventListener("abort", aborted);
    },
  };
}

async function apiSend(method, path, body, { signal, timeoutMs } = {}) {
  const { signal: merged, clear } = withTimeout(signal, timeoutMs);
  try {
    if (merged.aborted) throw merged.reason;
    const res = await untilAborted(fetch(resolveApiUrl(path), {
      method,
      headers: {
        Accept: "application/json",
        "Content-Type": "application/json",
        [TRUSTED_REQUEST_HEADER]: "1",
      },
      credentials: "include",
      body: body ? JSON.stringify(body) : null,
      signal: merged,
    }), merged);

    if (res.status === 204) return null;
    if (!res.ok) throw createApiError(await parseError(res, merged));

    const ct = res.headers.get("content-type") || "";
    if (!ct.includes("application/json") && !ct.includes("+json")) return null;
    return await untilAborted(res.json(), merged);
  } finally {
    clear();
  }
}

export async function apiGet(path, { signal, timeoutMs } = {}) {
  const { signal: merged, clear } = withTimeout(signal, timeoutMs);
  try {
    if (merged.aborted) throw merged.reason;
    const res = await untilAborted(fetch(resolveApiUrl(path), {
      headers: { Accept: "application/json" },
      credentials: "include",
      signal: merged,
    }), merged);

    if (!res.ok) throw createApiError(await parseError(res, merged));
    const ct = res.headers.get("content-type") || "";
    if (!ct.includes("application/json") && !ct.includes("+json")) return null;
    return await untilAborted(res.json(), merged);
  } finally {
    clear();
  }
}

export const apiPost   = (path, body, opts) => apiSend("POST", path, body, opts);
export const apiPut    = (path, body, opts) => apiSend("PUT", path, body, opts);
export const apiPatch  = (path, body, opts) => apiSend("PATCH", path, body, opts);
export const apiDelete = (path, opts)       => apiSend("DELETE", path, null, opts);
