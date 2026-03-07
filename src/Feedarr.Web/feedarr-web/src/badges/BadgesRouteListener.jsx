import { useEffect, useMemo } from "react";
import { useLocation } from "react-router-dom";
import { useBadgeStoreContext } from "./useBadgeStore.js";
import { normalizePath } from "./pathUtils.js";

const DEFAULT_BASENAME =
  typeof import.meta !== "undefined"
  && typeof import.meta.env !== "undefined"
  && import.meta.env.BASE_URL
    ? import.meta.env.BASE_URL
    : "/";

export default function BadgesRouteListener() {
  const location = useLocation();
  const badges = useBadgeStoreContext();
  const { markSeenByRoute, latestActivityTs, latestReleasesTs, latestUpdateTag } = badges;

  const normalizedPath = useMemo(() => {
    return normalizePath(location.pathname, DEFAULT_BASENAME);
  }, [location.pathname]);

  useEffect(() => {
    markSeenByRoute(normalizedPath, DEFAULT_BASENAME);
  }, [
    markSeenByRoute,
    normalizedPath,
    latestActivityTs,
    latestReleasesTs,
    latestUpdateTag,
  ]);

  return null;
}
