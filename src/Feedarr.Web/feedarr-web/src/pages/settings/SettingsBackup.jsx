import React from "react";
import ConfigurationBackupPanel from "../../components/backup/ConfigurationBackupPanel.jsx";
import Modal from "../../ui/Modal.jsx";
import InlineNotice from "./InlineNotice.jsx";
import { fmtBytes } from "./settingsUtils.js";

export default function SettingsBackup({ backups = [], handleBackupDownload, backupDownloadName, backupError, backupNotice, backupsLoading, backupState, backupDeleteOpen, backupDeleteTarget, backupDeleteLoading, openBackupDelete, closeBackupDelete, handleBackupDelete, onActionsChange }) {
  return <>
    <ConfigurationBackupPanel onActionsChange={onActionsChange} />
    {backupError && <InlineNotice variant="error" message={backupError} />}
    {backupNotice && <InlineNotice variant="success" message={backupNotice} />}
    {backupsLoading && <InlineNotice variant="info" message="Chargement des archives locales…" />}
    {backups.length > 0 && <details className="settings-card">
      <summary className="settings-card__title">Anciennes archives locales</summary>
      <p className="settings-help settings-help--spaced">Téléchargez une archive, puis sélectionnez-la dans « Restaurer une configuration ». Seule sa configuration sera importée ; les secrets non récupérables seront signalés.</p>
      <div className="indexer-list">{backups.map(item => <div className="indexer-card" key={item.name}><div className="indexer-row indexer-row--settings">
        <span className="indexer-title card-meta__value">{item.name} — {fmtBytes(item.sizeBytes ?? item.size_bytes ?? 0)}</span>
        <div className="indexer-actions"><button className="btn" type="button" disabled={!!backupDownloadName || backupDeleteLoading} onClick={() => handleBackupDownload(item.name)}>{backupDownloadName === item.name ? "Téléchargement…" : "Télécharger"}</button>
        <button className="btn btn-danger" type="button" disabled={backupDeleteLoading || !!backupDownloadName || backupState?.isBusy || backupState?.needsRestart} onClick={() => openBackupDelete(item)}>Supprimer</button></div>
      </div></div>)}</div>
    </details>}
    <Modal open={!!backupDeleteOpen} title="Supprimer l’archive locale" onClose={closeBackupDelete} closeDisabled={backupDeleteLoading}>
      <p>Supprimer définitivement l’archive « {backupDeleteTarget?.name} » ? Cette action est irréversible.</p>
      {backupError && <InlineNotice variant="error" message={backupError} />}
      {backupDeleteLoading && <InlineNotice variant="info" message="Suppression de l’archive en cours…" />}
      <div className="formactions">
        <button className="btn" type="button" disabled={backupDeleteLoading} onClick={closeBackupDelete}>Annuler</button>
        <button className="btn btn-danger" type="button" disabled={backupDeleteLoading} onClick={handleBackupDelete}>{backupDeleteLoading ? "Suppression…" : "Supprimer"}</button>
      </div>
    </Modal>
  </>;
}
