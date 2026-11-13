import React, { useRef, useState } from "react";
import { login, refreshSession } from "../../auth/session.js";
import Step2Security from "./Step2Security.jsx";

// Authorize the target before loading or mutating protected setup settings.
export default function SetupAccess({ state, onBack }) {
  const saveRef = useRef(null);
  const [status, setStatus] = useState({});
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const connect = async event => {
    event.preventDefault();
    if (busy) return;
    setBusy(true); setError("");
    try { await login(username, password, false); setPassword(""); }
    catch (e) {
      setPassword("");
      setError(e.code === "https_required" ? "Une connexion HTTPS est nécessaire pour ouvrir une session web." : "Connexion refusée. Vérifiez les identifiants de cette instance et réessayez.");
    } finally { setBusy(false); }
  };
  return <div className="setup-container"><div className="setup-card setup-step-group">
    <h2>Autoriser la configuration de cette instance</h2>
    <p>La configuration nécessite un accès autorisé avant l’enregistrement des paramètres.</p>
    {state.authConfigured ? <form onSubmit={connect}>
      <label>Utilisateur<input autoComplete="username" value={username} onChange={e => setUsername(e.target.value)} disabled={busy} /></label>
      <label>Mot de passe<input type="password" autoComplete="current-password" value={password} onChange={e => setPassword(e.target.value)} disabled={busy} /></label>
      <button className="btn btn-accent" disabled={busy}>Se connecter</button>
    </form> : <>
      <Step2Security saveRef={saveRef} onStatusChange={setStatus} required />
      <button className="btn btn-accent" disabled={status.saving} onClick={() => saveRef.current?.()}>Enregistrer les identifiants</button>
    </>}
    {error && <div role="alert">{error}</div>}
    <div className="setup-actions">
      {onBack && <button className="btn" disabled={busy || status.saving} onClick={onBack}>Retour au choix</button>}
      <button className="btn" disabled={busy || status.saving} onClick={() => { void refreshSession().catch(() => setError("Vérification impossible. Vérifiez la connexion puis réessayez.")); }}>Vérifier l’accès</button>
    </div>
  </div></div>;
}
