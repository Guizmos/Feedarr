import React, { useRef, useState, useSyncExternalStore } from "react";
import { getSessionSnapshot, subscribeSession, logout } from "./session.js";
import AppIcon from "../ui/AppIcon.jsx";
import { tr } from "../app/uiText.js";

export default function LogoutButton() {
  const { state } = useSyncExternalStore(subscribeSession, getSessionSnapshot, getSessionSnapshot);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const submitting = useRef(false);
  if (!state?.authenticated) return null;
  return <div className="auth-logout">
    <button className="subaction auth-logout-action" type="button" title={tr("Déconnexion", "Log out")} aria-label={tr("Déconnexion", "Log out")} aria-busy={busy} disabled={busy} onClick={async () => {
      if (submitting.current) return;
      submitting.current = true; setBusy(true); setError("");
      try { await logout(); }
      catch { setError(tr("Déconnexion impossible. Vérifiez le réseau et réessayez.", "Could not sign out. Check your connection and retry.")); }
      finally { submitting.current = false; setBusy(false); }
    }}><AppIcon name="logout" className="subaction__icon" /></button>
    {error && <span role="alert" className="auth-logout-error">{error}</span>}
  </div>;
}
