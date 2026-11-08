import { tr } from "../app/uiText.js";

export function isSsrfBlocked(error) {
  return error?.code === "ssrf_blocked"
    || error?.extensions?.code === "ssrf_blocked"
    || error?.payload?.code === "ssrf_blocked";
}

export function ssrfBlockedMessage(language) {
  return tr(
    "Connexion bloquée par la protection SSRF. Cette destination privée n'est pas autorisée. Ajoutez l'hôte et son port dans TrustedHosts ou son réseau dans TrustedNetworks, puis redémarrez Feedarr.",
    "Connection blocked by SSRF protection. This private destination is not authorized. Add its host and port to TrustedHosts or its network to TrustedNetworks, then restart Feedarr.",
    language
  );
}

export function integrationSaveErrorMessage(error, kind = "source") {
  if (!isSsrfBlocked(error)) return error?.message;
  const persistence = kind === "application"
    ? tr("L'application peut déjà être enregistrée, mais sa connexion ou sa synchronisation n'a pas été validée.",
      "The application may already be saved, but its connection or synchronization has not been validated.")
    : tr("La source peut déjà être enregistrée, mais ses CAPS ou sa synchronisation n'ont pas été validés.",
      "The source may already be saved, but its CAPS or synchronization have not been validated.");
  return `${ssrfBlockedMessage()} ${persistence}`;
}

export function discoveryFailureState(error) {
  return isSsrfBlocked(error) ? "error" : "manual";
}

export function shouldRetryDiscovery(error, attempt) {
  return !isSsrfBlocked(error) && attempt === 0
    && /invalid start of a value|unexpected token\s*</i.test(error?.message || "");
}
