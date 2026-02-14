export function normalizeAppType(type) {
  return String(type || "").trim().toLowerCase();
}

export function getAppLabel(type) {
  const normalized = normalizeAppType(type);
  if (normalized === "sonarr") return "Sonarr";
  if (normalized === "radarr") return "Radarr";
  if (normalized === "overseerr") return "Overseerr";
  if (normalized === "jellyseerr") return "Jellyseerr";
  return normalized || "Application";
}

export function isArrLibraryType(type) {
  const normalized = normalizeAppType(type);
  return normalized === "sonarr" || normalized === "radarr";
}

export function isRequestAppType(type) {
  const normalized = normalizeAppType(type);
  return normalized === "overseerr" || normalized === "jellyseerr";
}

export function normalizeRequestMode(mode) {
  const normalized = String(mode || "arr").trim().toLowerCase();
  return ["arr", "overseerr", "jellyseerr"].includes(normalized) ? normalized : "arr";
}

export function getRequestModeLabel(mode) {
  const normalized = normalizeRequestMode(mode);
  if (normalized === "overseerr") return "Via Overseerr";
  if (normalized === "jellyseerr") return "Via Jellyseerr";
  return "Direct Sonarr/Radarr";
}
