import { useCallback, useState } from "react";
import { apiGet, apiPost } from "../../../api/client.js";
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
      await apiPost("/api/activity/purge?scope=logs", {});
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
  };
}
