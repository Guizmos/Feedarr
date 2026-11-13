import React from "react";

export default function SettingsUsers({ security, setSecurity }) {
  const field = (key, value) => setSecurity(current => ({ ...current, [key]: value }));
  return <section className="settings-card" id="security">
    <div className="settings-card__title">Compte administrateur et connexion</div>
    <div className="indexer-list">
      <div className="indexer-card"><div className="indexer-row indexer-row--settings">
        <label className="indexer-title" htmlFor="transport-policy">Transport</label>
        <div className="indexer-actions"><select id="transport-policy" value={String(security.requireHttps)} onChange={e => field("requireHttps", e.target.value === "true")}>
          <option value="false">HTTP et HTTPS autorisés</option><option value="true">HTTPS obligatoire</option>
        </select></div>
      </div><p className="settings-help">L’authentification est toujours obligatoire. HTTP n’est pas chiffré. Pour imposer HTTPS, ouvrez d’abord Feedarr via une configuration HTTPS fonctionnelle.</p></div>
      {security.transportMigration && <p className="settings-help" role="status">Politique migrée : les anciens modes protégés conservent HTTPS obligatoire. Une instance anciennement Open exige désormais un compte administrateur.</p>}
      {[["username", "Identifiant", "text", "username"], ["password", "Nouveau mot de passe", "password", "new-password"], ["passwordConfirmation", "Confirmer le mot de passe", "password", "new-password"], ["publicBaseUrl", "URL publique", "url", "url"]].map(([key, label, type, autoComplete]) =>
        <div className="indexer-card" key={key}><div className="indexer-row indexer-row--settings">
          <label className="indexer-title" htmlFor={`security-${key}`}>{label}</label>
          <div className="indexer-actions"><div className="fieldWrap"><input id={`security-${key}`} type={type} autoComplete={autoComplete} value={security[key]} onChange={e => field(key, e.target.value)} /></div></div>
        </div></div>)}
      <p className="settings-help">Laissez les mots de passe vides pour conserver l’actuel. Après un changement, reconnectez-vous. Si HTTPS devient indisponible, activez temporairement Security__AllowHttpRecovery=true sur le serveur puis connectez-vous avec vos identifiants pour corriger la politique.</p>
    </div>
  </section>;
}
