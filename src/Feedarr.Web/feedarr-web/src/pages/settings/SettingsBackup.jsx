import React from "react";
import ConfigurationBackupPanel from "../../components/backup/ConfigurationBackupPanel.jsx";
import InlineNotice from "./InlineNotice.jsx";
import { fmtBytes } from "./settingsUtils.js";

export default function SettingsBackup({ backups = [], handleBackupDownload, backupDownloadName, backupError }) {
  return <>
    <ConfigurationBackupPanel />
    {backups.length > 0 && <details className="settings-card settings-card--full">
      <summary className="settings-card__title">Anciennes archives locales</summary>
      {backupError && <InlineNotice variant="error" message={backupError} />}
      <p className="settings-help settings-help--spaced">Téléchargez une archive, puis sélectionnez-la dans « Restaurer une configuration ». Seule sa configuration sera importée ; les secrets non récupérables seront signalés.</p>
      <div className="indexer-list">{backups.map(item => <div className="indexer-card" key={item.name}><div className="indexer-row indexer-row--settings">
        <span className="indexer-title card-meta__value">{item.name} — {fmtBytes(item.sizeBytes ?? item.size_bytes ?? 0)}</span>
        <button className="btn" type="button" disabled={!!backupDownloadName} onClick={() => handleBackupDownload(item.name)}>{backupDownloadName === item.name ? "Téléchargement…" : "Télécharger"}</button>
      </div></div>)}</div>
    </details>}
  </>;
}
