import { resolveApiUrl } from "../api/client.js";

function reportDownloadError(onError, message, error) {
  console.error(message, error);
  if (typeof onError === "function") {
    onError(message);
  }
}

function navigateCurrentTab(url) {
  if (typeof document !== "undefined" && document.body) {
    const link = document.createElement("a");
    link.href = url;
    link.rel = "noopener noreferrer";
    link.style.display = "none";
    document.body.appendChild(link);
    try {
      link.click();
    } finally {
      document.body.removeChild(link);
    }
    return true;
  }

  if (typeof window !== "undefined") {
    window.location.assign(url);
    return true;
  }

  return false;
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
      return navigateCurrentTab(url.toString());
    }

    popup.opener = null;
    return true;
  } catch (error) {
    reportDownloadError(onError, "Impossible d'ouvrir le lien de telechargement.", error);
    return false;
  }
}
