import { apiGet, apiPost, resolveApiUrl } from "../api/client.js";
import { tr } from "../app/uiText.js";

const POLL_INTERVAL_MS = 1000;
const DIAGNOSTIC_TIMEOUT_MS = 4 * 60_000;
let preparingSession;

function createDownloadAttempt() {
  if (typeof crypto.randomUUID === "function") return crypto.randomUUID();
  const bytes = crypto.getRandomValues(new Uint8Array(16));
  bytes[6] = (bytes[6] & 0x0f) | 0x40;
  bytes[8] = (bytes[8] & 0x3f) | 0x80;
  const hex = Array.from(bytes, (byte) => byte.toString(16).padStart(2, "0")).join("");
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}

// Serialize first-use cookie creation across concurrent downloads in this page.
function prepareSession(path) {
  if (!preparingSession) {
    preparingSession = apiPost(path.replace(/\/download$/, "/download-session"), null)
      .finally(() => { preparingSession = undefined; });
  }
  return preparingSession;
}

export function downloadErrorMessage(status, code, diagnosticUnavailable = false) {
  if (code === "download_session_invalid") return tr(
    "Session de téléchargement invalide ou expirée. Vérifiez les téléchargements du navigateur avant de réessayer.",
    "Download session is invalid or expired. Check your browser downloads before retrying."
  );
  if (diagnosticUnavailable) return tr(
    "Impossible de vérifier le téléchargement : erreur réseau ou suivi indisponible. Vérifiez les téléchargements du navigateur avant de réessayer.",
    "Unable to check the download: network error or tracking unavailable. Check your browser downloads before retrying."
  );
  if (code === "ssrf_blocked") return tr(
    "Téléchargement bloqué par la protection SSRF. Les torrents HTTP sur une destination privée restent interdits, même si le fournisseur est autorisé pour le RSS.",
    "Download blocked by SSRF protection. HTTP torrents on private destinations remain blocked, even when the provider is authorized for RSS."
  );
  if (status === 401) return tr("Authentification requise pour télécharger. Reconnectez-vous à Feedarr.", "Authentication is required to download. Sign in to Feedarr again.");
  if (status === 403) return tr("Téléchargement refusé : accès non autorisé.", "Download denied: access is not authorized.");
  if (status === 404) return tr("Release ou lien de téléchargement introuvable.", "Release or download link not found.");
  if (code === "network_error") return tr("Le téléchargement a échoué : erreur réseau avec le fournisseur.", "Download failed: network error while contacting the provider.");
  if (status === 400) return tr("Lien de téléchargement invalide ou destination refusée.", "Invalid download link or destination denied.");
  return tr("Le téléchargement a échoué : erreur du serveur ou du fournisseur.", "Download failed: server or provider error.");
}

// One native download, plus local status lookups. Never fetch/blob the torrent in JS.
export async function openDownloadPath(downloadPath, { onError } = {}) {
  if (typeof window === "undefined") return false;
  let iframe;
  let reported = false;
  const report = (message) => {
    if (!reported) onError?.(message);
    reported = true;
  };
  try {
    const path = String(downloadPath || "").trim();
    if (!/^\/api\/releases\/[1-9]\d*\/download$/.test(path)) {
      report(tr("Lien de téléchargement invalide.", "Invalid download link."));
      return false;
    }
    const url = new URL(resolveApiUrl(path), window.location.origin);
    if (!["http:", "https:"].includes(url.protocol) || url.username || url.password) {
      report(tr("Lien de téléchargement refusé.", "Download link denied."));
      return false;
    }
    try {
      await prepareSession(path);
    } catch (error) {
      report(downloadErrorMessage(error?.status, error?.code,
        error?.status !== 401 && error?.status !== 403));
      return false;
    }
    const attempt = createDownloadAttempt();
    url.searchParams.set("downloadAttempt", attempt);
    iframe = document.createElement("iframe");
    iframe.style.display = "none";
    iframe.setAttribute("aria-hidden", "true");
    iframe.onerror = () => report(downloadErrorMessage(null, null, true));
    iframe.src = url.toString();
    document.body.appendChild(iframe);
    // No load handler: navigating an iframe does not prove a successful download.
    const startedAt = Date.now();
    const deadline = startedAt + DIAGNOSTIC_TIMEOUT_MS;
    while (!reported && Date.now() < deadline) {
      let result;
      try {
        result = await apiGet(`${path.replace(/\/download$/, "/download-status")}/${attempt}`, { timeoutMs: 10_000 });
      } catch (error) {
        // Initial lookup can race with the iframe reaching the server; do not retry the download.
        if (error?.status === 404 && Date.now() - startedAt < 5000) {
          await new Promise((resolve) => setTimeout(resolve, POLL_INTERVAL_MS));
          continue;
        }
        report(downloadErrorMessage(error?.status, error?.code, error?.status !== 401 && error?.status !== 403));
        break;
      }
      if (result?.state === "failed") {
        report(downloadErrorMessage(result.statusCode, result.code));
        break;
      }
      if (result?.state === "completed") {
        iframe.remove();
        return true; // server response completed; no claim about browser/OS file saving
      }
      await new Promise((resolve) => setTimeout(resolve, POLL_INTERVAL_MS));
    }
    if (!reported) report(downloadErrorMessage(null, null, true));
    // A failed diagnostic request must not cancel a possibly ongoing native transfer.
    setTimeout(() => iframe?.remove(), DIAGNOSTIC_TIMEOUT_MS);
    return false;
  } catch {
    report(downloadErrorMessage(null, null, true));
    iframe?.remove();
    return false;
  }
}
