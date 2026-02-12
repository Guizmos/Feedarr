import { resolveApiUrl } from "../api/client.js";

function reportDownloadError(onError, message, error) {
  console.error(message, error);
  if (typeof onError === "function") {
    onError(message);
  }
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

    const popup = window.open(url.toString(), "_blank", "noopener,noreferrer");
    if (!popup) {
      reportDownloadError(onError, "Le telechargement a ete bloque par le navigateur.", null);
      return false;
    }

    popup.opener = null;
    return true;
  } catch (error) {
    reportDownloadError(onError, "Impossible d'ouvrir le lien de telechargement.", error);
    return false;
  }
}
