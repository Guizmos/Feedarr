import React from "react";
import Modal from "../../ui/Modal.jsx";
import { fmtBytes, fmtDateFromTs } from "./settingsUtils.js";
import AppIcon from "../../ui/AppIcon.jsx";

function BackupIconBtn({ icon, title, onClick, disabled, className, spinning }) {
  return (
    <button
      className={`iconbtn${className ? ` ${className}` : ""}${spinning ? " iconbtn--spin" : ""}`}
      onClick={onClick}
      disabled={disabled}
      title={title}
      style={{ flexShrink: 0 }}
    >
      <AppIcon name={spinning ? "progress_activity" : icon} />
    </button>
  );
}

export default function SettingsBackup({
  backupCreateOpen,
  backupCreateLoading,
  backups,
  backupsLoading,
  backupDownloadName,
  backupRestoreOpen,
  backupRestoreTarget,
  backupRestoreLoading,
  backupDeleteOpen,
  backupDeleteTarget,
  backupDeleteLoading,
  backupError,
  backupNotice,
  backupState,
  closeBackupCreate,
  handleBackupCreate,
  handleBackupDownload,
  openBackupRestore,
  closeBackupRestore,
  handleBackupRestore,
  openBackupDelete,
  closeBackupDelete,
  handleBackupDelete,
}) {
  const operationBusy = !!backupState?.isBusy;
  const restartRequired = !!backupState?.needsRestart;
  const isBusy = restartRequired || operationBusy || backupCreateLoading || backupRestoreLoading || backupDeleteLoading || !!backupDownloadName;

  return (
    <>
      {backupError && (
        <div className="onboarding__error" style={{ marginBottom: 12, gridColumn: "1 / -1" }}>
          {backupError}
        </div>
      )}
      {backupNotice && (
        <div className="onboarding__ok" style={{ marginBottom: 12, gridColumn: "1 / -1" }}>
          {backupNotice}
        </div>
      )}
      {restartRequired && (
        <div className="onboarding__warn" style={{ marginBottom: 12, gridColumn: "1 / -1" }}>
          Redemarrage requis apres restauration. Les actions de sauvegarde sont verrouillees jusqu'au redemarrage.
        </div>
      )}
      {operationBusy && (
        <div className="onboarding__warn" style={{ marginBottom: 12, gridColumn: "1 / -1" }}>
          Opération en cours: <strong>{backupState?.operation || "backup"}</strong> ({backupState?.phase || "running"}).
          Les actions de sauvegarde sont temporairement verrouillées.
        </div>
      )}
      <div className="indexer-list" style={{ gridColumn: "1 / -1" }}>
        {backupsLoading ? (
          <div className="indexer-card">
            <div className="indexer-row">
              <span className="indexer-url muted">Chargement...</span>
            </div>
          </div>
        ) : backups.length === 0 ? (
          <div className="indexer-card">
            <div className="indexer-row">
              <span className="indexer-url muted">Aucune sauvegarde présente</span>
            </div>
          </div>
        ) : (
          backups.map((item, idx) => {
            const sizeLabel = fmtBytes(item?.sizeBytes ?? item?.size_bytes ?? 0) || "-";
            const dateLabel = fmtDateFromTs(item?.createdAtTs ?? item?.created_at_ts ?? 0) || "-";
            const isDownloading = backupDownloadName === item.name;
            return (
              <div
                key={item.name}
                className="settings-rowcard"
                style={{
                  padding: "10px 16px",
                  display: "flex",
                  alignItems: "center",
                  gap: 16,
                  overflow: "hidden",
                  minWidth: 0,
                }}
              >
                {/* Dot */}
                <span style={{
                  width: 10, height: 10, borderRadius: "50%",
                  background: "#22c55e", flexShrink: 0,
                }} />

                {/* Number */}
                <span style={{
                  fontSize: 16, fontWeight: 800, flexShrink: 0,
                  width: 20, textAlign: "left",
                }}>
                  {idx + 1}
                </span>

                {/* Filename — flex-shrink, truncate with ellipsis */}
                <span
                  title={item.name}
                  style={{
                    flex: "1 1 0px",
                    minWidth: 0,
                    overflow: "hidden",
                    textOverflow: "ellipsis",
                    whiteSpace: "nowrap",
                    fontSize: 13,
                    fontWeight: 600,
                  }}
                >
                  {item.name}
                </span>

                {/* Size pill */}
                <span className="pill pill-muted" style={{ flexShrink: 0 }}>
                  {sizeLabel}
                </span>

                {/* Date pill */}
                <span className="pill pill-muted" style={{ flexShrink: 0 }}>
                  {dateLabel}
                </span>

                {/* Actions */}
                <div style={{ display: "flex", alignItems: "center", gap: 8, flexShrink: 0, marginLeft: "auto" }}>
                  <BackupIconBtn
                    icon="download"
                    title={isDownloading ? "Téléchargement..." : "Télécharger"}
                    onClick={() => handleBackupDownload(item.name)}
                    disabled={isBusy}
                    spinning={isDownloading}
                  />
                  <BackupIconBtn
                    icon="restore"
                    title="Restaurer"
                    onClick={() => openBackupRestore(item)}
                    disabled={isBusy}
                    className="iconbtn--warn"
                  />
                  <BackupIconBtn
                    icon="delete"
                    title="Supprimer"
                    onClick={() => openBackupDelete(item)}
                    disabled={isBusy}
                    className="iconbtn--danger"
                  />
                </div>
              </div>
            );
          })
        )}
      </div>

      <Modal open={backupCreateOpen} title="Nouvelle sauvegarde" onClose={closeBackupCreate} width={520}>
        <div style={{ padding: 12 }}>
          <div style={{ fontWeight: 700, marginBottom: 8 }}>Créer une nouvelle sauvegarde ?</div>
          <div className="muted" style={{ marginBottom: 12 }}>
            Cette action crée une archive ZIP locale contenant la base de données et les métadonnées de configuration
            (sans exposer les secrets en clair dans `config.json`).
          </div>
          <div className="formactions">
            <button
              className="btn btn-accent"
              type="button"
              onClick={handleBackupCreate}
              disabled={backupCreateLoading || isBusy}
            >
              {backupCreateLoading ? "Sauvegarde..." : "Créer"}
            </button>
            <button className="btn" type="button" onClick={closeBackupCreate} disabled={backupCreateLoading}>
              Annuler
            </button>
          </div>
        </div>
      </Modal>

      <Modal open={backupRestoreOpen} title="Restaurer une sauvegarde" onClose={closeBackupRestore} width={640}>
        <div className="backup-confirm-modal__content">
          <div className="backup-confirm-modal__lead">Confirmer la restauration ?</div>
          <div className="muted backup-confirm-modal__text">
            Cette action remplace la base actuelle par la sauvegarde :
            <strong className="backup-confirm-modal__name">{backupRestoreTarget?.name || "-"}</strong>
            Un redémarrage de l'application sera requis après restauration.
          </div>
          <div className="formactions">
            <button
              className="btn btn-danger"
              type="button"
              onClick={handleBackupRestore}
              disabled={backupRestoreLoading || isBusy}
            >
              {backupRestoreLoading ? "Restauration..." : "Restaurer"}
            </button>
            <button className="btn" type="button" onClick={closeBackupRestore} disabled={backupRestoreLoading}>
              Annuler
            </button>
          </div>
        </div>
      </Modal>

      <Modal open={backupDeleteOpen} title="Supprimer une sauvegarde" onClose={closeBackupDelete} width={640}>
        <div className="backup-confirm-modal__content">
          <div className="backup-confirm-modal__lead">Confirmer la suppression ?</div>
          <div className="muted backup-confirm-modal__text">
            Cette action va supprimer la sauvegarde :
            <strong className="backup-confirm-modal__name">{backupDeleteTarget?.name || "-"}</strong>
            Cette action est définitive.
          </div>
          <div className="formactions">
            <button className="btn btn-danger" type="button" onClick={handleBackupDelete} disabled={backupDeleteLoading || isBusy}>
              {backupDeleteLoading ? "Suppression..." : "Supprimer"}
            </button>
            <button className="btn" type="button" onClick={closeBackupDelete} disabled={backupDeleteLoading}>
              Annuler
            </button>
          </div>
        </div>
      </Modal>
    </>
  );
}
