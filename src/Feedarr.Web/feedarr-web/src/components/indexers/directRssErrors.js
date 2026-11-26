import { isSsrfBlocked } from "../../utils/ssrfErrors.js";

// Never render an arbitrary response body: a proxy or server error may echo a signed URL.
export function directRssHttpError(error) {
  if (isSsrfBlocked(error)) return "Destination RSS refusée par la protection SSRF. Utilise une URL publique autorisée.";
  const status = Number(error?.status);
  if (status === 401) return "Session Feedarr non authentifiée (HTTP 401). Reconnecte-toi.";
  if (status === 403) return "Requête refusée par Feedarr (HTTP 403). Vérifie la session et les permissions.";
  if (status === 404) return "Endpoint RSS direct introuvable (HTTP 404). Recompile puis redémarre le backend Feedarr.";
  if (status === 405) return "Méthode RSS direct indisponible (HTTP 405). Vérifie que le backend Feedarr est à jour et redémarré.";
  if (status === 400) {
    const errors = error?.payload?.errors;
    const fields = errors && typeof errors === "object" ? Object.keys(errors) : [];
    if (fields.some((field) => /(?:^|\.)CategoryKey$/i.test(field))) return "Catégorie Feedarr obligatoire ou invalide (HTTP 400).";
    if (fields.some((field) => /(?:^|\.)Name$/i.test(field))) return "Nom de source ou de flux invalide (HTTP 400). Vérifie sa longueur et le nom obligatoire de la source.";
    const known = {
      "RSS URL or Feedarr category invalid": "URL RSS ou catégorie Feedarr invalide (HTTP 400).",
      "Unknown RSS feed": "Identifiant de flux RSS inconnu pour cette source (HTTP 400). Recharge la source.",
      "Invalid RSS feed list": "Liste de flux RSS invalide : identifiants répétés ou plus de 100 flux (HTTP 400).",
    };
    const reason = error?.payload?.error || error?.payload?.title;
    return (Object.hasOwn(known, reason) ? known[reason] : null) || "Configuration RSS refusée par Feedarr (HTTP 400). Vérifie les champs obligatoires et leur format.";
  }
  if (status >= 500 && status <= 599) return `Erreur du backend Feedarr (HTTP ${status}). Consulte les logs backend.`;
  if (Number.isInteger(status) && status >= 400 && status <= 499) return `Requête RSS refusée par Feedarr (HTTP ${status}).`;
  return "Backend Feedarr injoignable ou délai dépassé. Vérifie que l’instance locale est démarrée.";
}
