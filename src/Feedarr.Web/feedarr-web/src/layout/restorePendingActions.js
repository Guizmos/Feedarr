export const restoreStatePath = "/api/system/backups/restore-state";
export const restoreChangedEvent = "restore:changed";

export async function cancelPreparedRestore(status, { confirm, cancel, refresh }) {
  if (!status?.pending || !status.id) return false;
  if (!await confirm("Annuler la restauration en attente ? La base actuelle restera inchangée.")) return false;
  await cancel(`/api/system/backups/pending-restore?id=${encodeURIComponent(status.id)}`);
  await refresh();
  return true;
}
