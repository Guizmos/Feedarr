import React, { useSyncExternalStore } from "react";
import { getSessionSnapshot, subscribeSession } from "../auth/session.js";

export default function HttpTransportNotice() {
  const { state } = useSyncExternalStore(subscribeSession, getSessionSnapshot, getSessionSnapshot);
  if (typeof window === "undefined" || window.location.protocol !== "http:") return null;
  return <div className="http-transport-notice" role="status">Connexion HTTP non chiffrée : vos identifiants et données transitent en clair.
    {state?.httpRecovery && " Récupération HTTP temporaire activée : désactivez-la sur le serveur après correction."}
  </div>;
}
