import { resolveApiUrl } from "../../api/client.js";

export const MAX_CONFIGURATION_UPLOAD = 64 * 1024 * 1024;
export const backupRoot = initial => initial ? "/api/setup/configuration-restore" : "/api/configuration-backups";

export async function configurationRequest(path, { method = "GET", body, bootstrap = "", signal, fetcher = fetch, binary = false } = {}) {
  const controller = new AbortController();
  const abort = () => controller.abort(signal.reason);
  if (signal?.aborted) abort(); else signal?.addEventListener("abort", abort, { once: true });
  const timer = setTimeout(() => controller.abort(new DOMException("La demande a dépassé le délai de deux minutes. Réessayez.", "TimeoutError")), 120000);
  const headers = { "X-Feedarr-Request": "1", Accept: binary ? "application/zip" : "application/json" };
  if (bootstrap) headers["X-Bootstrap-Secret"] = bootstrap;
  if (body && !(body instanceof FormData)) headers["Content-Type"] = "application/json";
  try {
    const response = await fetcher(resolveApiUrl(path), { method, headers, credentials: "include", signal: controller.signal,
      body: body instanceof FormData ? body : body ? JSON.stringify(body) : undefined });
    if (!response.ok) {
      let payload; try { payload = await response.json(); } catch { /* Never echo response text or upload content. */ }
      const known = {
        AuthenticationFailed: "Phrase secrète incorrecte ou archive altérée.",
        InvalidPassphrase: "Utilisez une phrase secrète de 12 à 1024 caractères.",
        UnreadableSecrets: "Des secrets locaux sont indéchiffrables. La sauvegarde a été refusée.",
        UnsupportedVersion: "Cette version de sauvegarde n’est pas compatible.",
        InvalidArchive: "Archive invalide.", InvalidConfiguration: "Configuration incohérente.",
        configuration_restore_ambiguous_identity: "Identités ambiguës : aucune restauration n’a été préparée.",
        configuration_restore_changed_or_invalid_state: "L’état a changé ou une confirmation reste nécessaire. Actualisez avant de réessayer.",
        configuration_restore_conflict_or_unreadable_state: "Une restauration existe déjà ou son état nécessite une vérification.",
        setup_closed: "Le setup est terminé. Reconnectez-vous et utilisez les paramètres.",
      };
      const error = new Error(known[payload?.code] || (response.status === 413 ? "Fichier trop volumineux (64 Mio maximum)." : response.status === 429 ? "Trop de demandes. Patientez avant de réessayer." : response.status === 403 || response.status === 401 ? "Autorisation requise. Vérifiez la connexion ou le secret bootstrap." : "L’opération a échoué. Actualisez puis réessayez."));
      error.status = response.status; throw error;
    }
    if (response.status === 204) return null;
    if (binary) {
      const blob = await response.blob();
      // Reject an HTTP 200 proxy/login page instead of downloading a fake ZIP.
      const signature = new Uint8Array(await blob.slice(0, 4).arrayBuffer());
      if (signature[0] !== 0x50 || signature[1] !== 0x4b || signature[2] !== 3 || signature[3] !== 4)
        throw new Error("Le serveur n’a pas renvoyé une archive ZIP valide. Vérifiez votre connexion puis réessayez.");
      return blob;
    }
    return await response.json();
  } catch (error) {
    if (controller.signal.aborted) throw controller.signal.reason;
    if (error instanceof TypeError) throw new Error("Connexion au serveur impossible. Vérifiez le réseau puis réessayez.");
    throw error;
  } finally { clearTimeout(timer); signal?.removeEventListener("abort", abort); }
}

export function configurationUpload(file, passphrase) {
  if (!file || !file.size || file.size > MAX_CONFIGURATION_UPLOAD) throw new Error("Sélectionnez une archive de 64 Mio maximum.");
  const body = new FormData(); body.append("passphrase", passphrase); body.append("archive", file); return body;
}

export function downloadConfiguration(blob, browser = window, dom = document) {
  if (!blob?.size) throw new Error("L’archive reçue est vide. Aucun téléchargement n’a été lancé.");
  const url = browser.URL.createObjectURL(blob);
  try {
    const link = dom.createElement("a"); link.href = url; link.download = "feedarr-configuration.zip";
    dom.body.appendChild(link); try { link.click(); } finally { link.remove(); }
  } finally { browser.setTimeout(() => browser.URL.revokeObjectURL(url), 1000); }
}

export function resumeSetupSteps(steps) {
  return Array.isArray(steps) ? [...new Set(steps)].filter(id => Number.isInteger(id) && id >= 3 && id <= 8).sort((a, b) => a - b) : null;
}
