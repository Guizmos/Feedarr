import { useCallback, useMemo, useState } from "react";
import { apiDelete, apiGet, apiPost, apiPut } from "../../../api/client.js";
import { triggerPosterPolling } from "../../../hooks/usePosterPollingService.js";
import { useRetroFetchProgress } from "../../../hooks/useRetroFetchProgress.js";

const notifyOnboardingRefresh = () => {
  if (typeof window !== "undefined") {
    window.dispatchEvent(new Event("onboarding:refresh"));
  }
};

const EMPTY_STATS = {};

function toHasFlagName(fieldKey) {
  if (!fieldKey) return "";
  const trimmed = String(fieldKey).trim();
  if (!trimmed) return "";
  return `has${trimmed.charAt(0).toUpperCase()}${trimmed.slice(1)}`;
}

function hasRequiredAuth(instance, definition) {
  if (!definition) return false;
  const requiredFields = (definition.fieldsSchema || []).filter((field) => field.required);
  if (requiredFields.length === 0) return true;
  const authFlags = instance?.authFlags || {};
  return requiredFields.every((field) => !!authFlags[toHasFlagName(field.key)]);
}

export default function useExternalProviderInstances() {
  const [definitions, setDefinitions] = useState([]);
  const [instances, setInstances] = useState([]);
  const [externalLoading, setExternalLoading] = useState(false);
  const [externalError, setExternalError] = useState("");

  const [testingExternal, setTestingExternal] = useState(null);
  const [testStatusByExternal, setTestStatusByExternal] = useState({});

  const [externalModalOpen, setExternalModalOpen] = useState(false);
  const [externalModalMode, setExternalModalMode] = useState("add");
  const [externalModalStep, setExternalModalStep] = useState(1);
  const [externalModalInstance, setExternalModalInstance] = useState(null);
  const [externalModalProviderKey, setExternalModalProviderKey] = useState("");
  const [externalModalDisplayName, setExternalModalDisplayName] = useState("");
  const [externalModalEnabled, setExternalModalEnabled] = useState(true);
  const [externalModalBaseUrl, setExternalModalBaseUrl] = useState("");
  const [externalModalAuth, setExternalModalAuth] = useState({});
  const [externalModalSaving, setExternalModalSaving] = useState(false);
  const [externalModalError, setExternalModalError] = useState("");

  const [externalDeleteOpen, setExternalDeleteOpen] = useState(false);
  const [externalDeleteInstance, setExternalDeleteInstance] = useState(null);
  const [externalDeleteLoading, setExternalDeleteLoading] = useState(false);

  const [externalToggleOpen, setExternalToggleOpen] = useState(false);
  const [externalToggleInstance, setExternalToggleInstance] = useState(null);

  const [posterCount, setPosterCount] = useState(0);
  const [missingPosterCount, setMissingPosterCount] = useState(0);
  const [releasesCount, setReleasesCount] = useState(0);
  const [providerStats, setProviderStats] = useState(EMPTY_STATS);

  const [retroLoading, setRetroLoading] = useState(false);
  const [retroStopLoading, setRetroStopLoading] = useState(false);
  const [retroMsg, setRetroMsg] = useState("");
  const { retroTask, startRetroFetch, stopRetroFetch } = useRetroFetchProgress();

  const definitionByKey = useMemo(() => {
    const map = new Map();
    (definitions || []).forEach((definition) => {
      map.set(String(definition.providerKey || "").toLowerCase(), definition);
    });
    return map;
  }, [definitions]);

  const existingProviderKeys = useMemo(() => {
    const keys = new Set();
    (instances || []).forEach((instance) => {
      const key = String(instance?.providerKey || "").trim().toLowerCase();
      if (key) keys.add(key);
    });
    return keys;
  }, [instances]);

  const availableExternalDefinitions = useMemo(
    () =>
      (definitions || []).filter((definition) => {
        const key = String(definition?.providerKey || "").trim().toLowerCase();
        if (!key) return false;
        return !existingProviderKeys.has(key);
      }),
    [definitions, existingProviderKeys]
  );

  const externalModalDefinition = useMemo(() => {
    if (!externalModalProviderKey) return null;
    return definitionByKey.get(String(externalModalProviderKey).toLowerCase()) || null;
  }, [definitionByKey, externalModalProviderKey]);

  const externalValidationByProvider = useMemo(() => {
    const validation = {};
    (definitions || []).forEach((definition) => {
      const key = String(definition.providerKey || "");
      const matchingInstances = (instances || []).filter(
        (instance) => String(instance.providerKey || "").toLowerCase() === key.toLowerCase()
      );
      const ok = matchingInstances.some((instance) => instance.enabled && hasRequiredAuth(instance, definition));
      validation[key] = ok ? "ok" : null;
    });
    return validation;
  }, [definitions, instances]);

  const allProvidersAdded = useMemo(() => {
    if (!definitions?.length) return false;
    return definitions.every((definition) => {
      const matchingInstances = (instances || []).filter(
        (instance) => String(instance.providerKey || "").toLowerCase() === String(definition.providerKey || "").toLowerCase()
      );
      return matchingInstances.some((instance) => hasRequiredAuth(instance, definition));
    });
  }, [definitions, instances]);

  const canAddExternalProvider = availableExternalDefinitions.length > 0;

  const resetExternalModal = useCallback(() => {
    setExternalModalMode("add");
    setExternalModalStep(1);
    setExternalModalInstance(null);
    setExternalModalProviderKey("");
    setExternalModalDisplayName("");
    setExternalModalEnabled(true);
    setExternalModalBaseUrl("");
    setExternalModalAuth({});
    setExternalModalSaving(false);
    setExternalModalError("");
  }, []);

  const loadExternalProviders = useCallback(async () => {
    setExternalLoading(true);
    setExternalError("");
    try {
      const data = await apiGet("/api/providers/external");
      setDefinitions(Array.isArray(data?.definitions) ? data.definitions : []);
      setInstances(Array.isArray(data?.instances) ? data.instances : []);
    } catch (e) {
      setExternalError(e?.message || "Erreur chargement providers metadata");
      setDefinitions([]);
      setInstances([]);
    } finally {
      setExternalLoading(false);
    }
  }, []);

  const loadProviderStats = useCallback(async () => {
    try {
      const [pc, mc, prov] = await Promise.all([
        apiGet("/api/posters/count"),
        apiGet("/api/posters/missing-count"),
        apiGet("/api/system/providers"),
      ]);
      setPosterCount(Number(pc?.count ?? 0));
      setMissingPosterCount(Number(mc?.count ?? 0));
      if (prov && typeof prov === "object") {
        const nextStats = {};
        Object.entries(prov).forEach(([providerKey, value]) => {
          if (!value || typeof value !== "object") return;
          nextStats[String(providerKey).toLowerCase()] = {
            calls: Number(value.calls ?? 0),
            failures: Number(value.failures ?? 0),
            avgMs: Number(value.avgMs ?? 0),
          };
        });
        setProviderStats(nextStats);
      } else {
        setProviderStats(EMPTY_STATS);
      }
    } catch {
      setPosterCount(0);
      setMissingPosterCount(0);
      setProviderStats(EMPTY_STATS);
    }
  }, []);

  const openExternalModalAdd = useCallback((providerKey = "") => {
    resetExternalModal();
    setExternalModalMode("add");
    const normalized = String(providerKey || "").trim();
    if (normalized) {
      if (existingProviderKeys.has(normalized.toLowerCase())) {
        setExternalModalError("Provider deja ajoute.");
        setExternalModalStep(1);
        setExternalModalOpen(true);
        return;
      }
      const definition = definitionByKey.get(normalized.toLowerCase());
      setExternalModalProviderKey(normalized);
      setExternalModalBaseUrl(definition?.defaultBaseUrl || "");
      setExternalModalStep(2);
    } else {
      setExternalModalStep(1);
    }
    setExternalModalOpen(true);
  }, [definitionByKey, existingProviderKeys, resetExternalModal]);

  const selectExternalModalProvider = useCallback((providerKey) => {
    const normalized = String(providerKey || "").trim();
    if (externalModalMode === "add" && existingProviderKeys.has(normalized.toLowerCase())) {
      setExternalModalProviderKey("");
      setExternalModalBaseUrl("");
      setExternalModalAuth({});
      setExternalModalError("Provider deja ajoute.");
      return;
    }
    const definition = definitionByKey.get(normalized.toLowerCase());
    setExternalModalProviderKey(normalized);
    setExternalModalBaseUrl(definition?.defaultBaseUrl || "");
    setExternalModalAuth({});
    setExternalModalError("");
  }, [definitionByKey, existingProviderKeys, externalModalMode]);

  const openExternalModalEdit = useCallback((instance) => {
    if (!instance) return;
    resetExternalModal();
    const providerKey = String(instance.providerKey || "");
    const definition = definitionByKey.get(providerKey.toLowerCase());

    setExternalModalMode("edit");
    setExternalModalStep(2);
    setExternalModalInstance(instance);
    setExternalModalProviderKey(providerKey);
    setExternalModalDisplayName(instance.displayName || "");
    setExternalModalEnabled(instance.enabled !== false);
    setExternalModalBaseUrl(instance.baseUrl || definition?.defaultBaseUrl || "");
    setExternalModalAuth({});
    setExternalModalOpen(true);
  }, [definitionByKey, resetExternalModal]);

  const closeExternalModal = useCallback(() => {
    if (externalModalSaving) return;
    setExternalModalOpen(false);
    resetExternalModal();
  }, [externalModalSaving, resetExternalModal]);

  const setExternalModalAuthField = useCallback((fieldKey, value) => {
    setExternalModalAuth((prev) => ({ ...prev, [fieldKey]: value }));
    setExternalModalError("");
  }, []);

  const goExternalModalStep = useCallback((step) => {
    setExternalModalError("");
    setExternalModalStep(step);
  }, []);

  const computeMissingRequiredFields = useCallback(() => {
    const definition = externalModalDefinition;
    if (!definition) return [];

    const requiredFields = (definition.fieldsSchema || []).filter((field) => field.required);
    const missing = [];
    requiredFields.forEach((field) => {
      const entered = String(externalModalAuth[field.key] || "").trim();
      if (entered) return;

      if (externalModalMode === "edit") {
        const hasFlag = !!externalModalInstance?.authFlags?.[toHasFlagName(field.key)];
        if (hasFlag) return;
      }

      missing.push(field.key);
    });

    return missing;
  }, [externalModalAuth, externalModalDefinition, externalModalInstance, externalModalMode]);

  const canSaveExternalModal = useMemo(() => {
    if (!externalModalDefinition) return false;
    if (externalModalMode === "add" && externalModalStep !== 2) return false;
    if (externalModalSaving) return false;
    return computeMissingRequiredFields().length === 0;
  }, [computeMissingRequiredFields, externalModalDefinition, externalModalMode, externalModalSaving, externalModalStep]);

  const saveExternalModal = useCallback(async () => {
    const definition = externalModalDefinition;
    if (!definition) {
      setExternalModalError("Provider invalide.");
      return;
    }

    if (externalModalMode === "add" && externalModalStep === 1) {
      if (!externalModalProviderKey) {
        setExternalModalError("Selectionnez un provider.");
        return;
      }
      goExternalModalStep(2);
      return;
    }

    const missing = computeMissingRequiredFields();
    if (missing.length > 0) {
      setExternalModalError(`Champ(s) requis: ${missing.join(", ")}`);
      return;
    }

    const authPayload = {};
    (definition.fieldsSchema || []).forEach((field) => {
      const value = String(externalModalAuth[field.key] || "").trim();
      if (!value) return;
      authPayload[field.key] = value;
    });

    const displayName = String(externalModalDisplayName || "").trim();
    const baseUrl = String(externalModalBaseUrl || "").trim();

    setExternalModalSaving(true);
    setExternalModalError("");
    try {
      if (externalModalMode === "add") {
        const payload = {
          providerKey: externalModalProviderKey,
          displayName: displayName || null,
          enabled: externalModalEnabled,
          baseUrl: baseUrl || null,
          auth: authPayload,
          options: {},
        };
        await apiPost("/api/providers/external", payload);
      } else if (externalModalInstance?.instanceId) {
        const payload = {
          displayName: displayName || null,
          enabled: externalModalEnabled,
          baseUrl: baseUrl || null,
          options: {},
        };
        if (Object.keys(authPayload).length > 0) {
          payload.auth = authPayload;
        }
        await apiPut(`/api/providers/external/${externalModalInstance.instanceId}`, payload);
      }

      notifyOnboardingRefresh();
      closeExternalModal();
      await loadExternalProviders();
    } catch (e) {
      setExternalModalError(e?.message || "Erreur sauvegarde provider metadata");
    } finally {
      setExternalModalSaving(false);
    }
  }, [
    closeExternalModal,
    computeMissingRequiredFields,
    externalModalAuth,
    externalModalBaseUrl,
    externalModalDefinition,
    externalModalDisplayName,
    externalModalEnabled,
    externalModalInstance,
    externalModalMode,
    externalModalProviderKey,
    externalModalStep,
    goExternalModalStep,
    loadExternalProviders,
  ]);

  const openExternalDelete = useCallback((instance) => {
    setExternalDeleteInstance(instance || null);
    setExternalDeleteOpen(true);
  }, []);

  const closeExternalDelete = useCallback(() => {
    if (externalDeleteLoading) return;
    setExternalDeleteOpen(false);
    setExternalDeleteInstance(null);
  }, [externalDeleteLoading]);

  const confirmExternalDelete = useCallback(async () => {
    if (!externalDeleteInstance?.instanceId) return;
    setExternalDeleteLoading(true);
    try {
      await apiDelete(`/api/providers/external/${externalDeleteInstance.instanceId}`);
      notifyOnboardingRefresh();
      closeExternalDelete();
      await loadExternalProviders();
    } catch (e) {
      throw new Error(e?.message || "Erreur suppression provider metadata");
    } finally {
      setExternalDeleteLoading(false);
    }
  }, [closeExternalDelete, externalDeleteInstance, loadExternalProviders]);

  const openExternalToggle = useCallback((instance) => {
    setExternalToggleInstance(instance || null);
    setExternalToggleOpen(true);
  }, []);

  const closeExternalToggle = useCallback(() => {
    setExternalToggleOpen(false);
    setExternalToggleInstance(null);
  }, []);

  const confirmExternalToggle = useCallback(async () => {
    if (!externalToggleInstance?.instanceId) return;
    try {
      await apiPut(`/api/providers/external/${externalToggleInstance.instanceId}`, {
        enabled: !externalToggleInstance.enabled,
      });
      notifyOnboardingRefresh();
      closeExternalToggle();
      await loadExternalProviders();
    } catch (e) {
      throw new Error(e?.message || "Erreur activation/desactivation provider metadata");
    }
  }, [closeExternalToggle, externalToggleInstance, loadExternalProviders]);

  const testExternal = useCallback(async (instanceId) => {
    if (!instanceId) return;
    const startedAt = Date.now();
    setTestingExternal(instanceId);
    setTestStatusByExternal((prev) => ({ ...prev, [instanceId]: "pending" }));

    try {
      const res = await apiPost(`/api/providers/external/${instanceId}/test`);
      const elapsed = Date.now() - startedAt;
      const wait = Math.max(2000 - elapsed, 0);
      setTimeout(() => {
        setTestStatusByExternal((prev) => ({ ...prev, [instanceId]: res?.ok ? "ok" : "error" }));
        setTimeout(() => {
          setTestStatusByExternal((prev) => {
            const next = { ...prev };
            delete next[instanceId];
            return next;
          });
        }, 1600);
        setTestingExternal(null);
      }, wait);
    } catch {
      const elapsed = Date.now() - startedAt;
      const wait = Math.max(2000 - elapsed, 0);
      setTimeout(() => {
        setTestStatusByExternal((prev) => ({ ...prev, [instanceId]: "error" }));
        setTimeout(() => {
          setTestStatusByExternal((prev) => {
            const next = { ...prev };
            delete next[instanceId];
            return next;
          });
        }, 1600);
        setTestingExternal(null);
      }, wait);
    }
  }, []);

  const loadExternalFlags = useCallback(async () => {
    await loadExternalProviders();
  }, [loadExternalProviders]);

  const saveExternalKeys = useCallback(async () => false, []);

  const isInstanceConfigured = useCallback((instance) => {
    const definition = definitionByKey.get(String(instance?.providerKey || "").toLowerCase());
    return hasRequiredAuth(instance, definition);
  }, [definitionByKey]);

  const retroActive = !!retroTask?.active;
  const retroStartMissing = Number(retroTask?.startMissing ?? 0);
  const retroTargetMissing = Number(retroTask?.targetMissing ?? 0);
  const retroCurrentMissing = Number(retroTask?.currentMissing ?? missingPosterCount ?? 0);
  const retroTotal = Number(
    retroTask?.total
      ?? (Array.isArray(retroTask?.ids) ? retroTask.ids.length : null)
      ?? Math.max(0, retroStartMissing - retroTargetMissing)
  );
  const retroDone = Number(retroTask?.done ?? Math.max(0, retroStartMissing - retroCurrentMissing));
  const retroPercent = retroTotal > 0 ? Math.min(100, Math.max(0, Math.round((retroDone / retroTotal) * 100))) : 0;

  async function handleRetroFetch() {
    if (retroTask?.active) return;
    setRetroLoading(true);
    setRetroMsg("");
    const { error } = await startRetroFetch();
    if (error) {
      setRetroMsg(error);
      setRetroLoading(false);
    } else {
      triggerPosterPolling("retro-fetch");
    }
  }

  async function handleRetroFetchStop() {
    if (!retroTask?.active || retroStopLoading) return;
    setRetroStopLoading(true);
    setRetroMsg("");
    const { error } = await stopRetroFetch();
    if (error) {
      setRetroMsg(error);
    } else {
      setRetroMsg("Retro fetch arrete");
    }
    setRetroStopLoading(false);
    setRetroLoading(false);
  }

  return {
    definitions,
    availableExternalDefinitions,
    instances,
    externalLoading,
    externalError,
    providerStats,
    loadExternalProviders,
    loadExternalFlags,
    saveExternalKeys,
    loadProviderStats,
    canAddExternalProvider,
    externalValidationByProvider,
    allProvidersAdded,
    isInstanceConfigured,
    testingExternal,
    testStatusByExternal,
    testExternal,
    openExternalModalAdd,
    openExternalModalEdit,
    closeExternalModal,
    goExternalModalStep,
    saveExternalModal,
    externalModalOpen,
    externalModalMode,
    externalModalStep,
    externalModalInstance,
    externalModalProviderKey,
    setExternalModalProviderKey,
    selectExternalModalProvider,
    externalModalDefinition,
    externalModalDisplayName,
    setExternalModalDisplayName,
    externalModalEnabled,
    setExternalModalEnabled,
    externalModalBaseUrl,
    setExternalModalBaseUrl,
    externalModalAuth,
    setExternalModalAuthField,
    externalModalSaving,
    externalModalError,
    canSaveExternalModal,
    openExternalDelete,
    closeExternalDelete,
    confirmExternalDelete,
    externalDeleteOpen,
    externalDeleteInstance,
    externalDeleteLoading,
    openExternalToggle,
    closeExternalToggle,
    confirmExternalToggle,
    externalToggleOpen,
    externalToggleInstance,
    posterCount,
    missingPosterCount,
    releasesCount,
    setReleasesCount,
    retroActive,
    retroLoading,
    retroStopLoading,
    retroMsg,
    retroPercent,
    retroDone,
    retroTotal,
    handleRetroFetch,
    handleRetroFetchStop,
  };
}
