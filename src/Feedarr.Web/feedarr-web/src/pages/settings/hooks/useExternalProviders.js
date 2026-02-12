import { useCallback, useState } from "react";
import { apiGet, apiPost, apiPut } from "../../../api/client.js";
import { triggerPosterPolling } from "../../../hooks/usePosterPollingService.js";
import { useRetroFetchProgress } from "../../../hooks/useRetroFetchProgress.js";

const notifyOnboardingRefresh = () => {
  if (typeof window !== "undefined") {
    window.dispatchEvent(new Event("onboarding:refresh"));
  }
};

export default function useExternalProviders() {
  const [externalFlags, setExternalFlags] = useState({
    hasTmdbApiKey: false,
    hasTvmazeApiKey: false,
    hasFanartApiKey: false,
    hasIgdbClientId: false,
    hasIgdbClientSecret: false,
    tmdbEnabled: true,
    tvmazeEnabled: true,
    fanartEnabled: true,
    igdbEnabled: true,
  });

  const [externalInput, setExternalInput] = useState({
    tmdbApiKey: "",
    tvmazeApiKey: "",
    fanartApiKey: "",
    igdbClientId: "",
    igdbClientSecret: "",
  });

  const [testingExternal, setTestingExternal] = useState(null);
  const [testStatusByExternal, setTestStatusByExternal] = useState({});

  // Modal state
  const [externalModalOpen, setExternalModalOpen] = useState(false);
  const [externalModalRow, setExternalModalRow] = useState(null);
  const [externalModalValue, setExternalModalValue] = useState("");
  const [externalModalValue2, setExternalModalValue2] = useState("");
  const [externalModalTesting, setExternalModalTesting] = useState(false);
  const [externalModalTested, setExternalModalTested] = useState(false);
  const [externalModalError, setExternalModalError] = useState("");

  // Disable confirmation
  const [externalDisableOpen, setExternalDisableOpen] = useState(false);
  const [externalDisableRow, setExternalDisableRow] = useState(null);

  // Toggle confirmation
  const [externalToggleOpen, setExternalToggleOpen] = useState(false);
  const [externalToggleRow, setExternalToggleRow] = useState(null);

  // Retro fetch
  const [retroLoading, setRetroLoading] = useState(false);
  const [retroStopLoading, setRetroStopLoading] = useState(false);
  const [retroMsg, setRetroMsg] = useState("");
  const { retroTask, startRetroFetch, stopRetroFetch } = useRetroFetchProgress();

  // Stats
  const [posterCount, setPosterCount] = useState(0);
  const [missingPosterCount, setMissingPosterCount] = useState(0);
  const [releasesCount, setReleasesCount] = useState(0);
  const [providerStats, setProviderStats] = useState({
    tmdb: { calls: 0, failures: 0 },
    tvmaze: { calls: 0, failures: 0 },
    fanart: { calls: 0, failures: 0 },
    igdb: { calls: 0, failures: 0 },
  });

  const loadExternalFlags = useCallback(async () => {
    try {
      const ex = await apiGet("/api/settings/external");
      if (ex) setExternalFlags(ex);
    } catch {
      // Ignore
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
      if (prov) {
        setProviderStats({
          tmdb: { calls: Number(prov.tmdb?.calls ?? 0), failures: Number(prov.tmdb?.failures ?? 0) },
          tvmaze: { calls: Number(prov.tvmaze?.calls ?? 0), failures: Number(prov.tvmaze?.failures ?? 0) },
          fanart: { calls: Number(prov.fanart?.calls ?? 0), failures: Number(prov.fanart?.failures ?? 0) },
          igdb: { calls: Number(prov.igdb?.calls ?? 0), failures: Number(prov.igdb?.failures ?? 0) },
        });
      }
    } catch {
      setPosterCount(0);
      setMissingPosterCount(0);
    }
  }, []);

  const saveExternalKeys = useCallback(async () => {
    const hasPayload =
      !!externalInput.tmdbApiKey ||
      !!externalInput.tvmazeApiKey ||
      !!externalInput.fanartApiKey ||
      !!externalInput.igdbClientId ||
      !!externalInput.igdbClientSecret;

    if (!hasPayload) return false;

    const ex = await apiPut("/api/settings/external", {
      tmdbApiKey: externalInput.tmdbApiKey || null,
      tvmazeApiKey: externalInput.tvmazeApiKey || null,
      fanartApiKey: externalInput.fanartApiKey || null,
      igdbClientId: externalInput.igdbClientId || null,
      igdbClientSecret: externalInput.igdbClientSecret || null,
    });
    if (ex) setExternalFlags(ex);
    notifyOnboardingRefresh();
    setExternalInput({
      tmdbApiKey: "",
      tvmazeApiKey: "",
      fanartApiKey: "",
      igdbClientId: "",
      igdbClientSecret: "",
    });
    return true;
  }, [externalInput]);

  // Modal functions
  function openExternalModal(row) {
    setExternalModalRow(row);
    setExternalModalValue(externalInput[row.inputKey] || "");
    if (row.inputKey2) {
      setExternalModalValue2(externalInput[row.inputKey2] || "");
    } else {
      setExternalModalValue2("");
    }
    setExternalModalTesting(false);
    setExternalModalTested(false);
    setExternalModalError("");
    setExternalModalOpen(true);
  }

  function closeExternalModal() {
    setExternalModalOpen(false);
    setExternalModalRow(null);
    setExternalModalValue("");
    setExternalModalValue2("");
    setExternalModalTesting(false);
    setExternalModalTested(false);
    setExternalModalError("");
  }

  async function testExternalModal() {
    if (!externalModalRow || !externalModalValue.trim()) return;
    if (externalModalRow.inputKey2 && !externalModalValue2.trim()) return;

    setExternalModalError("");
    setExternalModalTesting(true);
    try {
      const payload = { [externalModalRow.inputKey]: externalModalValue.trim() };
      if (externalModalRow.inputKey2) {
        payload[externalModalRow.inputKey2] = externalModalValue2.trim();
      }
      await apiPut("/api/settings/external", payload);

      const res = await apiPost("/api/settings/external/test", { kind: externalModalRow.kind });
      if (res?.ok) {
        setExternalModalTested(true);
        setExternalModalError("");
      } else {
        setExternalModalError(res?.error || "Test échoué");
        setExternalModalTested(false);
      }
    } catch (e) {
      setExternalModalError(e?.message || "Erreur test provider");
      setExternalModalTested(false);
    } finally {
      setExternalModalTesting(false);
    }
  }

  async function saveExternalModal() {
    if (!externalModalRow) return;
    try {
      const payload = { [externalModalRow.inputKey]: externalModalValue || null };
      if (externalModalRow.inputKey2) {
        payload[externalModalRow.inputKey2] = externalModalValue2 || null;
      }
      if (externalModalRow.toggleKey) {
        payload[externalModalRow.toggleKey] = true;
      }
      const ex = await apiPut("/api/settings/external", payload);
      if (ex) setExternalFlags(ex);
      notifyOnboardingRefresh();
      setExternalInput((prev) => {
        const next = { ...prev, [externalModalRow.inputKey]: "" };
        if (externalModalRow.inputKey2) {
          next[externalModalRow.inputKey2] = "";
        }
        return next;
      });
      closeExternalModal();
    } catch (e) {
      setExternalModalError(e?.message || "Erreur sauvegarde externe");
    }
  }

  // Disable functions
  function openExternalDisable(row) {
    setExternalDisableRow(row);
    setExternalDisableOpen(true);
  }

  function closeExternalDisable() {
    setExternalDisableOpen(false);
    setExternalDisableRow(null);
  }

  async function confirmDisableExternal() {
    if (!externalDisableRow) return;
    const payload = {};
    externalDisableRow.disableKeys.forEach((k) => {
      payload[k] = "";
    });
    payload[externalDisableRow.toggleKey] = false;
    try {
      await apiPut("/api/settings/external", payload);
      const ex = await apiGet("/api/settings/external");
      if (ex) setExternalFlags(ex);
      notifyOnboardingRefresh();
      setExternalInput((prev) => {
        const next = { ...prev };
        externalDisableRow.disableKeys.forEach((k) => {
          next[k] = "";
        });
        return next;
      });
      closeExternalDisable();
    } catch (e) {
      throw new Error(e?.message || "Erreur désactivation externe");
    }
  }

  // Toggle functions
  function openExternalToggle(row) {
    setExternalToggleRow(row);
    setExternalToggleOpen(true);
  }

  function closeExternalToggle() {
    setExternalToggleOpen(false);
    setExternalToggleRow(null);
  }

  async function confirmToggleExternal() {
    if (!externalToggleRow) return;
    if (!externalToggleRow.enabled) {
      const keyPresent = (() => {
        if (externalToggleRow.inputKey === "tmdbApiKey") return externalFlags.hasTmdbApiKey;
        if (externalToggleRow.inputKey === "tvmazeApiKey") return true;
        if (externalToggleRow.inputKey === "fanartApiKey") return externalFlags.hasFanartApiKey;
        if (externalToggleRow.inputKey === "igdbClientId" || externalToggleRow.inputKey === "igdbClientSecret")
          return externalFlags.hasIgdbClientId && externalFlags.hasIgdbClientSecret;
        return false;
      })();
      if (!keyPresent) {
        openExternalModal(externalToggleRow);
        closeExternalToggle();
        return;
      }
    }
    const payload = { [externalToggleRow.toggleKey]: !externalToggleRow.enabled };
    try {
      await apiPut("/api/settings/external", payload);
      const ex = await apiGet("/api/settings/external");
      if (ex) setExternalFlags(ex);
      notifyOnboardingRefresh();
      closeExternalToggle();
    } catch (e) {
      throw new Error(e?.message || "Erreur toggle externe");
    }
  }

  async function testExternal(rowKey, kind) {
    const startedAt = Date.now();
    setTestingExternal(rowKey);
    setTestStatusByExternal((prev) => ({ ...prev, [rowKey]: "pending" }));
    try {
      const res = await apiPost("/api/settings/external/test", { kind });
      const elapsed = Date.now() - startedAt;
      const wait = Math.max(2000 - elapsed, 0);
      setTimeout(() => {
        setTestStatusByExternal((prev) => ({ ...prev, [rowKey]: res?.ok ? "ok" : "error" }));
        setTimeout(() => {
          setTestStatusByExternal((prev) => {
            const next = { ...prev };
            delete next[rowKey];
            return next;
          });
        }, 1600);
        setTestingExternal(null);
      }, wait);
    } catch {
      const elapsed = Date.now() - startedAt;
      const wait = Math.max(2000 - elapsed, 0);
      setTimeout(() => {
        setTestStatusByExternal((prev) => ({ ...prev, [rowKey]: "error" }));
        setTimeout(() => {
          setTestStatusByExternal((prev) => {
            const next = { ...prev };
            delete next[rowKey];
            return next;
          });
        }, 1600);
        setTestingExternal(null);
      }, wait);
    }
  }

  // Retro fetch
  const retroActive = !!retroTask?.active;
  const retroStartMissing = Number(retroTask?.startMissing ?? 0);
  const retroTargetMissing = Number(retroTask?.targetMissing ?? 0);
  const retroCurrentMissing = Number(retroTask?.currentMissing ?? missingPosterCount ?? 0);
  const retroTotal = Number(
    retroTask?.total ?? (Array.isArray(retroTask?.ids) ? retroTask.ids.length : null) ?? Math.max(0, retroStartMissing - retroTargetMissing)
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
    // Flags & stats
    externalFlags,
    setExternalFlags,
    externalInput,
    providerStats,
    posterCount,
    missingPosterCount,
    releasesCount,
    setReleasesCount,
    // Loading
    loadExternalFlags,
    loadProviderStats,
    saveExternalKeys,
    // Testing
    testingExternal,
    testStatusByExternal,
    testExternal,
    // Modal
    externalModalOpen,
    externalModalRow,
    externalModalValue,
    setExternalModalValue,
    externalModalValue2,
    setExternalModalValue2,
    externalModalTesting,
    externalModalTested,
    externalModalError,
    setExternalModalTested,
    setExternalModalError,
    openExternalModal,
    closeExternalModal,
    testExternalModal,
    saveExternalModal,
    // Disable
    externalDisableOpen,
    externalDisableRow,
    openExternalDisable,
    closeExternalDisable,
    confirmDisableExternal,
    // Toggle
    externalToggleOpen,
    externalToggleRow,
    openExternalToggle,
    closeExternalToggle,
    confirmToggleExternal,
    // Retro fetch
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
