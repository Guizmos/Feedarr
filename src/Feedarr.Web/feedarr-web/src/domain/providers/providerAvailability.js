// Controlled codes only: never render an arbitrary provider error body.
export function describeProviderAvailability(instance) {
  if (instance?.providerKey !== "rawg" || !instance.availability) return null;
  const { status, reason } = instance.availability;
  const values = {
    quota_exhausted: ["Quota mensuel épuisé", "Monthly quota exhausted", "Appels RAWG suspendus. Après renouvellement du quota, utilisez « Vérifier et réactiver ». Un test manuel au maximum toutes les 30 secondes.", "RAWG calls suspended. After quota renewal, use ‘Check and reactivate’. At most one manual test every 30 seconds."],
    authentication_rejected: reason === "rawg_invalid_api_key"
      ? ["Authentification invalide", "Invalid authentication", "RAWG signale une clé invalide. Corrigez la configuration, puis vérifiez la connexion.", "RAWG reports an invalid key. Update the configuration, then check the connection."]
      : ["Accès non autorisé", "Unauthorized access", "RAWG refuse l’accès. Vérifiez les autorisations, puis testez la connexion.", "RAWG denied access. Check permissions, then test the connection."],
    access_denied: ["Accès refusé", "Access denied", "Appels RAWG suspendus après un refus d’accès. Vérifiez les autorisations.", "RAWG calls suspended after access was denied. Check permissions."],
    temporary_error: ["Erreur temporaire", "Temporary error", "RAWG est temporairement indisponible. Réessayez plus tard.", "RAWG is temporarily unavailable. Try again later."],
    request_error: ["Requête refusée", "Request rejected", "RAWG a refusé la requête ; ce refus ne prouve pas un quota épuisé.", "RAWG rejected the request; this does not establish quota exhaustion."],
    configuration_error: ["Configuration indisponible", "Configuration unavailable", "Vérifiez la configuration RAWG.", "Check RAWG configuration."],
    ready: ["Disponible", "Available", "", ""],
    unknown: ["Non vérifié", "Not checked", "", ""],
  };
  const value = values[status];
  if (!value) return null;
  return { fr: value[0], en: value[1], detailFr: value[2], detailEn: value[3],
    warning: status !== "ready", suspended: instance.availability.suspended === true };
}
