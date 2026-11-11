import React, { useRef, useState, useSyncExternalStore } from "react";
import { getSessionSnapshot, subscribeSession, logout } from "./session.js";
import { tr } from "../app/uiText.js";

export default function LogoutButton() {
  const { state } = useSyncExternalStore(subscribeSession, getSessionSnapshot, getSessionSnapshot);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const submitting = useRef(false);
  if (!state?.authenticated) return null;
  return <div className="auth-logout">
    <button className="btn" type="button" disabled={busy} onClick={async () => {
      if (submitting.current) return;
      submitting.current = true; setBusy(true); setError("");
      try { await logout(); }
      catch { setError(tr("Déconnexion impossible. Vérifiez le réseau et réessayez.", "Could not sign out. Check your connection and retry.")); }
      finally { submitting.current = false; setBusy(false); }
    }}>{tr("Déconnexion", "Sign out")}</button>
    {error && <span role="alert" className="auth-logout-error">{error}</span>}
  </div>;
}
