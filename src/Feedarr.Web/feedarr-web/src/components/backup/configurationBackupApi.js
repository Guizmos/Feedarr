import { resolveApiUrl } from "../../api/client.js";

export const MAX_CONFIGURATION_UPLOAD = 8 * 1024 * 1024 + 128;
const signature = "FEEDARR-CONFIG-3\n";

export async function configurationRequest(action, { body, signal, fetcher = fetch, binary = false } = {}) {
  const controller = new AbortController();
  const abort = () => controller.abort(signal.reason);
  if (signal?.aborted) abort(); else signal?.addEventListener("abort", abort, { once: true });
  const timer = setTimeout(() => controller.abort(new DOMException("Délai dépassé. Vérifiez le résultat avant de réessayer.", "TimeoutError")), 120000);
  const headers = { "X-Feedarr-Request": "1", Accept: binary ? "application/octet-stream" : "application/json" };
  if (!(body instanceof FormData)) headers["Content-Type"] = "application/json";
  try {
    const response = await fetcher(resolveApiUrl(`/api/configuration-backups/${action}`), { method: "POST", headers, credentials: "include", signal: controller.signal,
      body: body instanceof FormData ? body : JSON.stringify(body) });
    if (!response.ok) {
      let payload; try { payload = await response.json(); } catch { /* Never echo raw server content. */ }
      const known = {
        AuthenticationFailed: "Mot de passe incorrect ou sauvegarde altérée.",
        InvalidPassphrase: "Utilisez un mot de passe de 12 à 1024 caractères.",
        UnreadableSecrets: "Des secrets locaux sont indéchiffrables. Aucune sauvegarde n’a été créée.",
        UnsupportedVersion: "Version de sauvegarde incompatible.",
        InvalidArchive: "Fichier de sauvegarde invalide.", InvalidConfiguration: "Configuration invalide.",
        SizeLimitExceeded: "La sauvegarde dépasse la limite de 8 Mio.",
      };
      const message = known[payload?.code] || (response.status === 409 ? "La configuration a changé. Analysez à nouveau la sauvegarde." : response.status === 413 ? "Fichier trop volumineux (8 Mio maximum)." : response.status === 429 ? "Trop de demandes. Patientez une minute." : [401, 403].includes(response.status) ? "Autorisation refusée. Vérifiez votre connexion." : "L’opération a échoué. Aucune configuration partielle n’est conservée.");
      const error = new Error(message); error.status = response.status; throw error;
    }
    if (binary) {
      const blob = await response.blob();
      if (await blob.slice(0, signature.length).text() !== signature)
        throw new Error("Le serveur n’a pas renvoyé une sauvegarde Feedarr valide.");
      return blob;
    }
    return await response.json();
  } catch (error) {
    if (controller.signal.aborted) throw controller.signal.reason;
    if (error instanceof TypeError) throw new Error("Connexion interrompue. Vérifiez le résultat avant de réessayer.");
    throw error;
  } finally { clearTimeout(timer); signal?.removeEventListener("abort", abort); }
}

export function configurationFileError(file) {
  if (!file) return "Sélectionnez une sauvegarde.";
  if (!/\.feedarr$/i.test(file.name || "")) return "Sélectionnez un fichier .feedarr.";
  if (!file.size) return "Le fichier est vide.";
  if (file.size > MAX_CONFIGURATION_UPLOAD) return "Fichier trop volumineux (8 Mio maximum).";
  return "";
}

export function configurationUpload(file, password) {
  const error = configurationFileError(file); if (error) throw new Error(error);
  const body = new FormData(); body.append("password", password); body.append("file", file); return body;
}

export function downloadConfiguration(blob, browser = window, dom = document) {
  if (!blob?.size) throw new Error("La sauvegarde reçue est vide.");
  const url = browser.URL.createObjectURL(blob);
  try {
    const link = dom.createElement("a"); link.href = url; link.download = "feedarr-configuration.feedarr";
    dom.body.appendChild(link); try { link.click(); } finally { link.remove(); }
  } finally { browser.setTimeout(() => browser.URL.revokeObjectURL(url), 1000); }
}
