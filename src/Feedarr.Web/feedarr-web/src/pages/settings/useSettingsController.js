import { useCallback, useEffect, useRef, useState } from "react";
import { apiGet } from "../../api/client.js";
import { applyTheme } from "../../app/theme.js";
import { sleep } from "./settingsUtils.js";

// Specialized hooks
import useUiSettings from "./hooks/useUiSettings.js";
import useExternalProviderInstances from "./hooks/useExternalProviderInstances.js";
import useArrApplications from "./hooks/useArrApplications.js";
import useMaintenanceActions from "./hooks/useMaintenanceActions.js";
import useSecuritySettings from "./hooks/useSecuritySettings.js";

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
  const showUi = section === "ui";
  const showExternals = section === "externals";
  const showApplications = section === "applications";
  const showUsers = section === "users";
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
  const uiSettings = useUiSettings();
  const providers = useExternalProviderInstances();
  const applications = useArrApplications();
  const maintenance = useMaintenanceActions();
  const security = useSecuritySettings();

  // Extract stable function refs to avoid infinite loops
  const loadUiSettingsRef = useRef(uiSettings.loadUiSettings);
  const saveUiSettingsRef = useRef(uiSettings.saveUiSettings);
  const loadExternalFlagsRef = useRef(providers.loadExternalFlags);
  const loadProviderStatsRef = useRef(providers.loadProviderStats);
  const saveExternalKeysRef = useRef(providers.saveExternalKeys);
  const setReleasesCountRef = useRef(providers.setReleasesCount);
  const loadSecuritySettingsRef = useRef(security.loadSecuritySettings);
  const saveSecuritySettingsRef = useRef(security.saveSecuritySettings);
  const loadBackupsRef = useRef(maintenance.loadBackups);
  const loadBackupStateRef = useRef(maintenance.loadBackupState);
  const loadArrAppsRef = useRef(applications.loadArrApps);
  const loadArrSyncSettingsRef = useRef(applications.loadArrSyncSettings);
  const loadArrSyncStatusRef = useRef(applications.loadArrSyncStatus);

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
  const triggerArrSyncRef = useRef(applications.triggerArrSync);
  const saveArrSyncSettingsRef = useRef(applications.saveArrSyncSettings);
  const saveArrRequestModeDraftRef = useRef(applications.saveArrRequestModeDraft);
  const confirmArrDeleteRef = useRef(applications.confirmArrDelete);
  const toggleArrEnabledRef = useRef(applications.toggleArrEnabled);
  const confirmExternalDeleteRef = useRef(providers.confirmExternalDelete);
  const confirmExternalToggleRef = useRef(providers.confirmExternalToggle);

  // Sync all refs in a single render-phase update (refs don't trigger re-renders)
  // This replaces 23 individual useEffect hooks with direct assignment
  loadUiSettingsRef.current = uiSettings.loadUiSettings;
  saveUiSettingsRef.current = uiSettings.saveUiSettings;
  loadExternalFlagsRef.current = providers.loadExternalFlags;
  loadProviderStatsRef.current = providers.loadProviderStats;
  saveExternalKeysRef.current = providers.saveExternalKeys;
  setReleasesCountRef.current = providers.setReleasesCount;
  loadSecuritySettingsRef.current = security.loadSecuritySettings;
  saveSecuritySettingsRef.current = security.saveSecuritySettings;
  loadBackupsRef.current = maintenance.loadBackups;
  loadBackupStateRef.current = maintenance.loadBackupState;
  loadArrAppsRef.current = applications.loadArrApps;
  loadArrSyncSettingsRef.current = applications.loadArrSyncSettings;
  loadArrSyncStatusRef.current = applications.loadArrSyncStatus;
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
  triggerArrSyncRef.current = applications.triggerArrSync;
  saveArrSyncSettingsRef.current = applications.saveArrSyncSettings;
  saveArrRequestModeDraftRef.current = applications.saveArrRequestModeDraft;
  confirmArrDeleteRef.current = applications.confirmArrDelete;
  toggleArrEnabledRef.current = applications.toggleArrEnabled;
  confirmExternalDeleteRef.current = providers.confirmExternalDelete;
  confirmExternalToggleRef.current = providers.confirmExternalToggle;

  // Calculate isDirty with safe property access
  const isDirty =
    uiSettings.isDirty ||
    security.isDirty ||
    applications.isRequestModeDirty;
  const isSaveBlocked = showUsers && !security.canSave;

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
        loadUiSettingsRef.current(),
        loadExternalFlagsRef.current(),
        loadProviderStatsRef.current(),
        loadSecuritySettingsRef.current(),
      ]);
    } catch (e) {
      setErr(e?.message || "Erreur chargement settings");
    } finally {
      setLoading(false);
    }
  }, []);

  // Handle save
  const handleSave = useCallback(async () => {
    setErr("");
    const startedAt = Date.now();
    let ok = false;
    setSaveState("loading");

    try {
      // Save all settings
      await saveUiSettingsRef.current();
      await saveExternalKeysRef.current();
      await saveSecuritySettingsRef.current();
      await saveArrRequestModeDraftRef.current();

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
      setErr(e?.message || "Erreur sauvegarde settings");
    } finally {
      const elapsed = Date.now() - startedAt;
      if (elapsed < 1000) {
        await sleep(1000 - elapsed);
      }
      setSaveState(ok ? "success" : "error");
      setTimeout(() => setSaveState("idle"), 1000);
    }
  }, [hostPort, initialHostPort]);

  // Handle refresh
  const handleRefresh = useCallback(() => {
    load();
    if (showApplications) {
      loadArrAppsRef.current();
      loadArrSyncSettingsRef.current();
      loadArrSyncStatusRef.current();
    }
    if (showBackup) {
      loadBackupsRef.current();
      loadBackupStateRef.current();
    }
    if (showMaintenance) {
      loadStatsRef.current();
    }
  }, [load, showApplications, showBackup, showMaintenance]);

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
  }, [showMaintenance]);

  // Load applications data when section is active
  const applicationsLoadedRef = useRef(false);
  useEffect(() => {
    if (!showApplications) {
      applicationsLoadedRef.current = false;
      return;
    }
    if (applicationsLoadedRef.current) return;
    applicationsLoadedRef.current = true;
    loadArrAppsRef.current();
    loadArrSyncSettingsRef.current();
    loadArrSyncStatusRef.current();
  }, [showApplications]);

  // Set initial host port
  useEffect(() => {
    if (typeof window === "undefined") return;
    const currentPort =
      window.location.port || (window.location.protocol === "https:" ? "443" : "80");
    if (!hostPort) setHostPort(currentPort);
    if (!initialHostPort) setInitialHostPort(currentPort);
  }, [hostPort, initialHostPort]);

  // Apply theme on load
  useEffect(() => {
    if (loading) return;
    applyTheme(uiSettings.ui?.theme);
  }, [uiSettings.ui?.theme, loading]);

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

  // Wrap application handlers with error handling
  const triggerArrSync = useCallback(async () => {
    try {
      await triggerArrSyncRef.current();
    } catch (e) {
      setErr(e?.message || "Erreur");
    }
  }, []);

  const saveArrSyncSettings = useCallback(async (settings) => {
    try {
      await saveArrSyncSettingsRef.current(settings);
    } catch (e) {
      setErr(e?.message || "Erreur");
    }
  }, []);

  const saveArrRequestModeDraft = useCallback(async () => {
    try {
      await saveArrRequestModeDraftRef.current();
    } catch (e) {
      setErr(e?.message || "Erreur");
    }
  }, []);

  const confirmArrDelete = useCallback(async () => {
    try {
      await confirmArrDeleteRef.current();
    } catch (e) {
      setErr(e?.message || "Erreur");
    }
  }, []);

  const toggleArrEnabled = useCallback(async (app) => {
    try {
      await toggleArrEnabledRef.current(app);
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
    openArrModalAdd: applications.openArrModalAdd,
    canAddArrApp: applications.availableAddTypes.length > 0,
    openExternalModalAdd: providers.openExternalModalAdd,
    canAddExternalProvider: providers.canAddExternalProvider,
    triggerArrSync,
    arrSyncing: applications.arrSyncing,
    hasEnabledArrApps: applications.hasEnabledArrApps,
    general: {
      hostPort,
      urlBase,
    },
    ui: {
      ui: uiSettings.ui,
      setUi: uiSettings.setUi,
      pulseKeys: uiSettings.pulseKeys,
      handleThemeChange: uiSettings.handleThemeChange,
    },
    providers: {
      ...providers,
      confirmExternalDelete,
      confirmExternalToggle,
    },
    applications: {
      arrApps: applications.arrApps,
      arrAppsLoading: applications.arrAppsLoading,
      arrTestingId: applications.arrTestingId,
      arrTestStatusById: applications.arrTestStatusById,
      arrSyncingId: applications.arrSyncingId,
      arrSyncStatusById: applications.arrSyncStatusById,
      arrSyncing: applications.arrSyncing,
      arrSyncSettings: applications.arrSyncSettings,
      arrSyncStatus: applications.arrSyncStatus,
      arrSyncSaving: applications.arrSyncSaving,
      arrRequestModeDraft: applications.arrRequestModeDraft,
      arrPulseKeys: applications.arrPulseKeys,
      isRequestModeDirty: applications.isRequestModeDirty,
      hasEnabledArrApps: applications.hasEnabledArrApps,
      availableAddTypes: applications.availableAddTypes,
      syncArrApp: applications.syncArrApp,
      testArrApp: applications.testArrApp,
      openArrModalEdit: applications.openArrModalEdit,
      openArrDelete: applications.openArrDelete,
      toggleArrEnabled,
      setArrSyncSettings: applications.setArrSyncSettings,
      setArrRequestModeDraft: applications.setArrRequestModeDraft,
      saveArrSyncSettings,
      saveArrRequestModeDraft,
      arrModalOpen: applications.arrModalOpen,
      arrModalMode: applications.arrModalMode,
      arrModalApp: applications.arrModalApp,
      arrModalType: applications.arrModalType,
      arrModalName: applications.arrModalName,
      arrModalBaseUrl: applications.arrModalBaseUrl,
      arrModalApiKey: applications.arrModalApiKey,
      arrModalTesting: applications.arrModalTesting,
      arrModalTested: applications.arrModalTested,
      arrModalError: applications.arrModalError,
      arrModalSaving: applications.arrModalSaving,
      arrModalAdvanced: applications.arrModalAdvanced,
      arrModalConfig: applications.arrModalConfig,
      arrModalConfigLoading: applications.arrModalConfigLoading,
      arrModalAdvancedInitial: applications.arrModalAdvancedInitial,
      arrModalRootFolder: applications.arrModalRootFolder,
      arrModalQualityProfile: applications.arrModalQualityProfile,
      arrModalSeriesType: applications.arrModalSeriesType,
      arrModalSeasonFolder: applications.arrModalSeasonFolder,
      arrModalMonitorMode: applications.arrModalMonitorMode,
      arrModalSearchMissing: applications.arrModalSearchMissing,
      arrModalSearchCutoff: applications.arrModalSearchCutoff,
      arrModalMinAvail: applications.arrModalMinAvail,
      arrModalSearchForMovie: applications.arrModalSearchForMovie,
      setArrModalType: applications.setArrModalType,
      setArrModalName: applications.setArrModalName,
      setArrModalBaseUrl: applications.setArrModalBaseUrl,
      setArrModalApiKey: applications.setArrModalApiKey,
      setArrModalTested: applications.setArrModalTested,
      setArrModalError: applications.setArrModalError,
      setArrModalRootFolder: applications.setArrModalRootFolder,
      setArrModalQualityProfile: applications.setArrModalQualityProfile,
      setArrModalSeriesType: applications.setArrModalSeriesType,
      setArrModalSeasonFolder: applications.setArrModalSeasonFolder,
      setArrModalMonitorMode: applications.setArrModalMonitorMode,
      setArrModalSearchMissing: applications.setArrModalSearchMissing,
      setArrModalSearchCutoff: applications.setArrModalSearchCutoff,
      setArrModalMinAvail: applications.setArrModalMinAvail,
      setArrModalSearchForMovie: applications.setArrModalSearchForMovie,
      setArrModalAdvanced: applications.setArrModalAdvanced,
      setArrModalAdvancedInitial: applications.setArrModalAdvancedInitial,
      closeArrModal: applications.closeArrModal,
      testArrModal: applications.testArrModal,
      saveArrModal: applications.saveArrModal,
      arrDeleteOpen: applications.arrDeleteOpen,
      arrDeleteApp: applications.arrDeleteApp,
      arrDeleteLoading: applications.arrDeleteLoading,
      confirmArrDelete,
      closeArrDelete: applications.closeArrDelete,
    },
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
    users: {
      security: security.security,
      setSecurity: security.setSecurity,
      securityErrors: security.securityErrors,
      securityMessage: security.securityMessage,
      passwordMessage: security.passwordMessage,
      showExistingCredentialsHint: security.showExistingCredentialsHint,
      allowDowngradeToOpen: security.allowDowngradeToOpen,
      setAllowDowngradeToOpen: security.setAllowDowngradeToOpen,
      requiresDowngradeConfirmation: security.requiresDowngradeConfirmation,
      credentialsRequiredForMode: security.credentialsRequiredForMode,
      usernameRequired: security.usernameRequired,
      passwordRequired: security.passwordRequired,
      confirmRequired: security.confirmRequired,
      usernameFieldState: security.usernameFieldState,
      passwordFieldState: security.passwordFieldState,
      confirmFieldState: security.confirmFieldState,
    },
  };
}
