export function normalizeAppType(type) {
  return String(type || "").trim().toLowerCase();
}

export function getAppLabel(type) {
  const normalized = normalizeAppType(type);
  if (normalized === "sonarr") return "Sonarr";
  if (normalized === "radarr") return "Radarr";
  if (normalized === "overseerr") return "Overseerr";
  if (normalized === "jellyseerr") return "Jellyseerr";
  if (normalized === "seer") return "Seer";
  return normalized || "Application";
}

export function getAppBaseUrlPlaceholder(type) {
  const normalized = normalizeAppType(type);
  if (normalized === "sonarr") return "http://192.168.1.x:8989 ou https://sonarr.domain.com";
  if (normalized === "radarr") return "http://192.168.1.x:7878 ou https://radarr.domain.com";
  if (normalized === "jellyseerr") return "http://192.168.1.x:5055 ou https://jellyseerr.domain.com";
  if (normalized === "seer") return "http://192.168.1.x:5055 ou https://seer.domain.com";
  return "http://192.168.1.x:5055 ou https://overseerr.domain.com";
}

export function isArrLibraryType(type) {
  const normalized = normalizeAppType(type);
  return normalized === "sonarr" || normalized === "radarr";
}

export function isRequestAppType(type) {
  const normalized = normalizeAppType(type);
  return normalized === "overseerr" || normalized === "jellyseerr" || normalized === "seer";
}

export function normalizeRequestMode(mode) {
  const normalized = String(mode || "arr").trim().toLowerCase();
  return ["arr", "overseerr", "jellyseerr", "seer"].includes(normalized) ? normalized : "arr";
}

export function getRequestModeLabel(mode) {
  const normalized = normalizeRequestMode(mode);
  if (normalized === "overseerr") return "Via Overseerr";
  if (normalized === "jellyseerr") return "Via Jellyseerr";
  if (normalized === "seer") return "Via Seer";
  return "Direct Sonarr/Radarr";
}
