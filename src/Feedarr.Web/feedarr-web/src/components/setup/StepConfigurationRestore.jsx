import React, { useRef } from "react";
import AppIcon from "../../ui/AppIcon.jsx";
import { fmtBytes } from "../../pages/settings/settingsUtils.js";
import InlineNotice from "../../pages/settings/InlineNotice.jsx";
import { ConfigurationPreview } from "../backup/ConfigurationBackupPanel.jsx";

export default function StepConfigurationRestore({ restore }) {
  const archiveInput = useRef(null);
  const { stage, state, busy } = restore;
  const secretRow = (key, label, value, update, hint) => <div className="indexer-card">
    <div className="indexer-row indexer-row--settings">
      <label className="indexer-title" htmlFor={`restore-${key}`}>{label}</label>
      <div className="indexer-actions"><div className="fieldWrap"><input id={`restore-${key}`} type="password" autoComplete="off" disabled={busy} value={value} onChange={e => update(e.target.value)} aria-describedby={`restore-${key}-hint`} /></div></div>
    </div><div className="settings-help" id={`restore-${key}-hint`}>{hint}</div>
  </div>;
  return <div className="setup-step">
    <h2>{["Importer une configuration", "Vérifier la configuration", "Restaurer la configuration", "Finaliser la restauration"][stage - 1]}</h2>
    {stage === 1 && <>
      <p className="muted">Sélectionnez une archive V2 ou une ancienne sauvegarde compatible. Seule la configuration sera importée.</p>
      <div className="indexer-list">
        <div className="indexer-card"><div className="indexer-row indexer-row--settings">
          <span className="indexer-title">Archive de configuration</span><div className="indexer-actions">
            <input ref={archiveInput} type="file" hidden accept=".zip,application/zip" aria-label="Archive de configuration" disabled={busy} onChange={e => { const next = e.target.files?.[0]; if (next) void restore.setFile(next); e.target.value = ""; }} />
            <button className="btn" type="button" disabled={busy} onClick={() => archiveInput.current?.click()}><AppIcon name="save" />{restore.file ? "Remplacer le fichier" : "Choisir un fichier"}</button>
          {restore.file && <button className="btn" type="button" disabled={busy} onClick={() => restore.setFile(null)}>Annuler la sélection</button>}
          </div></div>
          <div className="settings-help card-meta__value" role="status">{restore.file ? `${restore.file.name} — ${fmtBytes(restore.file.size) || "0 B"}` : "Aucun fichier sélectionné"}</div>
          <div className="settings-help">Archive non vide de 64 Mio maximum.</div>
        </div>
        {restore.file && restore.analysisStatus === "selected" && <InlineNotice variant="info" title="Archive sélectionnée" message="Cette archive n’a pas encore été analysée." />}
        {restore.analysisStatus === "error" && <InlineNotice variant="error" title="Archive refusée ou erreur" message="L’archive n’a pas été validée. Vérifiez le fichier et la phrase secrète avant de réessayer." />}
        {secretRow("phrase", "Phrase secrète", restore.phrase, restore.setPhrase, "Laissez vide pour une ancienne archive non chiffrée.")}
        {secretRow("bootstrap", "Secret bootstrap", restore.bootstrap, restore.setBootstrap, "Uniquement si votre accès distant le demande.")}
      </div>
      {state?.legacyRestorePending && <InlineNotice variant="warning" message="Une restauration V1 est en attente. Appliquez-la ou annulez-la avant tout import V2." />}
    </>}
    {stage === 2 && <>
      <InlineNotice variant="success" title="Archive analysée et valide" message="Vérifiez les éléments importés et les avertissements avant de continuer." />
      <ConfigurationPreview preview={state?.pending} />
      <label className="checkline"><input type="checkbox" disabled={busy} checked={restore.consent} onChange={e => restore.setConsent(e.target.checked)} />Je confirme le remplacement de la configuration, avec conservation de la bibliothèque.</label>
      {state?.pending?.securityChangeRequiresConfirmation && <label className="checkline"><input type="checkbox" disabled={busy} checked={restore.securityConsent} onChange={e => restore.setSecurityConsent(e.target.checked)} />J’accepte les changements de sécurité et utiliserai les identifiants restaurés après redémarrage.</label>}
    </>}
    {stage === 3 && <>
      {state?.confirmed ? <InlineNotice variant="warning" title="Redémarrage requis" message="La restauration est confirmée. Redémarrez le conteneur Feedarr, puis vérifiez son application. Les identifiants restaurés peuvent nécessiter une nouvelle connexion." /> : <InlineNotice variant="info" title="Prêt à restaurer" message="La confirmation prépare la configuration pour le prochain redémarrage. La bibliothèque, les entities et les posters seront conservés." />}
    </>}
    {stage === 4 && <>
      <InlineNotice variant="success" title="Configuration appliquée" message="La bibliothèque a été conservée. Continuez pour accéder à Feedarr ou reprendre uniquement les étapes de configuration manquantes." />
      {state?.applied?.retentionPending && <InlineNotice variant="warning" message="La rétention importée plus stricte reste inactive. Vous pourrez la confirmer séparément dans les paramètres de sauvegarde." />}
      {state?.applied?.syncState === "held" && <InlineNotice variant="info" message="La première synchronisation attend une confirmation dans les paramètres de sauvegarde." />}
    </>}
    {restore.checking && <InlineNotice variant="info" message="Vérification de l’état de restauration…" />}
    {restore.error && <InlineNotice variant="error" message={restore.error} />}
    {restore.notice && <InlineNotice variant={restore.analysisStatus === "valid" ? "success" : "info"} message={restore.notice} />}
    {busy && <InlineNotice variant="info" message={stage === 1 ? "Analyse de l’archive en cours…" : "Opération en cours…"} />}
  </div>;
}
