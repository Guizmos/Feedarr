import React, { useCallback, useEffect, useMemo, useReducer, useRef, useState } from "react";
import { apiGet, apiPost } from "@api/client.js";
import Loader from "@ui/Loader.jsx";
import ReleaseModal from "@ui/ReleaseModal.jsx";
import { getSourceColor } from "@utils/sourceColors.js";
import { openDownloadPath } from "@utils/downloadPath.js";
import { executeAsync } from "@utils/executeAsync.js";
import { useSubbarSetter } from "@layout/useSubbar.js";
import useArrApps from "@hooks/useArrApps.js";
import { triggerPosterPolling } from "@hooks/usePosterPollingService.js";
import { normalizeRequestMode } from "@utils/appTypes.js";
import { LayoutGrid } from "lucide-react";
import { getActiveUiLanguage } from "../../app/locale.js";

// Local imports
import { fmtBytes, fmtDateFromTs } from "./utils/formatters.js";
import {
  isSeriesItem,
  isGameCategoryKey,
  isGameMediaType,
  hasDetailsPayload,
  mergePosterState,
  buildPosterUrl,
  normalizeTitleKey,
  sortManualResultsBySize,
} from "./utils/helpers.js";
import { ARR_STATUS_TTL_MS, ARR_STATUS_BATCH_SIZE } from "./utils/constants.js";
import useLibraryFilters from "./hooks/useLibraryFilters.js";
import useLibrarySelection from "./hooks/useLibrarySelection.js";
import useLibraryDerivedData from "./hooks/useLibraryDerivedData.js";
import LibraryGrid from "./components/LibraryGrid.jsx";
import LibraryList from "./components/LibraryList.jsx";
import LibraryBanner from "./components/LibraryBanner.jsx";
import LibraryPoster from "./components/LibraryPoster.jsx";
import LibrarySubbar from "./components/LibrarySubbar.jsx";
import RenameModal from "./components/RenameModal.jsx";
import ManualPosterModal from "./components/ManualPosterModal.jsx";

const initialLibraryUiState = {
  loading: true,
  err: "",
  selectedItem: null,
  posterAutoLoading: false,
  renameOpen: false,
  renameValue: "",
  renameTarget: null,
  renameOriginal: "",
  manualOpen: false,
  manualQuery: "",
  manualMediaType: "",
  manualResults: [],
  manualLoading: false,
  manualErr: "",
  manualTarget: null,
};

function libraryUiReducer(state, action) {
  switch (action.type) {
    case "set_loading":
      return { ...state, loading: Boolean(action.payload) };
    case "set_err":
      return { ...state, err: action.payload || "" };
    case "set_selected_item":
      return {
        ...state,
        selectedItem:
          typeof action.payload === "function"
            ? action.payload(state.selectedItem)
            : action.payload,
      };
    case "set_poster_auto_loading":
      return { ...state, posterAutoLoading: Boolean(action.payload) };
    case "set_rename_open":
      return { ...state, renameOpen: Boolean(action.payload) };
    case "set_rename_value":
      return { ...state, renameValue: action.payload || "" };
    case "set_rename_target":
      return { ...state, renameTarget: action.payload || null };
    case "set_rename_original":
      return { ...state, renameOriginal: action.payload || "" };
    case "set_manual_open":
      return { ...state, manualOpen: Boolean(action.payload) };
    case "set_manual_query":
      return { ...state, manualQuery: action.payload || "" };
    case "set_manual_media_type":
      return { ...state, manualMediaType: action.payload || "" };
    case "set_manual_results":
      return { ...state, manualResults: Array.isArray(action.payload) ? action.payload : [] };
    case "set_manual_loading":
      return { ...state, manualLoading: Boolean(action.payload) };
    case "set_manual_err":
      return { ...state, manualErr: action.payload || "" };
    case "set_manual_target":
      return { ...state, manualTarget: action.payload || null };
    default:
      return state;
  }
}

export default function Library() {
  const [uiState, dispatchUi] = useReducer(libraryUiReducer, initialLibraryUiState);
  const loading = uiState.loading;
  const err = uiState.err;
  const selectedItem = uiState.selectedItem;
  const posterAutoLoading = uiState.posterAutoLoading;
  const renameOpen = uiState.renameOpen;
  const renameValue = uiState.renameValue;
  const renameTarget = uiState.renameTarget;
  const renameOriginal = uiState.renameOriginal;
  const manualOpen = uiState.manualOpen;
  const manualQuery = uiState.manualQuery;
  const manualMediaType = uiState.manualMediaType;
  const manualResults = uiState.manualResults;
  const manualLoading = uiState.manualLoading;
  const manualErr = uiState.manualErr;
  const manualTarget = uiState.manualTarget;

  const setLoading = useCallback((value) => {
    dispatchUi({ type: "set_loading", payload: value });
  }, []);
  const setErr = useCallback((value) => {
    dispatchUi({ type: "set_err", payload: value });
  }, []);
  const setSelectedItem = useCallback((value) => {
    dispatchUi({ type: "set_selected_item", payload: value });
  }, []);
  const setPosterAutoLoading = useCallback((value) => {
    dispatchUi({ type: "set_poster_auto_loading", payload: value });
  }, []);
  const setRenameOpen = useCallback((value) => {
    dispatchUi({ type: "set_rename_open", payload: value });
  }, []);
  const setRenameValue = useCallback((value) => {
    dispatchUi({ type: "set_rename_value", payload: value });
  }, []);
  const setRenameTarget = useCallback((value) => {
    dispatchUi({ type: "set_rename_target", payload: value });
  }, []);
  const setRenameOriginal = useCallback((value) => {
    dispatchUi({ type: "set_rename_original", payload: value });
  }, []);
  const setManualOpen = useCallback((value) => {
    dispatchUi({ type: "set_manual_open", payload: value });
  }, []);
  const setManualQuery = useCallback((value) => {
    dispatchUi({ type: "set_manual_query", payload: value });
  }, []);
  const setManualMediaType = useCallback((value) => {
    dispatchUi({ type: "set_manual_media_type", payload: value });
  }, []);
  const setManualResults = useCallback((value) => {
    dispatchUi({ type: "set_manual_results", payload: value });
  }, []);
  const setManualLoading = useCallback((value) => {
    dispatchUi({ type: "set_manual_loading", payload: value });
  }, []);
  const setManualErr = useCallback((value) => {
    dispatchUi({ type: "set_manual_err", payload: value });
  }, []);
  const setManualTarget = useCallback((value) => {
    dispatchUi({ type: "set_manual_target", payload: value });
  }, []);

  const setContent = useSubbarSetter();

  // Card size slider (20-100, default 50 = current size)
  const SLIDER_MIN = 20;
  const GRID_MIN = 95, GRID_MAX = 285;   // grid: 95px → 190px → 285px
  const POSTER_MIN = 90, POSTER_MAX = 270; // poster: 90px → 180px → 270px

  const [gridCardSlider, setGridCardSlider] = useState(() => {
    const v = Number(localStorage.getItem("feedarr.library.cardSize.grid"));
    return Number.isFinite(v) && v >= SLIDER_MIN && v <= 100 ? v : 50;
  });
  const [posterCardSlider, setPosterCardSlider] = useState(() => {
    const v = Number(localStorage.getItem("feedarr.library.cardSize.poster"));
    return Number.isFinite(v) && v >= SLIDER_MIN && v <= 100 ? v : 50;
  });

  const gridCardSize = GRID_MIN + (gridCardSlider / 100) * (GRID_MAX - GRID_MIN);
  const posterCardSize = POSTER_MIN + (posterCardSlider / 100) * (POSTER_MAX - POSTER_MIN);

  const handleGridSlider = useCallback((e) => {
    const v = Number(e.target.value);
    setGridCardSlider(v);
    localStorage.setItem("feedarr.library.cardSize.grid", String(v));
  }, []);

  const handlePosterSlider = useCallback((e) => {
    const v = Number(e.target.value);
    setPosterCardSlider(v);
    localStorage.setItem("feedarr.library.cardSize.poster", String(v));
  }, []);

  // Sources
  const [sources, setSources] = useState([]);
  const enabledSources = useMemo(
    () => (sources || []).filter((s) => Number(s.enabled ?? 1) === 1),
    [sources]
  );

  // Filters hook
  const filters = useLibraryFilters(sources, enabledSources);

  // Selection hook
  const selection = useLibrarySelection();

  // Arr apps integration
  const {
    apps,
    hasSonarr,
    hasRadarr,
    hasOverseerr,
    hasJellyseerr,
    hasSeer,
    integrationMode,
  } = useArrApps({ pollMs: 120000 });
  const requestMode = normalizeRequestMode(integrationMode);
  const installedApps = useMemo(
    () =>
      (apps || [])
        .filter((app) => app && app.id != null && app.isEnabled !== false && app.hasApiKey !== false)
        .sort((a, b) => String(a.name || a.title || "").localeCompare(String(b.name || b.title || ""), getActiveUiLanguage(), { sensitivity: "base" })),
    [apps]
  );
  const [arrStatusMap, setArrStatusMap] = useState({});
  const arrStatusFetchedRef = useRef(new Map());
  const arrAbortRef = useRef(null);
  const arrRequestIdRef = useRef(0);
  const loadAbortRef = useRef(null);
  const loadRequestIdRef = useRef(0);

  // Items
  const [items, setItems] = useState([]);

  // Refs
  const posterRefreshRef = useRef({ attempts: 0, timer: null });
  const posterStateRefreshRef = useRef({ inFlight: false, lastFingerprint: "" });
  const gameDetailsFetchRef = useRef(new Set());
  const visibleItemIdsRef = useRef([]);

  // Source maps
  const sourceNameById = useMemo(() => {
    const map = new Map();
    (sources || []).forEach((s) => {
      const id = Number(s.id ?? s.sourceId);
      if (Number.isFinite(id)) map.set(id, s.name ?? s.title ?? `Source ${id}`);
    });
    return map;
  }, [sources]);

  const sourceColorById = useMemo(() => {
    const map = new Map();
    (sources || []).forEach((s) => {
      const id = Number(s.id ?? s.sourceId);
      if (Number.isFinite(id)) map.set(id, getSourceColor(id, s.color));
    });
    return map;
  }, [sources]);

  // Cleanup refs on unmount
  useEffect(() => {
    const arrStatusFetched = arrStatusFetchedRef.current;
    const gameDetailsFetch = gameDetailsFetchRef.current;
    const posterRefresh = posterRefreshRef.current;

    return () => {
      arrStatusFetched.clear();
      gameDetailsFetch.clear();
      if (loadAbortRef.current) loadAbortRef.current.abort();
      if (posterRefresh.timer) clearTimeout(posterRefresh.timer);
    };
  }, []);

  // Load sources
  useEffect(() => {
    (async () => {
      const src = await executeAsync(
        () => apiGet("/api/sources"),
        { context: "Failed to load sources for library page" }
      );
      if (Array.isArray(src)) setSources(src);
    })();
  }, []);

  // Build arr status map
  const buildArrStatusMap = useCallback(
    (sourceItems, prevMap) => {
      if (!hasSonarr && !hasRadarr && !hasOverseerr && !hasJellyseerr && !hasSeer) return {};
      const next = {};
      (sourceItems || []).forEach((it) => {
        const prev = prevMap?.[it.id];
        const inSonarr = hasSonarr && (prev?.inSonarr || it?.isInSonarr);
        const inRadarr = hasRadarr && (prev?.inRadarr || it?.isInRadarr);
        const inOverseerr = hasOverseerr && !!prev?.inOverseerr;
        const inJellyseerr = hasJellyseerr && !!prev?.inJellyseerr;
        const inSeer = hasSeer && !!prev?.inSeer;
        if (!inSonarr && !inRadarr && !inOverseerr && !inJellyseerr && !inSeer) return;
        next[it.id] = {
          inSonarr,
          inRadarr,
          inOverseerr,
          inJellyseerr,
          inSeer,
          sonarrUrl: inSonarr ? (prev?.sonarrUrl || it?.sonarrUrl || null) : null,
          radarrUrl: inRadarr ? (prev?.radarrUrl || it?.radarrUrl || null) : null,
          overseerrUrl: inOverseerr ? (prev?.overseerrUrl || null) : null,
          jellyseerrUrl: inJellyseerr ? (prev?.jellyseerrUrl || null) : null,
          seerUrl: inSeer ? (prev?.seerUrl || null) : null,
        };
      });
      return next;
    },
    [hasSonarr, hasRadarr, hasOverseerr, hasJellyseerr, hasSeer]
  );

  // Load items
  const load = useCallback(async () => {
    if (loadAbortRef.current) loadAbortRef.current.abort();
    const abortController = new AbortController();
    loadAbortRef.current = abortController;
    const requestId = ++loadRequestIdRef.current;
    const isLatestRequest = () =>
      loadRequestIdRef.current === requestId && !abortController.signal.aborted;

    if (posterRefreshRef.current.timer) {
      clearTimeout(posterRefreshRef.current.timer);
      posterRefreshRef.current.timer = null;
    }
    setErr("");

    const baseLimit = filters.limit === "all" ? 500 : Math.max(1, Number(filters.limit) || 100);
    const fetchLimit = filters.limit === "all" ? 1500 : Math.min(baseLimit * 3, 1500);
    const sid = Number(filters.sourceId);
    setLoading(true);

    try {
      if (!Number.isFinite(sid) || sid <= 0) {
        if (enabledSources.length === 0) {
          if (isLatestRequest()) setItems([]);
          return;
        }
        const params = new URLSearchParams();
        params.set("limit", String(fetchLimit));
        if (filters.seen) params.set("seen", filters.seen);

        const all = await Promise.allSettled(
          enabledSources.map(async (s) => {
            const id = s.id ?? s.sourceId;
            if (!id) return [];
            const data = await apiGet(`/api/feed/${id}?${params.toString()}`, {
              signal: abortController.signal,
            });
            return Array.isArray(data)
              ? data.map((it) => ({ ...it, sourceId: it.sourceId ?? id }))
              : [];
          })
        );
        if (!isLatestRequest()) return;

        const merged = all
          .filter((r) => r.status === "fulfilled")
          .flatMap((r) => r.value)
          .map((it) => ({
            ...it,
            size: fmtBytes(it.sizeBytes),
            date: fmtDateFromTs(it.publishedAt),
          }));

        merged.sort((a, b) => Number(b.publishedAt || 0) - Number(a.publishedAt || 0));
        if (isLatestRequest()) setItems(merged);
      } else {
        const params = new URLSearchParams();
        params.set("limit", String(fetchLimit));
        if (filters.q.trim()) params.set("q", filters.q.trim());
        if (filters.seen) params.set("seen", filters.seen);

        const data = await apiGet(`/api/feed/${sid}?${params.toString()}`, {
          signal: abortController.signal,
        });
        if (!isLatestRequest()) return;
        const mapped = (Array.isArray(data) ? data : []).map((it) => ({
          ...it,
          sourceId: it.sourceId ?? sid,
          size: fmtBytes(it.sizeBytes),
          date: fmtDateFromTs(it.publishedAt),
        }));
        if (isLatestRequest()) setItems(mapped);
      }
    } catch (e) {
      if (e?.name === "AbortError") return;
      if (!isLatestRequest()) return;
      setErr(e?.message || "Erreur chargement feed");
      setItems([]);
    } finally {
      if (loadRequestIdRef.current === requestId) setLoading(false);
    }
  }, [filters.sourceId, enabledSources, filters.limit, filters.q, filters.seen, setErr, setLoading]);

  useEffect(() => {
    posterRefreshRef.current.attempts = 0;
    load();
  }, [load]);

  // Update arr status map when items change
  useEffect(() => {
    if (!items || items.length === 0) {
      setArrStatusMap({});
      return;
    }
    setArrStatusMap((prev) => buildArrStatusMap(items, prev));
  }, [items, hasSonarr, hasRadarr, hasOverseerr, hasJellyseerr, hasSeer, buildArrStatusMap]);

  // Fetch arr status
  const fetchArrStatus = useCallback(async (itemsToCheck) => {
    const canCheckArr = hasSonarr || hasRadarr;
    const canCheckOverseerr = hasOverseerr;
    const canCheckJellyseerr = hasJellyseerr;
    const canCheckSeer = hasSeer;
    if (!canCheckArr && !canCheckOverseerr && !canCheckJellyseerr && !canCheckSeer) return;
    if (!itemsToCheck || itemsToCheck.length === 0) return;

    const now = Date.now();
    const newItems = itemsToCheck.filter((it) => {
      const hasIds = it.tmdbId || it.tvdbId;
      const hasTitle = it.titleClean?.trim() || it.title?.trim();
      if (!hasIds && !hasTitle) return false;
      const checkedAt = Number(it.arrCheckedAtTs || 0);
      const isExpired = !Number.isFinite(checkedAt) || checkedAt <= 0 || (now - checkedAt * 1000) >= ARR_STATUS_TTL_MS;
      if (!isExpired) return false;
      const lastChecked = arrStatusFetchedRef.current.get(it.id);
      if (lastChecked && now - lastChecked < ARR_STATUS_TTL_MS) return false;
      return true;
    });

    if (newItems.length === 0) return;

    // Cancel any in-flight arr status request
    if (arrAbortRef.current) arrAbortRef.current.abort();
    const abortController = new AbortController();
    arrAbortRef.current = abortController;

    // Track request to ignore stale responses
    const requestId = ++arrRequestIdRef.current;

    const batches = [];
    for (let i = 0; i < newItems.length; i += ARR_STATUS_BATCH_SIZE) {
      batches.push(newItems.slice(i, i + ARR_STATUS_BATCH_SIZE));
    }

    await executeAsync(async () => {
      const newStatuses = {};
      for (const batch of batches) {
        if (abortController.signal.aborted) return;
        if (arrRequestIdRef.current !== requestId) return;

        const apiItems = batch.map((it) => ({
          releaseId: it.id,
          tvdbId: it.tvdbId || null,
          tmdbId: it.tmdbId || null,
          mediaType: isSeriesItem(it) ? "series" : "movie",
          title: it.titleClean?.trim() || it.title?.trim() || null,
        }));

        const res = await apiPost("/api/arr/status", { items: apiItems }, { signal: abortController.signal });
        if (arrRequestIdRef.current !== requestId) return;
        if (res?.results && Array.isArray(res.results)) {
          const checkedAt = Date.now();
          batch.forEach((it) => arrStatusFetchedRef.current.set(it.id, checkedAt));

          // Create ID-based lookup map for safe matching (avoids index-based assumptions)
          const batchById = new Map(batch.map((it) => [it.id, it]));

          res.results.forEach((result) => {
            const releaseId = Number(result?.releaseId || 0);
            if (!Number.isFinite(releaseId) || releaseId <= 0) return;

            const originalItem = batchById.get(releaseId);
            if (!originalItem) return;
            const inSonarr = hasSonarr && !!result.inSonarr;
            const inRadarr = hasRadarr && !!result.inRadarr;
            const inOverseerr = hasOverseerr && !!result.inOverseerr;
            const inJellyseerr = hasJellyseerr && !!result.inJellyseerr;
            const inSeer = hasSeer && !!result.inSeer;
            if (result.exists || inSonarr || inRadarr || inOverseerr || inJellyseerr || inSeer) {
              newStatuses[originalItem.id] = {
                inSonarr,
                inRadarr,
                inOverseerr,
                inJellyseerr,
                inSeer,
                sonarrUrl: inSonarr ? (result.sonarrUrl || null) : null,
                radarrUrl: inRadarr ? (result.radarrUrl || null) : null,
                overseerrUrl: inOverseerr ? (result.overseerrUrl || null) : null,
                jellyseerrUrl: inJellyseerr ? (result.jellyseerrUrl || null) : null,
                seerUrl: inSeer ? (result.seerUrl || null) : null,
              };
            }
          });
        }
      }

      if (arrRequestIdRef.current !== requestId) return;
      if (Object.keys(newStatuses).length > 0) {
        setArrStatusMap((prev) => ({ ...prev, ...newStatuses }));
      }
    }, {
      context: "Failed to refresh Arr status batch in library",
      ignoreAbort: true,
    });
  }, [hasSonarr, hasRadarr, hasOverseerr, hasJellyseerr, hasSeer]);

  useEffect(() => {
    if (loading) return;
    const canCheckArr = hasSonarr || hasRadarr;
    const canCheckOverseerr = hasOverseerr;
    const canCheckJellyseerr = hasJellyseerr;
    const canCheckSeer = hasSeer;
    if (!canCheckArr && !canCheckOverseerr && !canCheckJellyseerr && !canCheckSeer) return;
    fetchArrStatus(items || []);
    return () => {
      if (arrAbortRef.current) arrAbortRef.current.abort();
    };
  }, [items, loading, hasSonarr, hasRadarr, hasOverseerr, hasJellyseerr, hasSeer, fetchArrStatus]);

  // Handler for arr status change
  const handleArrStatusChange = useCallback(async (itemId, arrType, newStatus) => {
    const isRequestType = arrType === "overseerr" || arrType === "jellyseerr" || arrType === "seer";
    const previousStatus = arrStatusMap[itemId];
    setArrStatusMap((prev) => ({ ...prev, [itemId]: { ...prev[itemId], ...newStatus } }));

    if (isRequestType) return;

    const item = (items || []).find((it) => it.id === itemId);
    if (!item) return;
    const hasIds = item.tmdbId || item.tvdbId;
    const hasTitle = item.titleClean?.trim() || item.title?.trim();
    if (!hasIds && !hasTitle) return;
    try {
      await apiPost("/api/arr/status", {
        items: [{
          releaseId: item.id,
          tvdbId: item.tvdbId || null,
          tmdbId: item.tmdbId || null,
          mediaType: isSeriesItem(item) ? "series" : "movie",
          title: item.titleClean?.trim() || item.title?.trim() || null,
        }],
      });
    } catch (error) {
      console.error("Failed to sync Arr status change", { itemId, arrType, error });
      setErr("Impossible de synchroniser le statut Arr.");
      setArrStatusMap((prev) => {
        const next = { ...prev };
        if (previousStatus == null) delete next[itemId];
        else next[itemId] = previousStatus;
        return next;
      });
    }
  }, [items, arrStatusMap, setErr]);

  // Filtered items
  const getUnifiedLabel = useCallback((it) => it?.unifiedCategoryLabel || "", []);

  // Destructure filter properties for stable dependencies
  const filterCategoryId = filters.categoryId;
  const filterApplicationId = filters.applicationId;
  const filterQuality = filters.quality;
  const filterSeen = filters.seen;
  const filterSortBy = filters.sortBy;
  const filterMaxAgeDays = filters.maxAgeDays;
  const filterQ = filters.q;
  const filterViewMode = filters.viewMode;
  const filterListSortBy = filters.listSortBy;
  const filterListSortDir = filters.listSortDir;
  const filterLimit = filters.limit;
  const filterApplicationType = useMemo(() => {
    if (!filterApplicationId) return "";
    if (filterApplicationId === "__hide_apps__") return "__hide_apps__";
    const row = installedApps.find((app) => String(app.id) === String(filterApplicationId));
    return row?.type ? String(row.type).toLowerCase() : "";
  }, [filterApplicationId, installedApps]);

  const { visibleItems, categoriesForDropdown, qualityOptions } = useLibraryDerivedData({
    items,
    sourceNameById,
    filterCategoryId,
    filterApplicationType,
    filterQuality,
    filterSeen,
    filterSortBy,
    filterMaxAgeDays,
    filterQ,
    filterViewMode,
    filterListSortBy,
    filterListSortDir,
    filterLimit,
    arrStatusById: arrStatusMap,
  });

  // Reset category if it no longer exists
  const setFilterCategoryId = filters.setCategoryId;
  const setFilterApplicationId = filters.setApplicationId;
  const setFilterQuality = filters.setQuality;

  useEffect(() => {
    if (!filterCategoryId) return;
    const exists = (categoriesForDropdown || []).some((c) => c.key === filterCategoryId);
    if (!exists) setFilterCategoryId("");
  }, [filterCategoryId, categoriesForDropdown, setFilterCategoryId]);

  useEffect(() => {
    if (!filterApplicationId) return;
    if (filterApplicationId === "__hide_apps__") return;
    const exists = (installedApps || []).some((app) => String(app.id) === String(filterApplicationId));
    if (!exists) setFilterApplicationId("");
  }, [filterApplicationId, installedApps, setFilterApplicationId]);

  useEffect(() => {
    if (!filterQuality) return;
    const wanted = String(filterQuality).trim().toLowerCase();
    const exists = (qualityOptions || []).some((q) => String(q).trim().toLowerCase() === wanted);
    if (!exists) setFilterQuality("");
  }, [filterQuality, qualityOptions, setFilterQuality]);

  const filterShowCategories = filters.uiSettings?.showCategories;
  useEffect(() => {
    if (filterShowCategories === false && filterCategoryId) {
      setFilterCategoryId("");
    }
  }, [filterShowCategories, filterCategoryId, setFilterCategoryId]);

  // Poster state refresh
  const applyPosterStateBatch = useCallback((rows) => {
    if (!Array.isArray(rows) || rows.length === 0) return;
    const byId = new Map(rows.map((row) => [Number(row.id), row]));
    if (byId.size === 0) return;
    setItems((prev) => prev.map((row) => {
      const update = byId.get(row.id);
      if (!update) return row;
      return mergePosterState(row, update);
    }));
    setSelectedItem((prev) => {
      if (!prev) return prev;
      const update = byId.get(prev.id);
      if (!update) return prev;
      return mergePosterState(prev, update);
    });
  }, [setSelectedItem]);

  const refreshVisiblePosterStates = useCallback(async (_reason, fingerprint) => {
    if (posterStateRefreshRef.current.inFlight) return;
    const ids = visibleItemIdsRef.current;
    if (ids.length === 0) return;
    if (fingerprint && posterStateRefreshRef.current.lastFingerprint === fingerprint) return;
    if (fingerprint) posterStateRefreshRef.current.lastFingerprint = fingerprint;
    posterStateRefreshRef.current.inFlight = true;
    await executeAsync(async () => {
      const res = await apiPost("/api/posters/releases/state", { ids });
      const rows = Array.isArray(res?.items) ? res.items : Array.isArray(res) ? res : [];
      applyPosterStateBatch(rows);
    }, {
      context: "Failed to refresh visible poster states in library",
      onFinally: () => {
        posterStateRefreshRef.current.inFlight = false;
      },
    });
  }, [applyPosterStateBatch]);

  useEffect(() => {
    visibleItemIdsRef.current = (visibleItems || []).map((it) => it.id).filter(Boolean);
  }, [visibleItems]);

  useEffect(() => {
    if (typeof window === "undefined") return undefined;
    const handler = (event) => {
      const detail = event?.detail || {};
      if (!detail.fingerprintChanged) return;
      refreshVisiblePosterStates(detail.reason, detail.fingerprint);
    };
    window.addEventListener("posters:polling:tick", handler);
    return () => window.removeEventListener("posters:polling:tick", handler);
  }, [refreshVisiblePosterStates]);

  // Actions
  const download = useCallback((it) => {
    const path = it.downloadPath || (it.id ? `/api/releases/${it.id}/download` : "");
    if (!path) return;
    openDownloadPath(path, { onError: setErr });
  }, [setErr]);

  const fetchGameDetails = useCallback(async (it) => {
    if (!it?.id) return;
    if (gameDetailsFetchRef.current.has(it.id)) return;
    gameDetailsFetchRef.current.add(it.id);
    await executeAsync(async () => {
      const res = await apiPost(`/api/releases/${it.id}/details/igdb`);
      const details = res?.details;
      if (!details) return;
      setItems((prev) => prev.map((row) => row.id === it.id ? { ...row, ...details } : row));
      setSelectedItem((prev) => prev?.id === it.id ? { ...prev, ...details } : prev);
    }, {
      context: "Failed to fetch IGDB details in library",
      onFinally: () => {
        gameDetailsFetchRef.current.delete(it.id);
      },
    });
  }, [setSelectedItem]);

  const openDetails = useCallback((it) => {
    if (!it) return;
    setSelectedItem(it);
    const isGame = isGameCategoryKey(it.unifiedCategoryKey) || isGameMediaType(it.mediaType);
    if (isGame && !hasDetailsPayload(it)) {
      fetchGameDetails(it);
    }
  }, [fetchGameDetails, setSelectedItem]);

  const closeDetails = useCallback(() => setSelectedItem(null), [setSelectedItem]);

  // Rename
  const renameRelease = useCallback((it) => {
    if (!it) return;
    const current = it.titleClean?.trim() ? it.titleClean : it.title;
    setRenameTarget(it);
    setRenameValue(current || "");
    setRenameOriginal(it.title || "");
    setRenameOpen(true);
  }, [setRenameOpen, setRenameOriginal, setRenameTarget, setRenameValue]);

  const closeRename = useCallback(() => {
    setRenameOpen(false);
    setRenameTarget(null);
    setRenameValue("");
    setRenameOriginal("");
  }, [setRenameOpen, setRenameOriginal, setRenameTarget, setRenameValue]);

  const saveRename = useCallback(async (e) => {
    e?.preventDefault?.();
    if (!renameTarget) return;
    const trimmed = renameValue.trim();
    if (!trimmed) return;
    try {
      setErr("");
      const res = await apiPost(`/api/releases/${renameTarget.id}/rename`, { title: trimmed });
      const newEntityId = res?.entityId ?? renameTarget.entityId ?? null;
      const posterUrl = res?.posterUrl ?? null;
      const posterUpdatedAtTs = res?.posterUpdatedAtTs ?? null;

      setItems((prev) => prev.map((row) =>
        row.id === renameTarget.id || (newEntityId && row.entityId === newEntityId)
          ? {
              ...row,
              ...(row.id === renameTarget.id ? {
                title: res?.title ?? trimmed,
                titleClean: res?.titleClean ?? row.titleClean,
                year: res?.year ?? row.year,
                season: res?.season ?? row.season,
                episode: res?.episode ?? row.episode,
                resolution: res?.resolution ?? row.resolution,
                source: res?.source ?? row.source,
                codec: res?.codec ?? row.codec,
                releaseGroup: res?.releaseGroup ?? row.releaseGroup,
                mediaType: res?.mediaType ?? row.mediaType,
                unifiedCategory: res?.unifiedCategory ?? row.unifiedCategory,
                entityId: newEntityId ?? row.entityId,
              } : {}),
              posterUrl,
              posterUpdatedAtTs: posterUpdatedAtTs ?? row.posterUpdatedAtTs,
            }
          : row
      ));
      setSelectedItem((prev) =>
        prev?.id === renameTarget.id || (newEntityId && prev?.entityId === newEntityId)
          ? { ...prev, posterUrl, posterUpdatedAtTs: posterUpdatedAtTs ?? prev?.posterUpdatedAtTs }
          : prev
      );
      closeRename();
    } catch (e) {
      setErr(e?.message || "Erreur renommage");
    }
  }, [renameTarget, renameValue, closeRename, setErr, setSelectedItem]);

  // Destructure selection for stable references
  const selectionSelectedIds = selection.selectedIds;
  const selectionExitMode = selection.exitSelectionMode;
  const selectionClearSelection = selection.clearSelection;

  // Bulk actions
  const bulkFetchPosters = useCallback(async () => {
    const ids = Array.from(selectionSelectedIds);
    if (ids.length === 0) return;
    setPosterAutoLoading(true);
    try {
      await apiPost("/api/posters/releases/fetch", { ids });
      triggerPosterPolling("bulk-fetch");
      await load();
      selectionExitMode();
    } finally {
      setPosterAutoLoading(false);
    }
  }, [selectionSelectedIds, selectionExitMode, load, setPosterAutoLoading]);

  const bulkSetSeen = useCallback(async (value) => {
    const ids = Array.from(selectionSelectedIds);
    if (ids.length === 0) return;
    await apiPost("/api/releases/seen", { ids, seen: value });
    setItems((prev) => prev.map((x) =>
      selectionSelectedIds.has(x.id) ? { ...x, seen: value ? 1 : 0 } : x
    ));
    selectionExitMode();
  }, [selectionSelectedIds, selectionExitMode]);

  const searchManualTimerRef = useRef(null);
  const searchManual = useCallback((query, mediaType) => {
    const q = (query || "").trim();
    if (!q) {
      setManualResults([]);
      return;
    }
    clearTimeout(searchManualTimerRef.current);
    searchManualTimerRef.current = setTimeout(async () => {
      setManualLoading(true);
      setManualErr("");
      try {
        const mt = (mediaType || "").trim();
        const res = await apiGet(`/api/posters/search?q=${encodeURIComponent(q)}&mediaType=${encodeURIComponent(mt)}`);
        const rows = Array.isArray(res?.results) ? res.results : [];
        setManualResults(sortManualResultsBySize(rows));
      } catch (e) {
        setManualErr(e?.message || "Erreur recherche posters");
      } finally {
        setManualLoading(false);
      }
    }, 350);
  }, [setManualErr, setManualLoading, setManualResults]);

  // Manual poster
  const openManualPoster = useCallback(() => {
    if (selectionSelectedIds.size !== 1) return;
    const id = Array.from(selectionSelectedIds)[0];
    const it = (items || []).find((x) => x.id === id);
    if (!it) return;
    const q = it.titleClean?.trim() ? it.titleClean : it.title;
    const rawMediaType = String(it.mediaType || "").trim().toLowerCase();
    const unifiedKey = String(it.unifiedCategoryKey || "").trim().toLowerCase();
    let nextMediaType = rawMediaType;
    if (!nextMediaType || nextMediaType === "unknown") {
      if (isGameCategoryKey(unifiedKey)) nextMediaType = "game";
      else if (unifiedKey === "audio") nextMediaType = "audio";
      else if (unifiedKey === "books" || unifiedKey === "book") nextMediaType = "book";
      else if (unifiedKey === "comics" || unifiedKey === "comic") nextMediaType = "comic";
      else if (unifiedKey === "anime") nextMediaType = "anime";
      else if (unifiedKey === "series" || unifiedKey === "shows") nextMediaType = "series";
      else nextMediaType = "movie";
    }
    setManualTarget(it);
    setManualQuery(q || "");
    setManualResults([]);
    setManualErr("");
    setManualMediaType(nextMediaType);
    setManualOpen(true);
    if (q) searchManual(q, nextMediaType);
  }, [selectionSelectedIds, items, searchManual, setManualErr, setManualMediaType, setManualOpen, setManualQuery, setManualResults, setManualTarget]);

  const closeManualPoster = useCallback(() => {
    setManualOpen(false);
    setManualQuery("");
    setManualResults([]);
    setManualErr("");
    setManualTarget(null);
    setManualMediaType("");
  }, [setManualErr, setManualMediaType, setManualOpen, setManualQuery, setManualResults, setManualTarget]);

  const applyManualPoster = useCallback(async (result) => {
    if (!manualTarget || !result) return;
    try {
      const provider = String(result.provider || "tmdb").toLowerCase();
      let payload = null;
      if (provider === "igdb") {
        const posterPath = result.posterPath || result.posterUrl;
        if (!posterPath) return;
        payload = { provider: "igdb", igdbId: result.igdbId, posterPath };
      } else if (provider === "theaudiodb") {
        const posterPath = result.posterPath || result.posterUrl;
        if (!posterPath) return;
        payload = { provider: "theaudiodb", providerId: result.providerId || null, posterPath };
      } else if (provider === "googlebooks") {
        const posterPath = result.posterPath || result.posterUrl;
        if (!posterPath) return;
        payload = { provider: "googlebooks", providerId: result.providerId || null, posterPath };
      } else if (provider === "comicvine") {
        const posterPath = result.posterPath || result.posterUrl;
        if (!posterPath) return;
        payload = { provider: "comicvine", providerId: result.providerId || null, posterPath };
      } else if (provider === "musicbrainz") {
        const posterPath = result.posterPath || result.posterUrl;
        if (!posterPath) return;
        payload = { provider: "musicbrainz", providerId: result.providerId || null, posterPath };
      } else if (provider === "openlibrary") {
        const posterPath = result.posterPath || result.posterUrl;
        if (!posterPath) return;
        payload = { provider: "openlibrary", providerId: result.providerId || null, posterPath };
      } else if (provider === "rawg") {
        const posterPath = result.posterPath || result.posterUrl;
        if (!posterPath) return;
        payload = { provider: "rawg", providerId: result.providerId || null, posterPath };
      } else {
        if (!result.posterPath) return;
        payload = { provider: "tmdb", tmdbId: result.tmdbId, posterPath: result.posterPath };
      }
      const res = await apiPost(`/api/posters/release/${manualTarget.id}/manual`, payload);
      const responseEntityId = Number(res?.entityId || manualTarget.entityId || 0) || null;
      const posterUpdatedAtTs = Number(res?.posterUpdatedAtTs || 0) || null;
      const posterFile = res?.posterFile || null;
      const targetTitle = normalizeTitleKey(manualTarget.titleClean || manualTarget.title);
      const targetYear = manualTarget.year != null ? Number(manualTarget.year) : null;

      const matchesFallback = (row) => {
        const rowTitle = normalizeTitleKey(row.titleClean || row.title);
        if (!rowTitle || rowTitle !== targetTitle) return false;
        if (targetYear != null && row.year != null && Number(row.year) !== targetYear) return false;
        return true;
      };

      const matchesRow = (row) => {
        if (responseEntityId && row.entityId) return Number(row.entityId) === responseEntityId;
        if (!row.entityId) return matchesFallback(row);
        return row.id === manualTarget.id;
      };

      const applyUpdate = (row) => {
        const updatedTs = posterUpdatedAtTs || row.posterUpdatedAtTs;
        const posterUrl = updatedTs
          ? buildPosterUrl(row.id, updatedTs)
          : (row.id === manualTarget.id && res?.posterUrl ? res.posterUrl : buildPosterUrl(row.id, updatedTs));
        return {
          ...row,
          entityId: responseEntityId || row.entityId,
          posterFile: posterFile || row.posterFile,
          posterUpdatedAtTs: updatedTs,
          posterUrl,
          posterLastError: null,
        };
      };

      setItems((prev) => prev.map((row) => matchesRow(row) ? applyUpdate(row) : row));
      setSelectedItem((prev) => prev && matchesRow(prev) ? applyUpdate(prev) : prev);
      triggerPosterPolling("manual-set");
      closeManualPoster();
      selectionClearSelection();
    } catch (e) {
      setManualErr(e?.message || "Erreur poster manuel");
    }
  }, [manualTarget, closeManualPoster, selectionClearSelection, setManualErr, setSelectedItem]);

  // Déstructurer les propriétés pour éviter les re-renders infinis dans useEffect
  const {
    selectionMode,
    selectedIds,
    toggleSelectionMode,
    selectAllVisible,
    toggleSelect,
  } = selection;

  const {
    sortBy,
    maxAgeDays,
    setMaxAgeDays,
    seen,
    setSeen,
    applicationId,
    setApplicationId,
    quality,
    setQuality,
    filtersOpen,
    setFiltersOpen,
    sourceId,
    setSourceId,
    uiSettings,
    categoryId,
    setCategoryId,
    setSortBy,
    viewMode,
    setViewMode,
    viewOptions,
    limit,
    setLimit,
    listSortBy,
    listSortDir,
    toggleListSort,
    selectedSourceName,
    defaultSort,
    defaultMaxAgeDays,
    defaultLimit,
  } = filters;

  const handleSelectAllVisible = useCallback(() => {
    selectAllVisible(visibleItems);
  }, [selectAllVisible, visibleItems]);
  const handleBulkFetchPosters = useCallback(() => {
    bulkFetchPosters();
  }, [bulkFetchPosters]);
  const handleOpenManualPoster = useCallback(() => {
    openManualPoster();
  }, [openManualPoster]);
  const handleBulkSetSeen = useCallback((value) => {
    bulkSetSeen(value);
  }, [bulkSetSeen]);

  const subbarProps = useMemo(() => ({
    subbarClassName: `subbar--library${filtersOpen && !selectionMode ? " subbar--library-filter-open" : ""}`,
    selectionMode,
    selectedIds,
    onToggleSelectionMode: toggleSelectionMode,
    onSelectAll: handleSelectAllVisible,
    posterAutoLoading,
    onBulkFetchPosters: handleBulkFetchPosters,
    onOpenManualPoster: handleOpenManualPoster,
    onBulkSetSeen: handleBulkSetSeen,
    sortBy,
    maxAgeDays,
    setMaxAgeDays,
    seen,
    setSeen,
    applicationId,
    setApplicationId,
    quality,
    setQuality,
    qualityOptions,
    filtersOpen,
    setFiltersOpen,
    installedApps,
    sources,
    enabledSources,
    sourceId,
    setSourceId,
    uiSettings,
    categoryId,
    setCategoryId,
    categoriesForDropdown,
    setSortBy,
    viewMode,
    setViewMode,
    viewOptions,
    limit,
    setLimit,
    defaultSort,
    defaultMaxAgeDays,
    defaultLimit,
  }), [
    filtersOpen, selectionMode, selectedIds, toggleSelectionMode, handleSelectAllVisible,
    posterAutoLoading, handleBulkFetchPosters, handleOpenManualPoster,
    handleBulkSetSeen, sortBy, maxAgeDays, setMaxAgeDays, sources,
    seen, setSeen, applicationId, setApplicationId, quality, setQuality,
    qualityOptions, setFiltersOpen, installedApps,
    enabledSources, sourceId, setSourceId, uiSettings, categoryId,
    setCategoryId, categoriesForDropdown, setSortBy, viewMode, setViewMode,
    viewOptions, limit, setLimit, defaultSort, defaultMaxAgeDays, defaultLimit,
  ]);

  useEffect(() => {
    setContent(<LibrarySubbar {...subbarProps} />);
    return () => setContent(null);
  }, [setContent, subbarProps]);

  return (
    <div className="page page--library">
      <div className="pagehead">
        <div>
          <h1>{selectedSourceName}</h1>
          <div className="muted">
            {err ? "—" : `Résultats: ${visibleItems.length}`}
          </div>
        </div>
        {(viewMode === "grid" || viewMode === "missing" || viewMode === "poster") && (
          <div className="library-size-slider">
            <LayoutGrid className="library-size-slider__icon" />
            <input
              type="range"
              min={SLIDER_MIN}
              max={100}
              value={viewMode === "poster" ? posterCardSlider : gridCardSlider}
              onChange={viewMode === "poster" ? handlePosterSlider : handleGridSlider}
            />
            <LayoutGrid className="library-size-slider__icon library-size-slider__icon--lg" />
          </div>
        )}
      </div>

      {loading && <Loader label="Chargement du feed…" />}

      {!loading && err && (
        <div className="errorbox">
          <div className="errorbox__title">Erreur</div>
          <div className="muted">{err}</div>
        </div>
      )}

      {!loading && !err && (viewMode === "grid" || viewMode === "missing") && (
        <LibraryGrid
          items={visibleItems}
          onDownload={download}
          onOpen={openDetails}
          selectionMode={selectionMode}
          selectedIds={selectedIds}
          onToggleSelect={toggleSelect}
          onRename={renameRelease}
          sortBy={sortBy}
          sourceNameById={sourceNameById}
          sourceColorById={sourceColorById}
          sourceId={sourceId}
          arrStatusMap={arrStatusMap}
          integrationMode={requestMode}
          cardSize={gridCardSize}
        />
      )}

      {!loading && !err && viewMode === "poster" && (
        <LibraryPoster
          items={visibleItems}
          onOpen={openDetails}
          selectionMode={selectionMode}
          selectedIds={selectedIds}
          onToggleSelect={toggleSelect}
          onRename={renameRelease}
          sourceNameById={sourceNameById}
          sourceColorById={sourceColorById}
          sourceId={sourceId}
          arrStatusMap={arrStatusMap}
          integrationMode={requestMode}
          cardSize={posterCardSize}
        />
      )}

      {!loading && !err && viewMode === "list" && (
        <LibraryList
          items={visibleItems}
          selectionMode={selectionMode}
          selectedIds={selectedIds}
          onToggleSelect={toggleSelect}
          onOpen={openDetails}
          onRename={renameRelease}
          listSortBy={listSortBy}
          listSortDir={listSortDir}
          onToggleSort={toggleListSort}
          sourceNameById={sourceNameById}
          sourceColorById={sourceColorById}
          getUnifiedLabel={getUnifiedLabel}
        />
      )}

      {!loading && !err && viewMode === "banner" && (
        <LibraryBanner
          items={visibleItems}
          selectionMode={selectionMode}
          selectedIds={selectedIds}
          onToggleSelect={toggleSelect}
          onOpen={openDetails}
          sourceNameById={sourceNameById}
          sourceColorById={sourceColorById}
        />
      )}

      <ReleaseModal
        open={!!selectedItem}
        item={selectedItem}
        onClose={closeDetails}
        onDownload={download}
        categoryLabel={selectedItem?.categoryName || selectedItem?.unifiedCategoryLabel || ""}
        indexerLabel={sourceNameById.get(Number(selectedItem?.sourceId)) || ""}
        indexerColor={sourceColorById.get(Number(selectedItem?.sourceId)) || null}
        hasSonarr={hasSonarr}
        hasRadarr={hasRadarr}
        hasOverseerr={hasOverseerr}
        hasJellyseerr={hasJellyseerr}
        hasSeer={hasSeer}
        integrationMode={requestMode}
        arrStatus={selectedItem ? arrStatusMap[selectedItem.id] : null}
        onArrStatusChange={handleArrStatusChange}
      />

      <RenameModal
        open={renameOpen}
        onClose={closeRename}
        renameValue={renameValue}
        setRenameValue={setRenameValue}
        renameOriginal={renameOriginal}
        onSave={saveRename}
      />

      <ManualPosterModal
        open={manualOpen}
        onClose={closeManualPoster}
        query={manualQuery}
        setQuery={setManualQuery}
        results={manualResults}
        loading={manualLoading}
        error={manualErr}
        onSearch={searchManual}
        onApply={applyManualPoster}
        mediaType={manualMediaType}
      />
    </div>
  );
}
