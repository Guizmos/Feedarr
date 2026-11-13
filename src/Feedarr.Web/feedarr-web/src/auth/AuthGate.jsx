import React, { useEffect, useSyncExternalStore } from "react";
import { Navigate, Outlet, useLocation } from "react-router-dom";
import { getSessionSnapshot, subscribeSession, refreshSession, sessionRoute, sessionRedirect } from "./session.js";
import { tr } from "../app/uiText.js";

export default function AuthGate() {
  const { phase, state, error } = useSyncExternalStore(subscribeSession, getSessionSnapshot, getSessionSnapshot);
  const location = useLocation();
  useEffect(() => { void refreshSession().catch(() => {}); }, []);
  useEffect(() => {
    if (!state?.expiresAt) return undefined;
    const delay = Math.max(0, new Date(state.expiresAt).getTime() - Date.now());
    const timer = setTimeout(() => { void refreshSession().catch(() => {}); }, Math.min(delay, 2_147_483_647));
    return () => clearTimeout(timer);
  }, [state]);

  const checking = phase !== "ready";
  const notice = <main className="auth-page"><section className="auth-card" role="status">
    <h1>{tr("Feedarr", "Feedarr")}</h1>
    <p>{phase === "error" ? tr("Connexion au serveur impossible. Vos données restent masquées. Vérifiez le réseau puis réessayez.", "Cannot reach the server. Your data remains hidden. Check your connection and retry.") : tr("Vérification de la session…", "Checking your session…")}</p>
    {phase === "error" && <p className="auth-error" role="alert">{error?.status ? `HTTP ${error.status} — ` : ""}{error?.message}</p>}
    {phase === "error" && <button className="btn btn-accent" onClick={() => { void refreshSession().catch(() => {}); }}>{tr("Réessayer", "Retry")}</button>}
  </section></main>;
  if (!state || sessionRoute(state) === "checking") return notice;

  const redirect = sessionRedirect(state, location.pathname, location.search, location.hash);
  if (!checking && redirect) return <Navigate replace to={redirect} />;
  const masked = checking && state.authenticated;
  return <>
    <div className={`auth-content${state.justCreated ? " auth-enter" : ""}`} style={masked ? { visibility: "hidden" } : undefined} inert={masked || undefined} data-auth-private={state.authenticated ? "true" : "false"}><Outlet /></div>
    {checking && (masked || phase === "error") && <div className="auth-verification">{notice}</div>}
  </>;
}
