import { useCallback, useEffect, useRef, useState } from "react";
import { apiDelete, apiGet, apiPost, apiPut } from "../../../api/client.js";
import { sleep } from "../settingsUtils.js";
import { normalizeRequestMode } from "../../../utils/appTypes.js";

const ALL_APP_TYPES = ["sonarr", "radarr", "overseerr", "jellyseerr"];

export default function useArrApplications() {
  const [arrApps, setArrApps] = useState([]);
  const [arrAppsLoading, setArrAppsLoading] = useState(false);

  // Modal state
  const [arrModalOpen, setArrModalOpen] = useState(false);
  const [arrModalMode, setArrModalMode] = useState("add");
  const [arrModalApp, setArrModalApp] = useState(null);
  const [arrModalType, setArrModalType] = useState("sonarr");
  const [arrModalName, setArrModalName] = useState("");
  const [arrModalBaseUrl, setArrModalBaseUrl] = useState("");
  const [arrModalApiKey, setArrModalApiKey] = useState("");
  const [arrModalTesting, setArrModalTesting] = useState(false);
  const [arrModalTested, setArrModalTested] = useState(false);
  const [arrModalError, setArrModalError] = useState("");
  const [arrModalSaving, setArrModalSaving] = useState(false);
  const [arrModalAdvanced, setArrModalAdvanced] = useState(false);
  const [arrModalConfig, setArrModalConfig] = useState(null);
  const [arrModalConfigLoading, setArrModalConfigLoading] = useState(false);
  const [arrModalAdvancedInitial, setArrModalAdvancedInitial] = useState(null);
  const [arrModalRootFolder, setArrModalRootFolder] = useState("");
  const [arrModalQualityProfile, setArrModalQualityProfile] = useState("");
  const [arrModalTags, setArrModalTags] = useState([]);

  // Sonarr-specific
  const [arrModalSeriesType, setArrModalSeriesType] = useState("standard");
  const [arrModalSeasonFolder, setArrModalSeasonFolder] = useState(true);
  const [arrModalMonitorMode, setArrModalMonitorMode] = useState("all");
  const [arrModalSearchMissing, setArrModalSearchMissing] = useState(true);
  const [arrModalSearchCutoff, setArrModalSearchCutoff] = useState(false);

  // Radarr-specific
  const [arrModalMinAvail, setArrModalMinAvail] = useState("released");
  const [arrModalSearchForMovie, setArrModalSearchForMovie] = useState(true);

  // Delete confirmation
  const [arrDeleteOpen, setArrDeleteOpen] = useState(false);
  const [arrDeleteApp, setArrDeleteApp] = useState(null);
  const [arrDeleteLoading, setArrDeleteLoading] = useState(false);

  // Test status per app
  const [arrTestingId, setArrTestingId] = useState(null);
  const [arrTestStatusById, setArrTestStatusById] = useState({});
  const [arrSyncingId, setArrSyncingId] = useState(null);
  const [arrSyncStatusById, setArrSyncStatusById] = useState({});

  // Sync settings
  const [arrSyncSettings, setArrSyncSettings] = useState({
    arrSyncIntervalMinutes: 60,
    arrAutoSyncEnabled: true,
    requestIntegrationMode: "arr",
  });
  const [arrSyncStatus, setArrSyncStatus] = useState([]);
  const [arrSyncStatusLoading, setArrSyncStatusLoading] = useState(false);
  const [arrSyncSaving, setArrSyncSaving] = useState(false);
  const [arrSyncing, setArrSyncing] = useState(false);
  const [arrRequestModeDraft, setArrRequestModeDraft] = useState("arr");
  const [arrPulseKeys, setArrPulseKeys] = useState(() => new Set());
  const arrPulseTimerRef = useRef(null);

  const hasEnabledArrApps = arrApps.some((app) => app.isEnabled && app.hasApiKey);
  const existingTypes = new Set(arrApps.map((app) => String(app.type || "").toLowerCase()));
  const availableAddTypes = ALL_APP_TYPES.filter((type) => !existingTypes.has(type));
  const isRequestModeDirty = arrRequestModeDraft !== normalizeRequestMode(arrSyncSettings?.requestIntegrationMode);

  useEffect(() => {
    return () => {
      if (arrPulseTimerRef.current) clearTimeout(arrPulseTimerRef.current);
    };
  }, []);

  // Load functions
  const loadArrApps = useCallback(async () => {
    setArrAppsLoading(true);
    try {
      const apps = await apiGet("/api/apps");
      setArrApps(Array.isArray(apps) ? apps : []);
    } catch {
      setArrApps([]);
    } finally {
      setArrAppsLoading(false);
    }
  }, []);

  const loadArrSyncSettings = useCallback(async () => {
    try {
      const general = await apiGet("/api/settings/general");
      const next = {
        arrSyncIntervalMinutes: Number(general?.arrSyncIntervalMinutes ?? 60),
        arrAutoSyncEnabled: general?.arrAutoSyncEnabled !== false,
        requestIntegrationMode: normalizeRequestMode(general?.requestIntegrationMode),
      };
      setArrSyncSettings(next);
      setArrRequestModeDraft(next.requestIntegrationMode);
    } catch {
      const fallback = {
        arrSyncIntervalMinutes: 60,
        arrAutoSyncEnabled: true,
        requestIntegrationMode: "arr",
      };
      setArrSyncSettings(fallback);
      setArrRequestModeDraft(fallback.requestIntegrationMode);
    }
  }, []);

  const loadArrSyncStatus = useCallback(async () => {
    setArrSyncStatusLoading(true);
    try {
      const status = await apiGet("/api/arr/sync/status");
      setArrSyncStatus(Array.isArray(status) ? status : []);
    } catch {
      setArrSyncStatus([]);
    } finally {
      setArrSyncStatusLoading(false);
    }
  }, []);

  const loadArrConfig = useCallback(async (appId) => {
    setArrModalConfigLoading(true);
    try {
      const config = await apiGet(`/api/apps/${appId}/config`);
      setArrModalConfig(config);
    } catch {
      setArrModalConfig(null);
    } finally {
      setArrModalConfigLoading(false);
    }
  }, []);

  // Save functions
  const saveArrSyncSettings = useCallback(async (settings) => {
    setArrSyncSaving(true);
    try {
      const wasRequestModeDirty = arrRequestModeDraft !== normalizeRequestMode(arrSyncSettings?.requestIntegrationMode);
      const current = await apiGet("/api/settings/general");
      const payload = {
        ...current,
        arrSyncIntervalMinutes: settings.arrSyncIntervalMinutes,
        arrAutoSyncEnabled: settings.arrAutoSyncEnabled,
        requestIntegrationMode: normalizeRequestMode(settings.requestIntegrationMode),
      };
      const saved = await apiPut("/api/settings/general", payload);
      const next = {
        arrSyncIntervalMinutes: Number(saved?.arrSyncIntervalMinutes ?? 60),
        arrAutoSyncEnabled: saved?.arrAutoSyncEnabled !== false,
        requestIntegrationMode: normalizeRequestMode(saved?.requestIntegrationMode),
      };
      setArrSyncSettings(next);
      if (!wasRequestModeDirty) {
        setArrRequestModeDraft(next.requestIntegrationMode);
      }
    } catch (e) {
      throw new Error(e?.message || "Erreur sauvegarde paramètres sync");
    } finally {
      setArrSyncSaving(false);
    }
  }, [arrRequestModeDraft, arrSyncSettings]);

  const saveArrRequestModeDraft = useCallback(async () => {
    if (!isRequestModeDirty) return;
    const updated = {
      ...arrSyncSettings,
      requestIntegrationMode: arrRequestModeDraft,
    };
    await saveArrSyncSettings(updated);

    if (arrPulseTimerRef.current) clearTimeout(arrPulseTimerRef.current);
    setArrPulseKeys(new Set(["arr.requestIntegrationMode"]));
    arrPulseTimerRef.current = setTimeout(() => {
      setArrPulseKeys(new Set());
    }, 1200);
  }, [arrRequestModeDraft, arrSyncSettings, isRequestModeDirty, saveArrSyncSettings]);

  const triggerArrSync = useCallback(async () => {
    if (!hasEnabledArrApps) return;
    setArrSyncing(true);
    try {
      await apiPost("/api/arr/sync");
      await loadArrSyncStatus();
    } catch (e) {
      throw new Error(e?.message || "Erreur synchronisation");
    } finally {
      setArrSyncing(false);
    }
  }, [loadArrSyncStatus, hasEnabledArrApps]);

  // Modal functions
  function resetArrModal() {
    setArrModalMode("add");
    setArrModalApp(null);
    setArrModalType("sonarr");
    setArrModalName("");
    setArrModalBaseUrl("");
    setArrModalApiKey("");
    setArrModalTesting(false);
    setArrModalTested(false);
    setArrModalError("");
    setArrModalSaving(false);
    setArrModalAdvanced(false);
    setArrModalAdvancedInitial(null);
    setArrModalConfig(null);
    setArrModalConfigLoading(false);
    setArrModalRootFolder("");
    setArrModalQualityProfile("");
    setArrModalTags([]);
    setArrModalSeriesType("standard");
    setArrModalSeasonFolder(true);
    setArrModalMonitorMode("all");
    setArrModalSearchMissing(true);
    setArrModalSearchCutoff(false);
    setArrModalMinAvail("released");
    setArrModalSearchForMovie(true);
  }

  function openArrModalAdd() {
    resetArrModal();
    setArrModalMode("add");
    setArrModalType(availableAddTypes[0] || "");
    setArrModalOpen(true);
  }

  function openArrModalEdit(app) {
    resetArrModal();
    setArrModalMode("edit");
    setArrModalApp(app);
    setArrModalType(app.type);
    setArrModalName(app.name || "");
    setArrModalBaseUrl(app.baseUrl || "");
    setArrModalApiKey("");
    setArrModalRootFolder(app.rootFolderPath || "");
    setArrModalQualityProfile(app.qualityProfileId ? String(app.qualityProfileId) : "");
    setArrModalTags(Array.isArray(app.tags) ? app.tags : []);
    setArrModalSeriesType(app.seriesType || "standard");
    setArrModalSeasonFolder(app.seasonFolder !== false);
    setArrModalMonitorMode(app.monitorMode || "all");
    setArrModalSearchMissing(app.searchMissing !== false);
    setArrModalSearchCutoff(!!app.searchCutoff);
    setArrModalMinAvail(app.minimumAvailability || "released");
    setArrModalSearchForMovie(app.searchForMovie !== false);
    setArrModalOpen(true);
    if (app.hasApiKey) {
      loadArrConfig(app.id);
    }
  }

  function closeArrModal() {
    setArrModalOpen(false);
    resetArrModal();
  }

  async function testArrModal() {
    if (!arrModalBaseUrl.trim() || !arrModalApiKey.trim()) return;
    setArrModalTesting(true);
    setArrModalError("");
    setArrModalTested(false);
    try {
      const res = await apiPost(`/api/apps/test?type=${arrModalType}`, {
        baseUrl: arrModalBaseUrl.trim(),
        apiKey: arrModalApiKey.trim(),
      });
      if (res?.ok) {
        setArrModalTested(true);
        setArrModalError("");
      } else {
        setArrModalError(res?.error || "Test échoué");
      }
    } catch (e) {
      setArrModalError(e?.message || "Erreur test connexion");
    } finally {
      setArrModalTesting(false);
    }
  }

  async function saveArrModal() {
    setArrModalSaving(true);
    setArrModalError("");
    try {
      if (arrModalMode === "add" && !arrModalType) {
        throw new Error("Aucun type disponible à ajouter");
      }

      const payload = {
        type: arrModalType,
        name: arrModalName.trim() || null,
        baseUrl: arrModalBaseUrl.trim(),
        rootFolderPath: arrModalRootFolder || null,
        qualityProfileId: arrModalQualityProfile ? Number(arrModalQualityProfile) : null,
        tags: arrModalTags.length > 0 ? arrModalTags : null,
      };

      if (arrModalApiKey.trim()) {
        payload.apiKey = arrModalApiKey.trim();
      }

      if (arrModalType === "sonarr") {
        payload.seriesType = arrModalSeriesType;
        payload.seasonFolder = arrModalSeasonFolder;
        payload.monitorMode = arrModalMonitorMode;
        payload.searchMissing = arrModalSearchMissing;
        payload.searchCutoff = arrModalSearchCutoff;
      } else if (arrModalType === "radarr") {
        payload.minimumAvailability = arrModalMinAvail;
        payload.searchForMovie = arrModalSearchForMovie;
      }

      if (arrModalMode === "add") {
        await apiPost("/api/apps", payload);
      } else if (arrModalApp) {
        await apiPut(`/api/apps/${arrModalApp.id}`, payload);
      }

      closeArrModal();
      await loadArrApps();
    } catch (e) {
      setArrModalError(e?.message || "Erreur sauvegarde");
    } finally {
      setArrModalSaving(false);
    }
  }

  // Delete functions
  function openArrDelete(app) {
    setArrDeleteApp(app);
    setArrDeleteOpen(true);
  }

  function closeArrDelete() {
    setArrDeleteOpen(false);
    setArrDeleteApp(null);
  }

  async function confirmArrDelete() {
    if (!arrDeleteApp) return;
    setArrDeleteLoading(true);
    try {
      await apiDelete(`/api/apps/${arrDeleteApp.id}`);
      closeArrDelete();
      await loadArrApps();
    } catch (e) {
      throw new Error(e?.message || "Erreur suppression");
    } finally {
      setArrDeleteLoading(false);
    }
  }

  // Test & Sync functions
  async function testArrApp(appId) {
    setArrTestingId(appId);
    setArrTestStatusById((prev) => ({ ...prev, [appId]: "pending" }));
    try {
      const res = await apiPost(`/api/apps/${appId}/test`);
      await sleep(1500);
      setArrTestStatusById((prev) => ({ ...prev, [appId]: res?.ok ? "ok" : "error" }));
      setTimeout(() => {
        setArrTestStatusById((prev) => {
          const next = { ...prev };
          delete next[appId];
          return next;
        });
      }, 1600);
    } catch {
      setArrTestStatusById((prev) => ({ ...prev, [appId]: "error" }));
      setTimeout(() => {
        setArrTestStatusById((prev) => {
          const next = { ...prev };
          delete next[appId];
          return next;
        });
      }, 1600);
    } finally {
      setArrTestingId(null);
    }
  }

  async function syncArrApp(app) {
    if (!app?.id || !app.isEnabled) return;
    if (arrTestingId === app.id || arrSyncing || arrSyncStatusById[app.id] === "pending") return;
    const startedAt = Date.now();
    setArrSyncingId(app.id);
    setArrSyncStatusById((prev) => ({ ...prev, [app.id]: "pending" }));
    try {
      const res = await apiPost(`/api/arr/sync/${app.id}`);
      await loadArrSyncStatus();
      const elapsed = Date.now() - startedAt;
      const wait = Math.max(2000 - elapsed, 0);
      setTimeout(() => {
        setArrSyncStatusById((prev) => ({ ...prev, [app.id]: res?.ok ? "ok" : "error" }));
        setTimeout(() => {
          setArrSyncStatusById((prev) => {
            const next = { ...prev };
            delete next[app.id];
            return next;
          });
        }, 1600);
        setArrSyncingId(null);
      }, wait);
    } catch {
      const elapsed = Date.now() - startedAt;
      const wait = Math.max(2000 - elapsed, 0);
      setTimeout(() => {
        setArrSyncStatusById((prev) => ({ ...prev, [app.id]: "error" }));
        setTimeout(() => {
          setArrSyncStatusById((prev) => {
            const next = { ...prev };
            delete next[app.id];
            return next;
          });
        }, 1600);
        setArrSyncingId(null);
      }, wait);
    }
  }

  async function toggleArrEnabled(app) {
    try {
      await apiPut(`/api/apps/${app.id}/enabled`, { enabled: !app.isEnabled });
      await loadArrApps();
    } catch (e) {
      throw new Error(e?.message || "Erreur activation/désactivation");
    }
  }

  async function setArrDefault(appId) {
    try {
      await apiPut(`/api/apps/${appId}/default`);
      await loadArrApps();
    } catch (e) {
      throw new Error(e?.message || "Erreur définition par défaut");
    }
  }

  return {
    // Apps list
    arrApps,
    arrAppsLoading,
    hasEnabledArrApps,
    availableAddTypes,
    loadArrApps,
    // Sync settings
    arrSyncSettings,
    arrSyncStatus,
    arrSyncStatusLoading,
    arrSyncSaving,
    arrSyncing,
    arrRequestModeDraft,
    arrPulseKeys,
    isRequestModeDirty,
    setArrSyncSettings,
    setArrRequestModeDraft,
    loadArrSyncSettings,
    loadArrSyncStatus,
    saveArrSyncSettings,
    saveArrRequestModeDraft,
    triggerArrSync,
    // Test status
    arrTestingId,
    arrTestStatusById,
    arrSyncingId,
    arrSyncStatusById,
    testArrApp,
    syncArrApp,
    toggleArrEnabled,
    setArrDefault,
    // Modal
    arrModalOpen,
    arrModalMode,
    arrModalApp,
    arrModalType,
    arrModalName,
    arrModalBaseUrl,
    arrModalApiKey,
    arrModalTesting,
    arrModalTested,
    arrModalError,
    arrModalSaving,
    arrModalAdvanced,
    arrModalConfig,
    arrModalConfigLoading,
    arrModalAdvancedInitial,
    arrModalRootFolder,
    arrModalQualityProfile,
    arrModalTags,
    arrModalSeriesType,
    arrModalSeasonFolder,
    arrModalMonitorMode,
    arrModalSearchMissing,
    arrModalSearchCutoff,
    arrModalMinAvail,
    arrModalSearchForMovie,
    setArrModalType,
    setArrModalName,
    setArrModalBaseUrl,
    setArrModalApiKey,
    setArrModalTested,
    setArrModalError,
    setArrModalAdvanced,
    setArrModalAdvancedInitial,
    setArrModalRootFolder,
    setArrModalQualityProfile,
    setArrModalTags,
    setArrModalSeriesType,
    setArrModalSeasonFolder,
    setArrModalMonitorMode,
    setArrModalSearchMissing,
    setArrModalSearchCutoff,
    setArrModalMinAvail,
    setArrModalSearchForMovie,
    openArrModalAdd,
    openArrModalEdit,
    closeArrModal,
    testArrModal,
    saveArrModal,
    // Delete
    arrDeleteOpen,
    arrDeleteApp,
    arrDeleteLoading,
    openArrDelete,
    closeArrDelete,
    confirmArrDelete,
  };
}
