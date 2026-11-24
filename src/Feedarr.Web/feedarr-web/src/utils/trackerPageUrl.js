// Only the dedicated, RSS-qualified detail URL is eligible. Never fall back to
// link/downloadUrl/guid: those may contain download credentials or point to Jackett.
export function getTrackerPageUrl(candidate) {
  if (typeof candidate !== "string" || !/^https?:\/\//i.test(candidate.trim())) return null;
  try {
    const url = new URL(candidate.trim());
    const host = url.hostname.toLowerCase();
    if (!host || url.username || url.password || url.href.includes("***") ||
        !host.includes(".") || /(?:^|\.)(?:localhost|local|internal)$/.test(host)) return null;
    const path = decodeURIComponent(url.pathname).toLowerCase().replace(/\/+$/, "");
    if (/\.torrent$|\/download|\/torznab|\/api\/v2\.0\/indexers|\/api\/v1\/indexer|\/search|\/\d+\/api(?:\/|$)/.test(path)) return null;
    for (const [name, value] of url.searchParams) {
      const key = name.toLowerCase().replace(/[-_]/g, "");
      if (/token|secret|password|auth|session|signature/.test(key) ||
          /key$/.test(key) || ["sig", "pwd", "pass", "download", "q", "query", "search"].includes(key) ||
          (key === "t" && /^(?:search|rss|recent)$/i.test(value)) ||
          (key === "action" && value.toLowerCase() === "download")) return null;
    }
    return url.href;
  } catch {
    return null;
  }
}
