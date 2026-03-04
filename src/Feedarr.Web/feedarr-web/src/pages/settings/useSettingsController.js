import { useCallback, useEffect, useRef, useState } from "react";
import { apiGet } from "../../api/client.js";
import { sleep } from "./settingsUtils.js";

// Specialized hooks
import useExternalProviderInstances from "./hooks/useExternalProviderInstances.js";
import useMaintenanceActions from "./hooks/useMaintenanceActions.js";
import useMaintenanceSettings from "./hooks/useMaintenanceSettings.js";

const settingsTitleBySection = {
  general: "Paramètres",
  ui: "UI",
  externals: "Métadonnées",
  applications: "Applications",
  users: "Sécurité",
  maintenance: "Maintenance",
  backup: "Sauvegarde",
};

export default function useSettingsController(section = "general") {
  const showGeneral = section === "general";
  const showUi = false;
  const showExternals = section === "externals";
  const showApplications = false;
  const showUsers = false;
  const showMaintenance = section === "maintenance";
  const showBackup = section === "backup";
  const settingsTitle = settingsTitleBySection[section] || "Paramètres";

  // Global state
  const [loading, setLoading] = useState(true);
  const [err, setErr] = useState("");
  const [saveState, setSaveState] = useState("idle");
  const [hostPort, setHostPort] = useState("");
  const [initialHostPort, setInitialHostPort] = useState("");
  const [urlBase] = useState("");

  // Compose specialized hooks
  const providers = useExternalProviderInstances();
  const maintenance = useMaintenanceActions();
  const maintenanceSettings = useMaintenanceSettings();

  // Extract stable function refs to avoid infinite loops
  const loadExternalFlagsRef = useRef(providers.loadExternalFlags);
  const loadProviderStatsRef = useRef(providers.loadProviderStats);
  const saveExternalKeysRef = useRef(providers.saveExternalKeys);
  const setReleasesCountRef = useRef(providers.setReleasesCount);
  const loadBackupsRef = useRef(maintenance.loadBackups);
  const loadBackupStateRef = useRef(maintenance.loadBackupState);
  const loadMaintenanceSettingsRef = useRef(maintenanceSettings.loadMaintenanceSettings);

  // Additional refs for action handlers
  const handleClearCacheRef = useRef(maintenance.handleClearCache);
  const handlePurgeLogsRef = useRef(maintenance.handlePurgeLogs);
  const handleBackupCreateRef = useRef(maintenance.handleBackupCreate);
  const handleBackupDownloadRef = useRef(maintenance.handleBackupDownload);
  const handleBackupRestoreRef = useRef(maintenance.handleBackupRestore);
  const handleBackupDeleteRef = useRef(maintenance.handleBackupDelete);
  const loadStatsRef = useRef(maintenance.loadStats);
  const handleVacuumRef = useRef(maintenance.handleVacuum);
  const handlePurgeSelectiveRef = useRef(maintenance.handlePurgeSelective);
  const handleCleanupPostersRef = useRef(maintenance.handleCleanupPosters);
  const handleTestProvidersRef = useRef(maintenance.handleTestProviders);
  const handleReparseRef = useRef(maintenance.handleReparse);
  const handleDetectDuplicatesRef = useRef(maintenance.handleDetectDuplicates);
  const handlePurgeDuplicatesRef = useRef(maintenance.handlePurgeDuplicates);
  const saveMaintenanceSettingsRef = useRef(maintenanceSettings.saveMaintenanceSettings);
  const confirmExternalDeleteRef = useRef(providers.confirmExternalDelete);
  const confirmExternalToggleRef = useRef(providers.confirmExternalToggle);

  // Sync all refs in a single render-phase update (refs don't trigger re-renders)
  // This replaces 23 individual useEffect hooks with direct assignment
  loadExternalFlagsRef.current = providers.loadExternalFlags;
  loadProviderStatsRef.current = providers.loadProviderStats;
  saveExternalKeysRef.current = providers.saveExternalKeys;
  setReleasesCountRef.current = providers.setReleasesCount;
  loadBackupsRef.current = maintenance.loadBackups;
  loadBackupStateRef.current = maintenance.loadBackupState;
  loadMaintenanceSettingsRef.current = maintenanceSettings.loadMaintenanceSettings;
  handleClearCacheRef.current = maintenance.handleClearCache;
  handlePurgeLogsRef.current = maintenance.handlePurgeLogs;
  handleBackupCreateRef.current = maintenance.handleBackupCreate;
  handleBackupDownloadRef.current = maintenance.handleBackupDownload;
  handleBackupRestoreRef.current = maintenance.handleBackupRestore;
  handleBackupDeleteRef.current = maintenance.handleBackupDelete;
  loadStatsRef.current = maintenance.loadStats;
  handleVacuumRef.current = maintenance.handleVacuum;
  handlePurgeSelectiveRef.current = maintenance.handlePurgeSelective;
  handleCleanupPostersRef.current = maintenance.handleCleanupPosters;
  handleTestProvidersRef.current = maintenance.handleTestProviders;
  handleReparseRef.current = maintenance.handleReparse;
  handleDetectDuplicatesRef.current = maintenance.handleDetectDuplicates;
  handlePurgeDuplicatesRef.current = maintenance.handlePurgeDuplicates;
  saveMaintenanceSettingsRef.current = maintenanceSettings.saveMaintenanceSettings;
  confirmExternalDeleteRef.current = providers.confirmExternalDelete;
  confirmExternalToggleRef.current = providers.confirmExternalToggle;

  // Calculate isDirty with safe property access
  const isDirty = maintenanceSettings.isDirty;
  const isSaveBlocked = false;
  const canSave = isDirty && !isSaveBlocked;

  // Load all settings
  const load = useCallback(async () => {
    setLoading(true);
    setErr("");
    try {
      // Load system status for releasesCount
      const status = await apiGet("/api/system/status");
      setReleasesCountRef.current(Number(status?.releasesCount ?? status?.ReleasesCount ?? 0));

      // Load all settings in parallel
      await Promise.all([
        loadExternalFlagsRef.current(),
        loadProviderStatsRef.current(),
      ]);
    } catch (e) {
      setErr(e?.message || "Erreur chargement settings");
    } finally {
      setLoading(false);
    }
  }, []);

  // Handle save
  const performSave = useCallback(async () => {
    setErr("");
    const startedAt = Date.now();
    let ok = false;
    setSaveState("loading");

    try {
      // Save all settings
      await saveExternalKeysRef.current();
      await saveMaintenanceSettingsRef.current();

      // Handle port change redirect
      if (typeof window !== "undefined") {
        const nextPort = String(hostPort || "").trim();
        const prevPort = String(initialHostPort || "").trim();
        if (nextPort && nextPort !== prevPort) {
          const { protocol, hostname, pathname, search, hash } = window.location;
          const nextUrl = `${protocol}//${hostname}:${nextPort}${pathname}${search}${hash}`;
          window.location.assign(nextUrl);
          return;
        }
      }

      ok = true;
    } catch (e) {
      if (!e?.isMaintenanceSettingsError) {
        setErr(e?.message || "Erreur sauvegarde settings");
      }
    } finally {
      const elapsed = Date.now() - startedAt;
      if (elapsed < 1000) {
        await sleep(1000 - elapsed);
      }
      setSaveState(ok ? "success" : "error");
      setTimeout(() => setSaveState("idle"), 1000);
    }
  }, [hostPort, initialHostPort]);

  const handleSave = useCallback(async () => {
    if (!canSave) return;
    await performSave();
  }, [canSave, performSave]);

  // Handle refresh
  const handleRefresh = useCallback(() => {
    load();
    if (showBackup) {
      loadBackupsRef.current();
      loadBackupStateRef.current();
    }
    if (showMaintenance) {
      loadStatsRef.current();
      loadMaintenanceSettingsRef.current();
    }
  }, [load, showBackup, showMaintenance]);

  // Initial load - run only once
  const hasLoadedRef = useRef(false);
  useEffect(() => {
    if (hasLoadedRef.current) return;
    hasLoadedRef.current = true;
    load();
  }, [load]);

  // Load backup data when section is active
  const backupLoadedRef = useRef(false);
  useEffect(() => {
    if (!showBackup) {
      backupLoadedRef.current = false;
      return;
    }
    if (backupLoadedRef.current) return;
    backupLoadedRef.current = true;
    loadBackupsRef.current();
    loadBackupStateRef.current();
  }, [showBackup]);

  useEffect(() => {
    if (!showBackup) return;

    const timer = setInterval(() => {
      loadBackupStateRef.current();
    }, 3000);

    return () => clearInterval(timer);
  }, [showBackup]);

  // Load maintenance stats when section is active
  const maintenanceLoadedRef = useRef(false);
  useEffect(() => {
    if (!showMaintenance) {
      maintenanceLoadedRef.current = false;
      return;
    }
    if (maintenanceLoadedRef.current) return;
    maintenanceLoadedRef.current = true;
    loadStatsRef.current();
    loadMaintenanceSettingsRef.current();
  }, [showMaintenance]);

  // Set initial host port
  useEffect(() => {
    if (typeof window === "undefined") return;
    const currentPort =
      window.location.port || (window.location.protocol === "https:" ? "443" : "80");
    if (!hostPort) setHostPort(currentPort);
    if (!initialHostPort) setInitialHostPort(currentPort);
  }, [hostPort, initialHostPort]);

  // Wrap maintenance handlers with error handling
  const handleClearCache = useCallback(async () => {
    try {
      await handleClearCacheRef.current(load);
    } catch (e) {
      setErr(e?.message || "Erreur");
    }
  }, [load]);

  const handlePurgeLogs = useCallback(async () => {
    try {
      await handlePurgeLogsRef.current();
    } catch (e) {
      setErr(e?.message || "Erreur");
    }
  }, []);

  const handleVacuum = useCallback(async () => {
    try {
      await handleVacuumRef.current();
    } catch (e) {
      setErr(e?.message || "Erreur");
    }
  }, []);

  const handlePurgeSelective = useCallback(async () => {
    try {
      await handlePurgeSelectiveRef.current();
    } catch (e) {
      setErr(e?.message || "Erreur");
    }
  }, []);

  const handleCleanupPosters = useCallback(async () => {
    try {
      await handleCleanupPostersRef.current();
    } catch (e) {
      setErr(e?.message || "Erreur");
    }
  }, []);

  const handleTestProviders = useCallback(async () => {
    try {
      await handleTestProvidersRef.current();
    } catch (e) {
      setErr(e?.message || "Erreur");
    }
  }, []);

  const handleReparse = useCallback(async () => {
    try {
      await handleReparseRef.current();
    } catch (e) {
      setErr(e?.message || "Erreur");
    }
  }, []);

  const handleDetectDuplicates = useCallback(async () => {
    try {
      await handleDetectDuplicatesRef.current();
    } catch (e) {
      setErr(e?.message || "Erreur");
    }
  }, []);

  const handlePurgeDuplicates = useCallback(async () => {
    try {
      await handlePurgeDuplicatesRef.current();
    } catch (e) {
      setErr(e?.message || "Erreur");
    }
  }, []);

  const handleBackupCreate = useCallback(async () => {
    try {
      await handleBackupCreateRef.current();
    } catch (e) {
      setErr(e?.message || "Erreur");
    }
  }, []);

  const handleBackupDownload = useCallback(async (name) => {
    try {
      await handleBackupDownloadRef.current(name);
    } catch (e) {
      setErr(e?.message || "Erreur");
    }
  }, []);

  const handleBackupRestore = useCallback(async () => {
    try {
      await handleBackupRestoreRef.current();
    } catch (e) {
      setErr(e?.message || "Erreur");
    }
  }, []);

  const handleBackupDelete = useCallback(async () => {
    try {
      await handleBackupDeleteRef.current();
    } catch (e) {
      setErr(e?.message || "Erreur");
    }
  }, []);

  // Wrap provider handlers with error handling
  const confirmExternalDelete = useCallback(async () => {
    try {
      await confirmExternalDeleteRef.current();
    } catch (e) {
      setErr(e?.message || "Erreur");
    }
  }, []);

  const confirmExternalToggle = useCallback(async () => {
    try {
      await confirmExternalToggleRef.current();
    } catch (e) {
      setErr(e?.message || "Erreur");
    }
  }, []);

  return {
    section,
    settingsTitle,
    showGeneral,
    showUi,
    showExternals,
    showApplications,
    showUsers,
    showMaintenance,
    showBackup,
    loading,
    err,
    handleRefresh,
    handleSave,
    saveState,
    isDirty,
    isSaveBlocked,
    canSave,
    openArrModalAdd: null,
    canAddArrApp: false,
    openExternalModalAdd: providers.openExternalModalAdd,
    canAddExternalProvider: providers.canAddExternalProvider,
    triggerArrSync: async () => undefined,
    arrSyncing: false,
    hasEnabledArrApps: false,
    general: {
      hostPort,
      urlBase,
    },
    ui: null,
    providers: {
      ...providers,
      confirmExternalDelete,
      confirmExternalToggle,
    },
    applications: {},
    maintenance: {
      // Cache
      clearCacheLoading: maintenance.clearCacheLoading,
      clearCacheOpen: maintenance.clearCacheOpen,
      setClearCacheOpen: maintenance.setClearCacheOpen,
      handleClearCache,
      // Logs (legacy)
      purgeLogsLoading: maintenance.purgeLogsLoading,
      purgeLogsOpen: maintenance.purgeLogsOpen,
      setPurgeLogsOpen: maintenance.setPurgeLogsOpen,
      handlePurgeLogs,
      // Selective purge
      purgeSelectiveOpen: maintenance.purgeSelectiveOpen,
      setPurgeSelectiveOpen: maintenance.setPurgeSelectiveOpen,
      purgeSelectiveLoading: maintenance.purgeSelectiveLoading,
      purgeScope: maintenance.purgeScope,
      setPurgeScope: maintenance.setPurgeScope,
      purgeOlderThanDays: maintenance.purgeOlderThanDays,
      setPurgeOlderThanDays: maintenance.setPurgeOlderThanDays,
      purgeSelectiveResult: maintenance.purgeSelectiveResult,
      handlePurgeSelective,
      // Vacuum
      vacuumOpen: maintenance.vacuumOpen,
      setVacuumOpen: maintenance.setVacuumOpen,
      vacuumLoading: maintenance.vacuumLoading,
      vacuumResult: maintenance.vacuumResult,
      handleVacuum,
      // Stats
      stats: maintenance.stats,
      statsLoading: maintenance.statsLoading,
      loadStats: maintenance.loadStats,
      // Orphan cleanup
      cleanupPostersOpen: maintenance.cleanupPostersOpen,
      setCleanupPostersOpen: maintenance.setCleanupPostersOpen,
      cleanupPostersLoading: maintenance.cleanupPostersLoading,
      cleanupPostersResult: maintenance.cleanupPostersResult,
      handleCleanupPosters,
      // Test providers
      testProvidersLoading: maintenance.testProvidersLoading,
      testProvidersResults: maintenance.testProvidersResults,
      handleTestProviders,
      // Reparse titles
      reparseOpen: maintenance.reparseOpen,
      setReparseOpen: maintenance.setReparseOpen,
      reparseLoading: maintenance.reparseLoading,
      reparseResult: maintenance.reparseResult,
      handleReparse,
      // Detect duplicates
      duplicatesLoading: maintenance.duplicatesLoading,
      duplicatesResult: maintenance.duplicatesResult,
      handleDetectDuplicates,
      duplicatesPurgeOpen: maintenance.duplicatesPurgeOpen,
      setDuplicatesPurgeOpen: maintenance.setDuplicatesPurgeOpen,
      duplicatesPurgeLoading: maintenance.duplicatesPurgeLoading,
      handlePurgeDuplicates,
      maintenanceSettings: maintenanceSettings.maintenanceSettings,
      setMaintenanceSettings: maintenanceSettings.setMaintenanceSettings,
      initialMaintenanceSettings: maintenanceSettings.initialMaintenanceSettings,
      maintenanceFieldErrors: maintenanceSettings.fieldErrors,
      maintenanceSaveError: maintenanceSettings.saveError,
      maintenancePulseKinds: maintenanceSettings.pulseKinds,
      maintenanceSettingsDirty: maintenanceSettings.isDirty,
      restoreRecommendedDefaults: maintenanceSettings.restoreRecommendedDefaults,
      toggleAdvancedOptions: maintenanceSettings.toggleAdvancedOptions,
    },
    backup: {
      backupCreateOpen: maintenance.backupCreateOpen,
      backupCreateLoading: maintenance.backupCreateLoading,
      backups: maintenance.backups,
      backupsLoading: maintenance.backupsLoading,
      backupDownloadName: maintenance.backupDownloadName,
      backupRestoreOpen: maintenance.backupRestoreOpen,
      backupRestoreTarget: maintenance.backupRestoreTarget,
      backupRestoreLoading: maintenance.backupRestoreLoading,
      backupDeleteOpen: maintenance.backupDeleteOpen,
      backupDeleteTarget: maintenance.backupDeleteTarget,
      backupDeleteLoading: maintenance.backupDeleteLoading,
      backupError: maintenance.backupError,
      backupNotice: maintenance.backupNotice,
      backupState: maintenance.backupState,
      loadBackups: maintenance.loadBackups,
      loadBackupState: maintenance.loadBackupState,
      openBackupCreate: maintenance.openBackupCreate,
      closeBackupCreate: maintenance.closeBackupCreate,
      handleBackupCreate,
      handleBackupDownload,
      openBackupRestore: maintenance.openBackupRestore,
      closeBackupRestore: maintenance.closeBackupRestore,
      handleBackupRestore,
      openBackupDelete: maintenance.openBackupDelete,
      closeBackupDelete: maintenance.closeBackupDelete,
      handleBackupDelete,
    },
    users: null,
  };
}
