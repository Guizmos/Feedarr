import React from "react";
import ConfigurationBackupPanel from "../../components/backup/ConfigurationBackupPanel.jsx";
import InlineNotice from "./InlineNotice.jsx";
import { fmtBytes } from "./settingsUtils.js";

export default function SettingsBackup({ backups = [], handleBackupDownload, backupDownloadName, backupError }) {
  return <>
    <ConfigurationBackupPanel />
    {backups.length > 0 && <details style={{ gridColumn: "1 / -1", marginTop: 16 }}>
      <summary>Anciennes archives locales</summary>
      {backupError && <InlineNotice variant="error" message={backupError} />}
      <p className="muted">Téléchargez une archive, puis sélectionnez-la dans « Restaurer une configuration ». Seule sa configuration sera importée ; les secrets non récupérables seront signalés.</p>
      {backups.map(item => <div className="settings-rowcard" key={item.name}>
        <span>{item.name} — {fmtBytes(item.sizeBytes ?? item.size_bytes ?? 0)}</span>
        <button className="btn" type="button" disabled={!!backupDownloadName} onClick={() => handleBackupDownload(item.name)}>Télécharger</button>
      </div>)}
    </details>}
  </>;
}
