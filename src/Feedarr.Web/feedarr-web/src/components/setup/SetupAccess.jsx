import React, { useRef, useState } from "react";
import { login, refreshSession } from "../../auth/session.js";
import InlineNotice from "../../pages/settings/InlineNotice.jsx";
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
      setError(e.code === "https_required" ? "Une connexion HTTPS est nécessaire pour ouvrir une session web." : e.name === "TimeoutError" || e instanceof TypeError ? "Connexion au serveur impossible ou trop lente. Vérifiez le réseau puis réessayez." : "Connexion refusée. Vérifiez les identifiants de cette instance et réessayez.");
    } finally { setBusy(false); }
  };
  const checkAccess = async () => {
    if (busy) return;
    setBusy(true); setError("");
    try { await refreshSession(); }
    catch (e) { setError(e?.message || "Vérification impossible. Vérifiez la connexion puis réessayez."); }
    finally { setBusy(false); }
  };
  return <div className="setup-container"><div className="setup-card setupWizardFrame" aria-busy={busy || status.saving}>
    <div className="setupWizardBody"><div className="setup-card__content setup-step-group">
      <div className="setup-step setup-security setup-step-group">
        <div><h2>Autoriser la configuration de cette instance</h2>
          <p className="muted">Connectez-vous avec les identifiants de cette instance pour poursuivre la configuration.</p></div>
        {state.authConfigured ? <form id="setup-login" className="indexer-list" onSubmit={connect}>
          <div className="indexer-card"><div className="indexer-row indexer-row--settings">
            <label className="indexer-title" htmlFor="setup-username">Utilisateur</label>
            <div className="indexer-actions"><div className="fieldWrap"><input id="setup-username" name="username" autoComplete="username" value={username} onChange={e => setUsername(e.target.value)} disabled={busy} required /></div></div>
          </div></div>
          <div className="indexer-card"><div className="indexer-row indexer-row--settings">
            <label className="indexer-title" htmlFor="setup-password">Mot de passe</label>
            <div className="indexer-actions"><div className="fieldWrap"><input id="setup-password" name="password" type="password" autoComplete="current-password" value={password} onChange={e => setPassword(e.target.value)} disabled={busy} required /></div></div>
          </div></div>
        </form> : <Step2Security saveRef={saveRef} onStatusChange={setStatus} required />}
        {error && <InlineNotice variant="error" message={error} />}
      </div>
    </div></div>
    <div className="setupWizardFooter"><div className="setup-actions">
      <div className="settings-card__actions">
        {onBack && <button className="btn" type="button" disabled={busy || status.saving} onClick={onBack}>Retour au choix</button>}
        <button className="btn" type="button" disabled={busy || status.saving} onClick={checkAccess}>Vérifier l’accès</button>
      </div>
      {state.authConfigured
        ? <button className="btn btn-accent" type="submit" form="setup-login" disabled={busy}>{busy ? "Connexion…" : "Se connecter"}</button>
        : <button className="btn btn-accent" type="button" disabled={status.saving} onClick={() => saveRef.current?.()}>{status.saving ? "Enregistrement…" : "Enregistrer les identifiants"}</button>}
    </div></div>
  </div></div>;
}
