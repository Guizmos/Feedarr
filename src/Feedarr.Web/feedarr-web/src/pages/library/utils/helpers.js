import { normalizeCategoryGroupKey } from "../../../domain/categories/index.js";

/**
 * Fonctions utilitaires pour la logique métier de la bibliothèque
 */

export function isSeriesItem(it) {
  const canonical = normalizeCategoryGroupKey(it?.mediaType || it?.unifiedCategoryKey);
  return canonical === "series" || canonical === "anime" || canonical === "emissions";
}

export function scoreResolution(value) {
  const raw = String(value || "").toLowerCase();
  if (!raw) return 0;
  if (raw.includes("2160") || raw.includes("4k")) return 4;
  if (raw.includes("1080")) return 3;
  if (raw.includes("720")) return 2;
  if (raw.includes("480")) return 1;
  return 0;
}

export function getEpisodeSortValue(it) {
  const season = Number(it?.season);
  const episode = Number(it?.episode);
  const s = Number.isFinite(season) && season > 0 ? season : 0;
  const e = Number.isFinite(episode) && episode > 0 ? episode : 0;
  return s * 1000 + e;
}

export function inferPosterSizeFromUrl(url) {
  if (!url) return "";
  const raw = String(url);
  const tmdbMatch = raw.match(/\/t\/p\/([^/]+)/i);
  if (tmdbMatch?.[1]) return tmdbMatch[1];
  const lower = raw.toLowerCase();
  if (lower.includes("cover_big")) return "cover_big";
  if (lower.includes("t_cover")) return "cover";
  return "";
}

export function scorePosterSize(size) {
  const raw = String(size || "").toLowerCase().trim();
  if (!raw) return 0;
  if (raw === "original") return 100000;
  const tmdb = raw.match(/^w(\d+)$/);
  if (tmdb?.[1]) return Number(tmdb[1]);
  const dims = raw.match(/^(\d+)\s*x\s*(\d+)$/);
  if (dims) return Number(dims[1]) * Number(dims[2]);
  if (raw.includes("cover_big")) return 350;
  if (raw.includes("cover")) return 250;
  return 0;
}

export function getPosterSizeLabel(result) {
  return result?.posterSize || inferPosterSizeFromUrl(result?.posterUrl);
}

export function sortManualResultsBySize(results) {
  const sorted = [...results];
  sorted.sort((a, b) => {
    const scoreA = scorePosterSize(getPosterSizeLabel(a));
    const scoreB = scorePosterSize(getPosterSizeLabel(b));
    if (scoreA !== scoreB) return scoreB - scoreA;
    return String(a?.title || "").localeCompare(String(b?.title || ""));
  });
  return sorted;
}

export function isGameCategoryKey(key) {
  return String(key || "").toLowerCase() === "games";
}

export function isGameMediaType(mediaType) {
  return String(mediaType || "").toLowerCase() === "game";
}

export function normalizeTitleKey(value) {
  return String(value || "").trim().toLowerCase();
}

export function buildPosterUrl(releaseId, posterUpdatedAtTs) {
  if (!releaseId) return "";
  const ts = Number(posterUpdatedAtTs || 0);
  return ts > 0 ? `/api/posters/release/${releaseId}?v=${ts}` : `/api/posters/release/${releaseId}`;
}

export function mergePosterState(base, update) {
  const next = { ...base };
  if (update.entityId != null) next.entityId = update.entityId;
  if (update.posterFile !== undefined) next.posterFile = update.posterFile;
  if (update.posterUpdatedAtTs !== undefined) next.posterUpdatedAtTs = update.posterUpdatedAtTs;
  if (update.posterLastAttemptTs !== undefined) next.posterLastAttemptTs = update.posterLastAttemptTs;
  if (update.posterLastError !== undefined) next.posterLastError = update.posterLastError;
  if (update.posterUrl !== undefined) next.posterUrl = update.posterUrl;
  return next;
}

export function normalizeIndexerName(value) {
  return String(value || "").toUpperCase().replace(/[^A-Z0-9]/g, "");
}

export function getIndexerClass(value) {
  const key = normalizeIndexerName(value);
  if (key === "YGEGE") return "banner-pill--indexer-ygege";
  if (key === "C411") return "banner-pill--indexer-c411";
  if (key === "TOS") return "banner-pill--indexer-tos";
  if (key === "LACALE") return "banner-pill--indexer-lacale";
  return "";
}

export function hasDetailsPayload(it) {
  return Boolean(
    it?.overview ||
    it?.releaseDate ||
    it?.rating ||
    it?.ratingVotes ||
    it?.genres
  );
}

export function getResolutionClass(resolution) {
  const raw = String(resolution || "").toLowerCase();
  if (raw.includes("2160") || raw.includes("4k")) return "banner-pill--4k";
  if (raw.includes("1080")) return "banner-pill--1080";
  if (raw.includes("720")) return "banner-pill--720";
  return "";
}
