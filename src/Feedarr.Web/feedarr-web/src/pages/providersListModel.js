export function labelForProviderType(value) {
  const key = String(value || "").toLowerCase();
  if (key === "prowlarr") return "Prowlarr";
  return "Jackett";
}

export function normalizeProviderBaseUrl(value) {
  return String(value || "").trim().replace(/\/+$/, "");
}

export function maskProviderUrl(url) {
  if (!url) return "—";
  try {
    const u = new URL(url);
    const host = u.host;
    return `${u.protocol}//${host}/•••`;
  } catch {
    if (url.length <= 8) return "•••";
    return `${url.slice(0, 8)}•••`;
  }
}

export function formatProviderDateFromTs(tsSeconds) {
  const n = Number(tsSeconds);
  if (!Number.isFinite(n) || n <= 0) return "";
  const d = new Date(n * 1000);
  return d.toLocaleString("fr-FR", { dateStyle: "short", timeStyle: "short" });
}

export function buildProviderRows(items) {
  return (items || [])
    .map((p) => ({
      ...p,
      _name: p.name || labelForProviderType(p.type),
      _url: maskProviderUrl(p.baseUrl),
      _typeLabel: labelForProviderType(p.type),
      _lastTest: p.lastTestOkAt ? formatProviderDateFromTs(p.lastTestOkAt) : "",
    }))
    .sort((a, b) => (Number(a.id) || 0) - (Number(b.id) || 0));
}
