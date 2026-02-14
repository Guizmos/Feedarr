import { useCallback, useEffect, useRef, useState } from "react";
import { apiGet } from "../api/client.js";
import { normalizeRequestMode } from "../utils/appTypes.js";

/**
 * Hook pour gérer la disponibilité des applications Sonarr/Radarr
 * - Charge les apps depuis /api/apps
 * - Fournit hasSonarr / hasRadarr (enabled + hasApiKey)
 * - Fournit les apps par type pour le fallback
 */
export default function useArrApps({ pollMs = 60000 } = {}) {
  const [apps, setApps] = useState([]);
  const [integrationMode, setIntegrationMode] = useState("arr");
  const [loading, setLoading] = useState(true);
  const timer = useRef(null);
  const refreshInFlight = useRef(false);

  const refresh = useCallback(async () => {
    if (refreshInFlight.current) return;
    refreshInFlight.current = true;
    try {
      const [appsResult, generalResult] = await Promise.allSettled([
        apiGet("/api/apps"),
        apiGet("/api/settings/general"),
      ]);

      const appsData = appsResult.status === "fulfilled" ? appsResult.value : [];
      const general = generalResult.status === "fulfilled" ? generalResult.value : null;
      const mode = normalizeRequestMode(general?.requestIntegrationMode);

      setApps(Array.isArray(appsData) ? appsData : []);
      setIntegrationMode(mode);
    } catch {
      // Silently fail - backend might be down
    } finally {
      setLoading(false);
      refreshInFlight.current = false;
    }
  }, []);

  useEffect(() => {
    refresh();
    if (pollMs > 0) {
      timer.current = setInterval(refresh, pollMs);
      return () => timer.current && clearInterval(timer.current);
    }
  }, [refresh, pollMs]);

  // Filter apps by type that are enabled AND have API key
  const sonarrApps = apps.filter(
    (a) => a.type === "sonarr" && a.isEnabled && a.hasApiKey
  );
  const radarrApps = apps.filter(
    (a) => a.type === "radarr" && a.isEnabled && a.hasApiKey
  );
  const overseerrApps = apps.filter(
    (a) => a.type === "overseerr" && a.isEnabled && a.hasApiKey
  );
  const jellyseerrApps = apps.filter(
    (a) => a.type === "jellyseerr" && a.isEnabled && a.hasApiKey
  );

  // Get default app or first available
  const getDefaultSonarr = () =>
    sonarrApps.find((a) => a.isDefault) || sonarrApps[0] || null;
  const getDefaultRadarr = () =>
    radarrApps.find((a) => a.isDefault) || radarrApps[0] || null;
  const getDefaultOverseerr = () =>
    overseerrApps.find((a) => a.isDefault) || overseerrApps[0] || null;
  const getDefaultJellyseerr = () =>
    jellyseerrApps.find((a) => a.isDefault) || jellyseerrApps[0] || null;

  return {
    apps,
    integrationMode,
    loading,
    refresh,
    sonarrApps,
    radarrApps,
    overseerrApps,
    jellyseerrApps,
    hasSonarr: sonarrApps.length > 0,
    hasRadarr: radarrApps.length > 0,
    hasOverseerr: overseerrApps.length > 0,
    hasJellyseerr: jellyseerrApps.length > 0,
    getDefaultSonarr,
    getDefaultRadarr,
    getDefaultOverseerr,
    getDefaultJellyseerr,
  };
}
