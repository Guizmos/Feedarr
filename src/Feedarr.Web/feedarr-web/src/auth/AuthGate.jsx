import React, { useEffect, useSyncExternalStore } from "react";
import { Navigate, Outlet, useLocation } from "react-router-dom";
import { getSessionSnapshot, subscribeSession, refreshSession, sessionRoute, lockSession } from "./session.js";
import { tr } from "../app/uiText.js";

export default function AuthGate() {
  const { phase, state } = useSyncExternalStore(subscribeSession, getSessionSnapshot, getSessionSnapshot);
  const location = useLocation();
  useEffect(() => { void refreshSession().catch(() => {}); }, []);
  useEffect(() => {
    if (!state?.expiresAt) return undefined;
    const delay = Math.max(0, new Date(state.expiresAt).getTime() - Date.now());
    const timer = setTimeout(() => { lockSession(); void refreshSession().catch(() => {}); }, Math.min(delay, 2_147_483_647));
    return () => clearTimeout(timer);
  }, [state]);

  if (phase !== "ready") return <main className="auth-page"><section className="auth-card" role="status">
    <h1>{tr("Feedarr", "Feedarr")}</h1>
    <p>{phase === "error" ? tr("Connexion au serveur impossible. Vos données restent masquées. Vérifiez le réseau puis réessayez.", "Cannot reach the server. Your data remains hidden. Check your connection and retry.") : tr("Vérification de la session…", "Checking your session…")}</p>
    {phase === "error" && <button className="btn btn-accent" onClick={() => { lockSession(); void refreshSession().catch(() => {}); }}>{tr("Réessayer", "Retry")}</button>}
  </section></main>;

  const route = sessionRoute(state);
  if (route === "configuration") return <main className="auth-page"><section className="auth-card">
    <h1>{tr("Configuration de sécurité requise", "Security configuration required")}</h1>
    <p>{tr("Aucun identifiant n’est configuré. Utilisez le parcours de configuration autorisé de votre instance ou votre accès local Smart. Aucun accès privé n’a été ouvert.", "No credentials are configured. Use your instance’s authorized setup flow or local Smart access. Private access remains protected.")}</p>
    <button className="btn" onClick={() => { lockSession(); void refreshSession().catch(() => {}); }}>{tr("Vérifier à nouveau", "Check again")}</button>
  </section></main>;
  if (route === "login" && location.pathname !== "/login") return <Navigate replace to={`/login?returnTo=${encodeURIComponent(location.pathname + location.search + location.hash)}`} />;
  if (route === "setup" && location.pathname !== "/setup") return <Navigate replace to="/setup" />;
  return <Outlet />;
}
