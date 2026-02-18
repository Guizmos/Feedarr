import { useCallback, useEffect, useMemo, useState } from "react";
import { useSearchParams } from "react-router-dom";
import { apiGet, apiPut } from "../../../api/client.js";

const MAX_AGE_VALUES = new Set(["", "1", "2", "3", "7", "15", "30"]);
const SEEN_VALUES = new Set(["", "1", "0"]);
const APP_FILTER_SPECIAL_VALUES = new Set(["", "__hide_apps__"]);

function safeGetStorage(key) {
  try { return window.localStorage.getItem(key); }
  catch { return null; }
}

function safeSetStorage(key, value) {
  try { window.localStorage.setItem(key, value); }
  catch { /* quota exceeded or private browsing */ }
}

/**
 * Hook pour gérer les filtres et préférences de la bibliothèque
 */
export default function useLibraryFilters(sources, enabledSources) {
  const [searchParams] = useSearchParams();
  const [sourceId, setSourceId] = useState("");
  const [q, setQ] = useState("");
  const [categoryId, setCategoryId] = useState(() => safeGetStorage("feedarr.library.filter.categoryId") || "");
  const [sortBy, setSortBy] = useState("date");
  const [maxAgeDays, setMaxAgeDays] = useState(() => {
    const stored = safeGetStorage("feedarr.library.filter.maxAgeDays");
    return MAX_AGE_VALUES.has(stored ?? "") ? (stored ?? "") : "";
  });
  const [viewMode, setViewMode] = useState("grid");
  const [listSortBy, setListSortBy] = useState("date");
  const [listSortDir, setListSortDir] = useState("desc");
  const [seen, setSeen] = useState(() => {
    const stored = safeGetStorage("feedarr.library.filter.seen");
    return SEEN_VALUES.has(stored ?? "") ? (stored ?? "") : "";
  });
  const [applicationId, setApplicationId] = useState(() => {
    const stored = safeGetStorage("feedarr.library.filter.applicationId") || "";
    return APP_FILTER_SPECIAL_VALUES.has(stored) || /^-?\d+$/.test(stored) ? stored : "";
  });
  const [quality, setQuality] = useState(() => safeGetStorage("feedarr.library.filter.quality") || "");
  const [filtersOpen, setFiltersOpen] = useState(
    () => safeGetStorage("feedarr.library.filterbar.open") === "1"
  );
  const [uiSettings, setUiSettings] = useState(null);
  const [sourceReady, setSourceReady] = useState(false);

  const [limit, setLimit] = useState(() => {
    const stored = safeGetStorage("feedarr.library.limit");
    if (stored === "all") return "all";
    const n = Number(stored);
    return Number.isFinite(n) && n > 0 ? n : 100;
  });

  const viewOptions = useMemo(() => {
    const options = [
      { value: "grid", label: "Cartes" },
      { value: "poster", label: "Poster" },
      { value: "banner", label: "Banner" },
      { value: "list", label: "Liste" },
    ];
    if (uiSettings?.enableMissingPosterView) {
      options.push({ value: "missing", label: "Sans poster" });
    }
    return options;
  }, [uiSettings?.enableMissingPosterView]);

  const selectedSourceName = useMemo(() => {
    const sid = Number(sourceId);
    if (!Number.isFinite(sid) || sid <= 0) return "Bibliotheque";
    const row = enabledSources.find((s) => Number(s.id ?? s.sourceId) === sid);
    return row?.name ?? row?.title ?? `Source ${sid}`;
  }, [sourceId, enabledSources]);

  // Charger la source courante depuis localStorage (ou defaults UI)
  useEffect(() => {
    if (!uiSettings) return;
    if (!sources || sources.length === 0) return;
    const storedRaw = safeGetStorage("feedarr.library.sourceId");
    const stored = storedRaw ?? "";
    const defaultSourceId = String(uiSettings?.defaultFilterSourceId ?? "").trim();
    const isValid = (id) =>
      enabledSources.some((s) => String(s.id ?? s.sourceId) === String(id));

    setSourceId((prev) => {
      if (prev && isValid(prev)) return prev;
      if (stored && isValid(stored)) return stored;
      if (storedRaw == null && defaultSourceId && isValid(defaultSourceId)) return defaultSourceId;
      if (!prev && stored === "") return "";
      const first = enabledSources[0]?.id ?? enabledSources[0]?.sourceId;
      return first != null ? String(first) : "";
    });
    setSourceReady(true);
  }, [sources, enabledSources, uiSettings]);

  // Persister sourceId
  useEffect(() => {
    if (!sourceReady) return;
    safeSetStorage("feedarr.library.sourceId", String(sourceId || ""));
  }, [sourceId, sourceReady]);

  // Persister categoryId
  useEffect(() => {
    safeSetStorage("feedarr.library.filter.categoryId", String(categoryId || ""));
  }, [categoryId]);

  // Persister maxAgeDays
  useEffect(() => {
    safeSetStorage("feedarr.library.filter.maxAgeDays", String(maxAgeDays || ""));
  }, [maxAgeDays]);

  // Persister seen
  useEffect(() => {
    safeSetStorage("feedarr.library.filter.seen", String(seen || ""));
  }, [seen]);

  // Persister applicationId
  useEffect(() => {
    safeSetStorage("feedarr.library.filter.applicationId", String(applicationId || ""));
  }, [applicationId]);

  // Persister quality
  useEffect(() => {
    safeSetStorage("feedarr.library.filter.quality", String(quality || ""));
  }, [quality]);

  // Persister ouverture de la filterbar
  useEffect(() => {
    safeSetStorage("feedarr.library.filterbar.open", filtersOpen ? "1" : "0");
  }, [filtersOpen]);

  // Persister limit
  useEffect(() => {
    safeSetStorage("feedarr.library.limit", String(limit || 100));
  }, [limit]);

  // Charger les paramètres UI
  useEffect(() => {
    apiGet("/api/settings/ui")
      .then((ui) => {
        const def = String(ui?.defaultView || "grid").toLowerCase();
        const normalized = def === "cards" ? "grid" : def;
        if (["grid", "list", "banner", "poster"].includes(normalized)) {
          setViewMode(normalized);
        }

        // Apply default sort
        const defSort = String(ui?.defaultSort || "date").toLowerCase();
        if (["date", "seeders", "downloads"].includes(defSort)) {
          setSortBy(defSort);
        }

        // Apply default maxAgeDays
        const defMaxAge = String(ui?.defaultMaxAgeDays ?? "");
        const storedMaxAge = safeGetStorage("feedarr.library.filter.maxAgeDays");
        if (storedMaxAge == null && MAX_AGE_VALUES.has(defMaxAge)) {
          setMaxAgeDays(defMaxAge);
        }

        // Apply default seen (only if no localStorage override)
        const storedSeen = safeGetStorage("feedarr.library.filter.seen");
        const defSeen = String(ui?.defaultFilterSeen ?? "");
        if (storedSeen == null && SEEN_VALUES.has(defSeen)) {
          setSeen(defSeen);
        }

        // Apply default application filter (only if no localStorage override)
        const storedApp = safeGetStorage("feedarr.library.filter.applicationId");
        const defApp = String(ui?.defaultFilterApplication ?? "");
        if (storedApp == null) {
          if (APP_FILTER_SPECIAL_VALUES.has(defApp) || /^-?\d+$/.test(defApp)) {
            setApplicationId(defApp);
          }
        }

        // Apply default category/quality (only if no localStorage override)
        const storedCategory = safeGetStorage("feedarr.library.filter.categoryId");
        if (storedCategory == null) {
          setCategoryId(String(ui?.defaultFilterCategoryId ?? ""));
        }
        const storedQuality = safeGetStorage("feedarr.library.filter.quality");
        if (storedQuality == null) {
          setQuality(String(ui?.defaultFilterQuality ?? ""));
        }

        // Apply default limit (only if no localStorage override)
        const storedLimit = safeGetStorage("feedarr.library.limit");
        if (!storedLimit) {
          const defLimit = Number(ui?.defaultLimit ?? 100);
          if (defLimit === 0) {
            setLimit("all");
          } else if ([50, 100, 200, 500].includes(defLimit)) {
            setLimit(defLimit);
          }
        }

        setUiSettings(ui || null);
      })
      .catch((error) => {
        console.error("Failed to load UI settings for library filters", error);
      });
  }, []);

  // Réinitialiser viewMode si missing désactivé
  useEffect(() => {
    if (uiSettings?.enableMissingPosterView !== false) return;
    if (viewMode !== "missing") return;
    const def = String(uiSettings?.defaultView || "grid").toLowerCase();
    const normalized = def === "cards" ? "grid" : def;
    setViewMode(["grid", "list", "banner", "poster"].includes(normalized) ? normalized : "grid");
  }, [uiSettings?.enableMissingPosterView, uiSettings?.defaultView, viewMode]);

  // Synchroniser q avec searchParams
  useEffect(() => {
    const next = (searchParams.get("q") || "").trim();
    setQ((prev) => (prev === next ? prev : next));
  }, [searchParams]);

  // Synchroniser listSort avec sortBy
  useEffect(() => {
    if (viewMode !== "list" && viewMode !== "banner") return;
    if (!sortBy) return;
    setListSortBy(sortBy);
    setListSortDir("desc");
  }, [sortBy, viewMode]);

  const toggleListSort = useCallback((key) => {
    setListSortDir((prevDir) =>
      listSortBy === key ? (prevDir === "asc" ? "desc" : "asc") : "asc"
    );
    setListSortBy(key);
  }, [listSortBy]);

  const persistViewMode = useCallback(async (next) => {
    const normalized = String(next || "grid").toLowerCase();
    const value = normalized === "cards" ? "grid" : normalized;
    if (value === "missing") {
      setViewMode("missing");
      return;
    }
    if (!["grid", "list", "banner", "poster"].includes(value)) return;
    setViewMode(value);
    try {
      let current = uiSettings;
      if (!current) {
        current = await apiGet("/api/settings/ui");
      }
      const payload = { ...(current || {}), defaultView: value };
      const saved = await apiPut("/api/settings/ui", payload);
      setUiSettings(saved || payload);
    } catch (error) {
      console.error("Failed to persist library default view", error);
    }
  }, [uiSettings]);

  // Computed defaults from uiSettings
  const defaultSort = uiSettings?.defaultSort || "date";
  const defaultMaxAgeDays = uiSettings?.defaultMaxAgeDays ?? "";
  const defaultLimit = uiSettings?.defaultLimit === 0 ? "all" : (uiSettings?.defaultLimit || 100);
  const defaultSeen = uiSettings?.defaultFilterSeen ?? "";
  const defaultApplication = uiSettings?.defaultFilterApplication ?? "";
  const defaultCategoryId = uiSettings?.defaultFilterCategoryId ?? "";
  const defaultQuality = uiSettings?.defaultFilterQuality ?? "";

  return {
    sourceId,
    setSourceId,
    q,
    setQ,
    categoryId,
    setCategoryId,
    sortBy,
    setSortBy,
    maxAgeDays,
    setMaxAgeDays,
    viewMode,
    setViewMode: persistViewMode,
    listSortBy,
    listSortDir,
    toggleListSort,
    seen,
    setSeen,
    applicationId,
    setApplicationId,
    quality,
    setQuality,
    filtersOpen,
    setFiltersOpen,
    limit,
    setLimit,
    uiSettings,
    viewOptions,
    selectedSourceName,
    sourceReady,
    defaultSort,
    defaultMaxAgeDays,
    defaultLimit,
    defaultSeen,
    defaultApplication,
    defaultCategoryId,
    defaultQuality,
  };
}
