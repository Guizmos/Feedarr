import { getActiveUiLanguage } from "../app/locale.js";
import { normalizeCategoryGroupKey } from "../domain/categories/index.js";

export function fmtBytes(bytes) {
  const n = Number(bytes);
  if (!Number.isFinite(n) || n <= 0) return "";
  const units = ["B", "KB", "MB", "Go", "To"];
  let v = n;
  let i = 0;
  while (v >= 1024 && i < units.length - 1) {
    v /= 1024;
    i++;
  }
  const digits = i === 0 ? 0 : i === 1 ? 0 : 1;
  return `${v.toFixed(digits)} ${units[i]}`;
}

export function fmtSizeGo(bytes) {
  const n = Number(bytes);
  if (!Number.isFinite(n) || n <= 0) return "";
  const go = n / (1024 * 1024 * 1024);
  const digits = go >= 100 ? 0 : go >= 10 ? 1 : 2;
  return `${go.toFixed(digits)} Go`;
}

export function fmtDateFromTs(tsSeconds) {
  const n = Number(tsSeconds);
  if (!Number.isFinite(n) || n <= 0) return "";
  const d = new Date(n * 1000);
  return d.toLocaleString(getActiveUiLanguage(), { dateStyle: "short", timeStyle: "short" });
}

export function getSizeLabel(it) {
  const direct = String(it?.size || "").trim();
  if (direct) return direct;
  return fmtBytes(it?.sizeBytes || it?.size_bytes || 0);
}

export function pad2(value) {
  return String(value).padStart(2, "0");
}

export function formatSeasonEpisode(it) {
  const season = Number(it?.season);
  const episode = Number(it?.episode);
  if (Number.isFinite(season) && season > 0 && Number.isFinite(episode) && episode > 0) {
    return `S${pad2(season)}E${pad2(episode)}`;
  }
  if (Number.isFinite(season) && season > 0) {
    return `S${pad2(season)}`;
  }
  return "";
}

export function getMediaTypeLabel(it) {
  const raw = String(it?.mediaType || it?.unifiedCategoryKey || "").toLowerCase();
  const canonical = normalizeCategoryGroupKey(raw);
  if (!raw) return it?.unifiedCategoryLabel || "";
  if (canonical === "films") return "Film";
  if (canonical === "series") return "Serie TV";
  if (canonical === "anime") return "Anime";
  if (canonical === "emissions") return "Emission";
  if (canonical === "games") return "Jeu";
  if (canonical === "spectacle") return "Spectacle";
  if (canonical === "audio") return "Audio";
  if (canonical === "books") return "Livre";
  if (canonical === "comics") return "Comic";
  if (canonical === "animation") return "Animation";
  if (["audio", "music"].includes(raw)) return "Audio";
  if (["book", "books", "livre", "livres"].includes(raw)) return "Livre";
  if (["comic", "comics", "bd", "manga"].includes(raw)) return "Comic";
  return it?.unifiedCategoryLabel || raw;
}
