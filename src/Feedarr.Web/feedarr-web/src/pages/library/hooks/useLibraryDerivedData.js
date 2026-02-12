import { useMemo } from "react";
import { getEpisodeSortValue, scoreResolution } from "../utils/helpers.js";
import { UNIFIED_CATEGORY_OPTIONS } from "../utils/constants.js";

export default function useLibraryDerivedData({
  items,
  sourceNameById,
  filterCategoryId,
  filterSeen,
  filterSortBy,
  filterMaxAgeDays,
  filterQ,
  filterViewMode,
  filterListSortBy,
  filterListSortDir,
  filterLimit,
}) {
  const preCategoryFiltered = useMemo(() => {
    let result = items || [];
    result = result.filter((it) => it.unifiedCategoryKey);

    if (filterSeen) {
      const sv = filterSeen === "1" ? 1 : filterSeen === "0" ? 0 : null;
      if (sv !== null) result = result.filter((it) => Number(it.seen) === sv);
    }
    if (filterSortBy === "date" && filterMaxAgeDays) {
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
  }, [items, filterSeen, filterSortBy, filterMaxAgeDays, filterQ]);

  const filtered = useMemo(() => {
    let result = preCategoryFiltered;

    if (filterCategoryId) {
      result = result.filter((it) => it.unifiedCategoryKey === filterCategoryId);
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
    preCategoryFiltered,
    filterCategoryId,
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
    visibleItems,
    categoriesForDropdown,
  };
}
