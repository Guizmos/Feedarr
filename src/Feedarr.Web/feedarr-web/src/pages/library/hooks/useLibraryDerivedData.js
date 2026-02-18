import { useMemo } from "react";
import { getEpisodeSortValue, scoreResolution } from "../utils/helpers.js";
import { UNIFIED_CATEGORY_OPTIONS } from "../utils/constants.js";

function normalize(value) {
  return String(value || "").trim().toLowerCase();
}

function matchesApplicationFilter(item, arrStatus, appType) {
  const type = normalize(appType);
  if (!type) return true;
  const hasAnyApp =
    !!(arrStatus?.inSonarr || arrStatus?.inRadarr || arrStatus?.inOverseerr || arrStatus?.inJellyseerr || arrStatus?.inSeer)
    || !!(item?.isInSonarr || item?.isInRadarr);
  if (type === "__hide_apps__") return !hasAnyApp;
  if (type === "sonarr") return !!(arrStatus?.inSonarr || item?.isInSonarr);
  if (type === "radarr") return !!(arrStatus?.inRadarr || item?.isInRadarr);
  if (type === "overseerr") return !!arrStatus?.inOverseerr;
  if (type === "jellyseerr") return !!arrStatus?.inJellyseerr;
  if (type === "seer") return !!arrStatus?.inSeer;
  return true;
}

export default function useLibraryDerivedData({
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
  arrStatusById,
}) {
  const preCategoryFiltered = useMemo(() => {
    let result = items || [];
    result = result.filter((it) => it.unifiedCategoryKey);

    if (filterApplicationType) {
      result = result.filter((it) =>
        matchesApplicationFilter(it, arrStatusById?.[it.id], filterApplicationType)
      );
    }

    if (filterSeen) {
      const sv = filterSeen === "1" ? 1 : filterSeen === "0" ? 0 : null;
      if (sv !== null) result = result.filter((it) => Number(it.seen) === sv);
    }
    if (filterMaxAgeDays) {
      const days = Number(filterMaxAgeDays);
      if (Number.isFinite(days) && days > 0) {
        const now = Date.now() / 1000;
        const maxAgeSeconds = days * 24 * 60 * 60;
        result = result.filter((it) => {
          const ts = Number(it.publishedAt || 0);
          if (!Number.isFinite(ts) || ts <= 0) return false;
          return now - ts <= maxAgeSeconds;
        });
      }
    }

    const s = String(filterQ || "").trim().toLowerCase();
    if (s) {
      result = result.filter((it) => String(it.title || "").toLowerCase().includes(s));
    }

    return result;
  }, [items, filterApplicationType, arrStatusById, filterSeen, filterMaxAgeDays, filterQ]);

  const categoryFiltered = useMemo(() => {
    if (!filterCategoryId) return preCategoryFiltered;
    return preCategoryFiltered.filter((it) => it.unifiedCategoryKey === filterCategoryId);
  }, [preCategoryFiltered, filterCategoryId]);

  const qualityOptions = useMemo(() => {
    const byKey = new Map();
    (categoryFiltered || []).forEach((it) => {
      const raw = String(it?.resolution || "").trim();
      const key = normalize(raw);
      if (!key) return;
      if (!byKey.has(key)) byKey.set(key, raw);
    });

    const ordered = Array.from(byKey.entries())
      .sort((a, b) => {
        const scoreDiff = scoreResolution(b[1]) - scoreResolution(a[1]);
        if (scoreDiff !== 0) return scoreDiff;
        return a[1].localeCompare(b[1], "fr-FR", { sensitivity: "base" });
      })
      .map(([, label]) => label);

    const selectedKey = normalize(filterQuality);
    if (selectedKey && !byKey.has(selectedKey)) {
      return [filterQuality, ...ordered];
    }
    return ordered;
  }, [categoryFiltered, filterQuality]);

  const filtered = useMemo(() => {
    let result = categoryFiltered;

    if (filterQuality) {
      const wanted = normalize(filterQuality);
      result = result.filter((it) => normalize(it?.resolution) === wanted);
    }

    if (filterViewMode === "missing") {
      result = result.filter((it) => !it.posterUrl);
    }

    const sorted = [...result];
    if (filterViewMode === "list" || filterViewMode === "banner") {
      const dir = filterListSortDir === "asc" ? 1 : -1;
      const byTitle = (v) => String(v.titleClean || v.title || "").toLowerCase();
      const bySource = (v) => String(sourceNameById.get(Number(v.sourceId)) || "").toLowerCase();
      const byCategory = (v) => String(v.unifiedCategoryLabel || "").toLowerCase();
      const byEpisode = (v) => getEpisodeSortValue(v);
      const byQuality = (v) => scoreResolution(v?.resolution);
      const byCodec = (v) => String(v.codec || "").toLowerCase();
      const bySize = (v) => Number(v.sizeBytes || v.size_bytes || 0);

      sorted.sort((a, b) => {
        let av;
        let bv;
        if (filterListSortBy === "title") { av = byTitle(a); bv = byTitle(b); }
        else if (filterListSortBy === "source") { av = bySource(a); bv = bySource(b); }
        else if (filterListSortBy === "category") { av = byCategory(a); bv = byCategory(b); }
        else if (filterListSortBy === "episode") { av = byEpisode(a); bv = byEpisode(b); }
        else if (filterListSortBy === "quality") { av = byQuality(a); bv = byQuality(b); }
        else if (filterListSortBy === "codec") { av = byCodec(a); bv = byCodec(b); }
        else if (filterListSortBy === "size") { av = bySize(a); bv = bySize(b); }
        else if (filterListSortBy === "seeders") { av = Number(a.seeders || 0); bv = Number(b.seeders || 0); }
        else if (filterListSortBy === "downloads") { av = Number(a.grabs || 0); bv = Number(b.grabs || 0); }
        else { av = Number(a.publishedAt || 0); bv = Number(b.publishedAt || 0); }
        if (av < bv) return -1 * dir;
        if (av > bv) return 1 * dir;
        return 0;
      });
    } else if (filterSortBy === "seeders") {
      sorted.sort((a, b) => Number(b.seeders || 0) - Number(a.seeders || 0));
    } else if (filterSortBy === "downloads") {
      sorted.sort((a, b) => Number(b.grabs || 0) - Number(a.grabs || 0));
    } else {
      sorted.sort((a, b) => Number(b.publishedAt || 0) - Number(a.publishedAt || 0));
    }

    return sorted;
  }, [
    categoryFiltered,
    filterQuality,
    filterViewMode,
    filterListSortBy,
    filterListSortDir,
    filterSortBy,
    sourceNameById,
  ]);

  const visibleItems = useMemo(() => {
    if (filterLimit === "all") return filtered || [];
    const max = Math.max(1, Number(filterLimit) || 100);
    return (filtered || []).slice(0, max);
  }, [filtered, filterLimit]);

  const categoriesForDropdown = useMemo(() => {
    const labels = new Map();
    (preCategoryFiltered || []).forEach((it) => {
      const key = it.unifiedCategoryKey;
      if (!key) return;
      if (!labels.has(key)) labels.set(key, it.unifiedCategoryLabel || key);
    });
    if (filterCategoryId && !labels.has(filterCategoryId)) labels.set(filterCategoryId, filterCategoryId);
    const ordered = UNIFIED_CATEGORY_OPTIONS
      .filter((c) => labels.has(c.key))
      .map((c) => ({ key: c.key, label: labels.get(c.key) || c.label }));
    const extras = Array.from(labels.keys())
      .filter((k) => !UNIFIED_CATEGORY_OPTIONS.some((c) => c.key === k))
      .map((k) => ({ key: k, label: labels.get(k) || k }));
    return ordered.concat(extras);
  }, [preCategoryFiltered, filterCategoryId]);

  return {
    filtered,
    qualityOptions,
    visibleItems,
    categoriesForDropdown,
  };
}
