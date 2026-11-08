// Comparison only: never use this key as a request URL or a security decision.
export function normalizeIntegrationUrl(value) {
  const raw = String(value || "").trim();
  if (!raw) return null;
  const explicitHttp = /^https?:\/\//i.test(raw);
  if (!explicitHttp && raw.includes("://")) return null;

  try {
    const url = new URL(explicitHttp ? raw : `http://${raw}`);
    if (url.username || url.password || url.hash) return null;
    const port = url.port || (url.protocol === "https:" ? "443" : "80");
    const path = url.pathname.replace(/\/+$/, "").replace(/%[a-f0-9]{2}/gi, (encoded) => {
      const character = String.fromCharCode(Number.parseInt(encoded.slice(1), 16));
      return /^[a-z0-9._~-]$/i.test(character) ? character : encoded.toUpperCase();
    });
    return `${url.protocol}//${url.hostname.toLowerCase()}:${port}${path}${url.search}`;
  } catch {
    return null;
  }
}

export function findPersistedIntegration(records, { id, type, url, urlField = "baseUrl" }) {
  const candidates = (Array.isArray(records) ? records : []).filter((record) =>
    record && (type == null || String(record.type).toLowerCase() === String(type).toLowerCase())
  );
  // A known ID is authoritative, including when the form URL has been edited.
  if (id != null && id !== "") {
    return candidates.find((record) => String(record.id) === String(id));
  }
  const key = normalizeIntegrationUrl(url);
  if (!key) return undefined;
  const matches = candidates.filter((record) => normalizeIntegrationUrl(record[urlField]) === key);
  return matches.length === 1 ? matches[0] : undefined;
}
