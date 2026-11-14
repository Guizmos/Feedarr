import { useMemo } from "react";
import { scoreResolution } from "../utils/helpers.js";
import { UNIFIED_CATEGORY_OPTIONS } from "../utils/constants.js";

function normalize(value) {
  return String(value || "").trim().toLowerCase();
}

export function matchesApplicationFilter(item, arrStatus, appType) {
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

// Search, catalog filters and ordering have already been applied before SQL LIMIT.
export default function useLibraryDerivedData({ items, facets, filterCategoryId, filterQuality }) {
  const categoriesForDropdown = useMemo(() => {
    const keys = new Set(facets?.categories || []);
    if (filterCategoryId) keys.add(filterCategoryId);
    return [...keys].map(key => UNIFIED_CATEGORY_OPTIONS.find(c => c.key === key) || { key, label: key });
  }, [facets, filterCategoryId]);
  const qualityOptions = useMemo(() => {
    const values = new Set(facets?.qualities || []);
    if (filterQuality) values.add(filterQuality);
    return [...values].sort((a, b) => scoreResolution(b) - scoreResolution(a));
  }, [facets, filterQuality]);
  return { filtered: items, visibleItems: items, categoriesForDropdown, qualityOptions };
}
