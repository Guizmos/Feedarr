import { useCallback, useState } from "react";
import { apiDelete, apiGet, apiPost, resolveApiUrl } from "../../../api/client.js";
import { triggerPosterPolling } from "../../../hooks/usePosterPollingService.js";

export default function useMaintenanceActions() {
  // Cache
  const [clearCacheLoading, setClearCacheLoading] = useState(false);
  const [clearCacheOpen, setClearCacheOpen] = useState(false);

  // Logs (legacy simple purge)
  const [purgeLogsLoading, setPurgeLogsLoading] = useState(false);
  const [purgeLogsOpen, setPurgeLogsOpen] = useState(false);

  // Selective log purge
  const [purgeSelectiveOpen, setPurgeSelectiveOpen] = useState(false);
  const [purgeSelectiveLoading, setPurgeSelectiveLoading] = useState(false);
  const [purgeScope, setPurgeScope] = useState("all");
  const [purgeOlderThanDays, setPurgeOlderThanDays] = useState("");
  const [purgeSelectiveResult, setPurgeSelectiveResult] = useState(null);

  // Vacuum
  const [vacuumOpen, setVacuumOpen] = useState(false);
  const [vacuumLoading, setVacuumLoading] = useState(false);
  const [vacuumResult, setVacuumResult] = useState(null);

  // Stats
  const [stats, setStats] = useState(null);
  const [statsLoading, setStatsLoading] = useState(false);

  // Orphan poster cleanup
  const [cleanupPostersOpen, setCleanupPostersOpen] = useState(false);
  const [cleanupPostersLoading, setCleanupPostersLoading] = useState(false);
  const [cleanupPostersResult, setCleanupPostersResult] = useState(null);

  // Provider tests
  const [testProvidersLoading, setTestProvidersLoading] = useState(false);
  const [testProvidersResults, setTestProvidersResults] = useState(null);

  // Reparse titles
  const [reparseOpen, setReparseOpen] = useState(false);
  const [reparseLoading, setReparseLoading] = useState(false);
  const [reparseResult, setReparseResult] = useState(null);

  // Detect duplicates
  const [duplicatesLoading, setDuplicatesLoading] = useState(false);
  const [duplicatesResult, setDuplicatesResult] = useState(null);
  const [duplicatesPurgeOpen, setDuplicatesPurgeOpen] = useState(false);
  const [duplicatesPurgeLoading, setDuplicatesPurgeLoading] = useState(false);

  // Backups
  const [backupCreateOpen, setBackupCreateOpen] = useState(false);
  const [backupCreateLoading, setBackupCreateLoading] = useState(false);
  const [backups, setBackups] = useState([]);
  const [backupsLoading, setBackupsLoading] = useState(false);
  const [backupDownloadName, setBackupDownloadName] = useState("");
  const [backupRestoreOpen, setBackupRestoreOpen] = useState(false);
  const [backupRestoreTarget, setBackupRestoreTarget] = useState(null);
  const [backupRestoreLoading, setBackupRestoreLoading] = useState(false);
  const [backupDeleteOpen, setBackupDeleteOpen] = useState(false);
  const [backupDeleteTarget, setBackupDeleteTarget] = useState(null);
  const [backupDeleteLoading, setBackupDeleteLoading] = useState(false);
  const [backupError, setBackupError] = useState("");
  const [backupNotice, setBackupNotice] = useState("");
  const [backupState, setBackupState] = useState(null);
  const restartRequired = !!backupState?.needsRestart;
  const backupLockedByRestartMessage = "Redemarrage requis apres restauration. Action impossible avant redemarrage.";

  // Load backups
  const loadBackups = useCallback(async () => {
    setBackupsLoading(true);
    setBackupError("");
    try {
      const list = await apiGet("/api/system/backups");
      setBackups(Array.isArray(list) ? list : []);
    } catch (e) {
      setBackupError(e?.message || "Erreur chargement backups");
      setBackups([]);
    } finally {
      setBackupsLoading(false);
    }
  }, []);

  const loadBackupState = useCallback(async () => {
    try {
      const state = await apiGet("/api/system/backups/state");
      setBackupState(state || null);
    } catch {
      // no-op: state is optional for UX
    }
  }, []);

  // Load stats
  const loadStats = useCallback(async () => {
    setStatsLoading(true);
    try {
      const data = await apiGet("/api/maintenance/stats");
      setStats(data);
    } catch (e) {
      setStats(null);
    } finally {
      setStatsLoading(false);
    }
  }, []);

  // Cache functions
  async function handleClearCache(onReload) {
    setClearCacheLoading(true);
    try {
      await apiPost("/api/posters/cache/clear", {});
      triggerPosterPolling("cache-clear");
      setClearCacheOpen(false);
      if (onReload) await onReload();
    } catch (e) {
      throw new Error(e?.message || "Erreur vider cache posters");
    } finally {
      setClearCacheLoading(false);
    }
  }

  // Logs functions (legacy)
  async function handlePurgeLogs() {
    setPurgeLogsLoading(true);
    try {
      await apiPost("/api/activity/purge", {});
      setPurgeLogsOpen(false);
    } catch (e) {
      throw new Error(e?.message || "Erreur purge logs");
    } finally {
      setPurgeLogsLoading(false);
    }
  }

  // Selective purge
  async function handlePurgeSelective() {
    setPurgeSelectiveLoading(true);
    setPurgeSelectiveResult(null);
    try {
      const body = { scope: purgeScope };
      const days = parseInt(purgeOlderThanDays, 10);
      if (days > 0) body.olderThanDays = days;
      const res = await apiPost("/api/maintenance/purge-logs", body);
      setPurgeSelectiveResult(res);
      setPurgeSelectiveOpen(false);
      loadStats();
    } catch (e) {
      throw new Error(e?.message || "Erreur purge sélective");
    } finally {
      setPurgeSelectiveLoading(false);
    }
  }

  // Vacuum
  async function handleVacuum() {
    setVacuumLoading(true);
    setVacuumResult(null);
    try {
      const res = await apiPost("/api/maintenance/vacuum", {});
      setVacuumResult(res);
      setVacuumOpen(false);
      loadStats();
    } catch (e) {
      throw new Error(e?.message || "Erreur optimisation DB");
    } finally {
      setVacuumLoading(false);
    }
  }

  // Cleanup posters
  async function handleCleanupPosters() {
    setCleanupPostersLoading(true);
    setCleanupPostersResult(null);
    try {
      const res = await apiPost("/api/maintenance/cleanup-posters", {});
      setCleanupPostersResult(res);
      setCleanupPostersOpen(false);
      loadStats();
    } catch (e) {
      throw new Error(e?.message || "Erreur nettoyage posters");
    } finally {
      setCleanupPostersLoading(false);
    }
  }

  // Test providers
  async function handleTestProviders() {
    setTestProvidersLoading(true);
    setTestProvidersResults(null);
    try {
      const res = await apiPost("/api/maintenance/test-providers", {});
      setTestProvidersResults(res?.results || []);
    } catch (e) {
      setTestProvidersResults([]);
    } finally {
      setTestProvidersLoading(false);
    }
  }

  // Reparse titles
  async function handleReparse() {
    setReparseLoading(true);
    setReparseResult(null);
    try {
      const res = await apiPost("/api/maintenance/reparse-titles", {});
      setReparseResult(res);
      setReparseOpen(false);
      loadStats();
    } catch (e) {
      throw new Error(e?.message || "Erreur re-parsing");
    } finally {
      setReparseLoading(false);
    }
  }

  // Detect duplicates
  async function handleDetectDuplicates() {
    setDuplicatesLoading(true);
    setDuplicatesResult(null);
    try {
      const res = await apiPost("/api/maintenance/detect-duplicates", {});
      setDuplicatesResult(res);
    } catch (e) {
      setDuplicatesResult(null);
    } finally {
      setDuplicatesLoading(false);
    }
  }

  // Purge duplicates
  async function handlePurgeDuplicates() {
    setDuplicatesPurgeLoading(true);
    try {
      const res = await apiPost("/api/maintenance/detect-duplicates?purge=true", {});
      setDuplicatesResult(res);
      setDuplicatesPurgeOpen(false);
      loadStats();
    } catch (e) {
      throw new Error(e?.message || "Erreur purge doublons");
    } finally {
      setDuplicatesPurgeLoading(false);
    }
  }

  // Backup create functions
  function openBackupCreate() {
    if (restartRequired || backupState?.isBusy) return;
    setBackupCreateOpen(true);
  }

  function closeBackupCreate() {
    if (backupCreateLoading) return;
    setBackupCreateOpen(false);
  }

  async function handleBackupCreate() {
    if (backupState?.isBusy) return;
    if (restartRequired) {
      setBackupError(backupLockedByRestartMessage);
      return;
    }
    setBackupCreateLoading(true);
    setBackupError("");
    setBackupNotice("");
    try {
      await apiPost("/api/system/backups", {});
      setBackupCreateOpen(false);
      await loadBackups();
      await loadBackupState();
      setBackupNotice("Sauvegarde créée avec succès.");
    } catch (e) {
      setBackupError(e?.message || "Erreur sauvegarde");
    } finally {
      setBackupCreateLoading(false);
    }
  }

  // Backup download
  async function handleBackupDownload(name) {
    if (restartRequired) {
      setBackupError(backupLockedByRestartMessage);
      return;
    }
    setBackupDownloadName(name);
    setBackupError("");
    setBackupNotice("");
    try {
      const response = await fetch(
        resolveApiUrl(`/api/system/backups/${encodeURIComponent(name)}/download`),
        { method: "GET", credentials: "include" }
      );
      if (!response.ok) {
        const ct = response.headers.get("content-type") || "";
        if (ct.includes("application/json")) {
          let msg = "";
          try {
            const payload = await response.json();
            msg = payload?.error || payload?.message || "";
          } catch {
            // keep default empty message
          }
          throw new Error(msg ? `HTTP ${response.status} - ${msg}` : `HTTP ${response.status}`);
        }

        const text = await response.text();
        const cleaned = String(text || "").replace(/\s+/g, " ").trim();
        throw new Error(cleaned ? `HTTP ${response.status} - ${cleaned.slice(0, 180)}` : `HTTP ${response.status}`);
      }

      const blob = await response.blob();
      const url = window.URL.createObjectURL(blob);
      const a = document.createElement("a");
      a.href = url;
      a.download = name;
      document.body.appendChild(a);
      a.click();
      window.URL.revokeObjectURL(url);
      document.body.removeChild(a);
      setBackupNotice("Téléchargement terminé.");
    } catch (e) {
      setBackupError(e?.message || "Erreur telechargement backup");
    } finally {
      setBackupDownloadName("");
    }
  }

  // Backup restore functions
  function openBackupRestore(item) {
    if (restartRequired || backupState?.isBusy) return;
    setBackupRestoreTarget(item);
    setBackupRestoreOpen(true);
  }

  function closeBackupRestore() {
    if (backupRestoreLoading) return;
    setBackupRestoreOpen(false);
    setBackupRestoreTarget(null);
  }

  async function handleBackupRestore() {
    if (!backupRestoreTarget) return;
    if (backupState?.isBusy) return;
    if (restartRequired) {
      setBackupError(backupLockedByRestartMessage);
      return;
    }
    setBackupRestoreLoading(true);
    setBackupError("");
    setBackupNotice("");
    try {
      const res = await apiPost(
        `/api/system/backups/${encodeURIComponent(backupRestoreTarget.name)}/restore`,
        {}
      );
      const notices = [];
      if (res?.needsRestart) {
        notices.push("Restauration terminée. Redémarrage de l'application requis.");
      } else {
        notices.push("Restauration terminée.");
      }
      if (res?.warning) notices.push(res.warning);
      setBackupNotice(notices.join(" "));
      setBackupRestoreOpen(false);
      setBackupRestoreTarget(null);
      await loadBackups();
      await loadBackupState();
    } catch (e) {
      setBackupError(e?.message || "Erreur restauration backup");
    } finally {
      setBackupRestoreLoading(false);
    }
  }

  // Backup delete functions
  function openBackupDelete(item) {
    if (restartRequired || backupState?.isBusy) return;
    setBackupDeleteTarget(item);
    setBackupDeleteOpen(true);
  }

  function closeBackupDelete() {
    setBackupDeleteOpen(false);
    setBackupDeleteTarget(null);
  }

  async function handleBackupDelete() {
    if (!backupDeleteTarget) return;
    if (backupState?.isBusy) return;
    if (restartRequired) {
      setBackupError(backupLockedByRestartMessage);
      return;
    }
    setBackupDeleteLoading(true);
    setBackupError("");
    setBackupNotice("");
    try {
      await apiDelete(`/api/system/backups/${encodeURIComponent(backupDeleteTarget.name)}`);
      closeBackupDelete();
      await loadBackups();
      await loadBackupState();
      setBackupNotice("Sauvegarde supprimée.");
    } catch (e) {
      setBackupError(e?.message || "Erreur suppression backup");
    } finally {
      setBackupDeleteLoading(false);
    }
  }

  return {
    // Cache
    clearCacheLoading,
    clearCacheOpen,
    setClearCacheOpen,
    handleClearCache,
    // Logs (legacy)
    purgeLogsLoading,
    purgeLogsOpen,
    setPurgeLogsOpen,
    handlePurgeLogs,
    // Selective purge
    purgeSelectiveOpen,
    setPurgeSelectiveOpen,
    purgeSelectiveLoading,
    purgeScope,
    setPurgeScope,
    purgeOlderThanDays,
    setPurgeOlderThanDays,
    purgeSelectiveResult,
    handlePurgeSelective,
    // Vacuum
    vacuumOpen,
    setVacuumOpen,
    vacuumLoading,
    vacuumResult,
    handleVacuum,
    // Stats
    stats,
    statsLoading,
    loadStats,
    // Orphan cleanup
    cleanupPostersOpen,
    setCleanupPostersOpen,
    cleanupPostersLoading,
    cleanupPostersResult,
    handleCleanupPosters,
    // Test providers
    testProvidersLoading,
    testProvidersResults,
    handleTestProviders,
    // Reparse titles
    reparseOpen,
    setReparseOpen,
    reparseLoading,
    reparseResult,
    handleReparse,
    // Detect duplicates
    duplicatesLoading,
    duplicatesResult,
    handleDetectDuplicates,
    duplicatesPurgeOpen,
    setDuplicatesPurgeOpen,
    duplicatesPurgeLoading,
    handlePurgeDuplicates,
    // Backups
    backups,
    backupsLoading,
    backupError,
    backupNotice,
    backupState,
    loadBackups,
    loadBackupState,
    backupCreateOpen,
    backupCreateLoading,
    openBackupCreate,
    closeBackupCreate,
    handleBackupCreate,
    backupDownloadName,
    handleBackupDownload,
    backupRestoreOpen,
    backupRestoreTarget,
    backupRestoreLoading,
    openBackupRestore,
    closeBackupRestore,
    handleBackupRestore,
    backupDeleteOpen,
    backupDeleteTarget,
    backupDeleteLoading,
    openBackupDelete,
    closeBackupDelete,
    handleBackupDelete,
  };
}
