const API_BASE = (import.meta.env.VITE_API_BASE || "").replace(/\/+$/, "");

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

async function apiSend(method, path, body) {
  const res = await fetch(resolveApiUrl(path), {
    method,
    headers: {
      Accept: "application/json",
      "Content-Type": "application/json",
    },
    credentials: "include",
    body: body ? JSON.stringify(body) : null,
  });

  if (res.status === 204) return null;
  if (!res.ok) throw new Error(await parseError(res));

  const ct = res.headers.get("content-type") || "";
  if (!ct.includes("application/json")) return null;
  return res.json();
}

export async function apiGet(path) {
  const res = await fetch(resolveApiUrl(path), {
    headers: { Accept: "application/json" },
    credentials: "include",
  });

  if (!res.ok) throw new Error(await parseError(res));
  const ct = res.headers.get("content-type") || "";
  if (!ct.includes("application/json")) return null;
  return res.json();
}

export const apiPost   = (path, body) => apiSend("POST", path, body);
export const apiPut    = (path, body) => apiSend("PUT", path, body);
export const apiDelete = (path)       => apiSend("DELETE", path);
