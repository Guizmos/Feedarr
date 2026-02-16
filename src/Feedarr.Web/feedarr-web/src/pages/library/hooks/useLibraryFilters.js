import { useCallback, useEffect, useMemo, useState } from "react";
import { useSearchParams } from "react-router-dom";
import { apiGet, apiPut } from "../../../api/client.js";

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
  const [categoryId, setCategoryId] = useState("");
  const [sortBy, setSortBy] = useState("date");
  const [maxAgeDays, setMaxAgeDays] = useState("");
  const [viewMode, setViewMode] = useState("grid");
  const [listSortBy, setListSortBy] = useState("date");
  const [listSortDir, setListSortDir] = useState("desc");
  const [seen, setSeen] = useState("");
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

  // Charger les sources depuis le localStorage
  useEffect(() => {
    if (!sources || sources.length === 0) return;
    const stored = safeGetStorage("feedarr.library.sourceId") || "";
    const isValid = (id) =>
      enabledSources.some((s) => String(s.id ?? s.sourceId) === String(id));

    setSourceId((prev) => {
      if (prev && isValid(prev)) return prev;
      if (!prev && stored === "") return "";
      if (stored && isValid(stored)) return stored;
      const first = enabledSources[0]?.id ?? enabledSources[0]?.sourceId;
      return first != null ? String(first) : "";
    });
    setSourceReady(true);
  }, [sources, enabledSources]);

  // Persister sourceId
  useEffect(() => {
    if (!sourceReady) return;
    safeSetStorage("feedarr.library.sourceId", String(sourceId || ""));
  }, [sourceId, sourceReady]);

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
        if (["", "1", "2", "3", "7", "15", "30"].includes(defMaxAge)) {
          setMaxAgeDays(defMaxAge);
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
    limit,
    setLimit,
    uiSettings,
    viewOptions,
    selectedSourceName,
    sourceReady,
    defaultSort,
    defaultMaxAgeDays,
    defaultLimit,
  };
}
