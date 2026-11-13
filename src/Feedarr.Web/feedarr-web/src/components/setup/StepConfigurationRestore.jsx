import React from "react";
import ConfigurationArchiveInput from "../backup/ConfigurationArchiveInput.jsx";
import InlineNotice from "../../pages/settings/InlineNotice.jsx";
import { ConfigurationPreview } from "../backup/ConfigurationBackupPanel.jsx";

export default function StepConfigurationRestore({ restore }) {
  const { stage, state, busy } = restore;
  const secretRow = (key, label, value, update, hint) => <div className="indexer-card">
    <div className="indexer-row indexer-row--settings">
      <label className="indexer-title" htmlFor={`restore-${key}`}>{label}</label>
      <div className="indexer-actions"><div className="fieldWrap"><input id={`restore-${key}`} type="password" autoComplete="off" disabled={busy} value={value} onChange={e => update(e.target.value)} aria-describedby={`restore-${key}-hint`} /></div></div>
    </div><div className="settings-help" id={`restore-${key}-hint`}>{hint}</div>
  </div>;
  return <div className="setup-step setup-security setup-step-group" aria-busy={busy || restore.checking}>
    <h2>{["Importer une configuration", "Vérifier la configuration", "Restaurer la configuration", "Finaliser la restauration"][stage - 1]}</h2>
    {stage === 1 && <>
      <p className="muted">Sélectionnez une archive V2 ou une ancienne sauvegarde compatible. Seule la configuration sera importée.</p>
      <div className="indexer-list">
        <ConfigurationArchiveInput file={restore.file} onChange={restore.setFile} disabled={busy}
          status={restore.analysisStatus} error={restore.error} id="restore-archive" />
        {secretRow("phrase", "Phrase secrète de la sauvegarde", restore.phrase, restore.setPhrase, "Laissez vide pour une ancienne archive non chiffrée.")}
        {secretRow("bootstrap", "Secret bootstrap de l’instance cible", restore.bootstrap, restore.setBootstrap, "Autorise l’accès à cette instance à distance. Ce secret est distinct de la phrase secrète de la sauvegarde.")}
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
    {restore.error && <><InlineNotice variant="error" message={restore.error} /><button className="btn" type="button" disabled={busy || restore.checking} onClick={restore.reloadState}>Revérifier l’état de restauration</button></>}
    {restore.notice && <InlineNotice variant={restore.analysisStatus === "valid" ? "success" : "info"} message={restore.notice} />}
    {busy && <InlineNotice variant="info" message={stage === 1 ? "Analyse de l’archive en cours…" : "Opération en cours…"} />}
  </div>;
}
