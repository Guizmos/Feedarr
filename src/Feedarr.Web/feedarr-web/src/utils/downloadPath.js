import { resolveApiUrl } from "../api/client.js";

const IFRAME_CLEANUP_MS = 30_000;

function reportDownloadError(onError, message, error) {
  console.error(message, error);
  if (typeof onError === "function") {
    onError(message);
  }
}

function triggerDownloadWithIframe(url) {
  if (typeof document === "undefined" || !document.body) {
    throw new Error("document.body indisponible");
  }

  const iframe = document.createElement("iframe");
  iframe.style.display = "none";
  iframe.setAttribute("aria-hidden", "true");
  iframe.src = url;

  document.body.appendChild(iframe);

  const timeout = typeof window !== "undefined" ? window.setTimeout : setTimeout;
  timeout(() => {
    if (iframe.parentNode) {
      iframe.parentNode.removeChild(iframe);
    }
  }, IFRAME_CLEANUP_MS);

  return true;
}

function triggerDownloadWithAnchor(url) {
  if (typeof document === "undefined" || !document.body) {
    throw new Error("document.body indisponible");
  }

  const link = document.createElement("a");
  link.href = url;
  link.target = "_self";
  link.rel = "noopener";
  link.style.display = "none";

  document.body.appendChild(link);
  try {
    link.click();
  } finally {
    if (link.parentNode) {
      link.parentNode.removeChild(link);
    }
  }

  return true;
}

export function openDownloadPath(downloadPath, options = {}) {
  const { onError } = options;

  if (typeof window === "undefined") {
    return false;
  }

  try {
    const path = String(downloadPath || "").trim();
    if (!path) {
      reportDownloadError(onError, "URL de telechargement manquante.", null);
      return false;
    }
    if (!path.startsWith("/api/")) {
      reportDownloadError(onError, "URL de telechargement invalide.", null);
      return false;
    }

    const resolved = resolveApiUrl(path);
    const url = new URL(resolved, window.location.origin);
    const isHttp = url.protocol === "http:" || url.protocol === "https:";
    if (!isHttp || !url.pathname.startsWith("/api/")) {
      reportDownloadError(onError, "URL de telechargement refusee.", null);
      return false;
    }

    try {
      return triggerDownloadWithIframe(url.toString());
    } catch {
      return triggerDownloadWithAnchor(url.toString());
    }
  } catch (error) {
    reportDownloadError(onError, "Impossible d'ouvrir le lien de telechargement.", error);
    return false;
  }
}
