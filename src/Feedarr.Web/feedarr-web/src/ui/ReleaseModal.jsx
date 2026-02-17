import React, { useEffect, useMemo, useState } from "react";
import { Download, CirclePlus, Loader2, FileText } from "lucide-react";
import { apiPost, resolveApiUrl } from "../api/client.js";
import Modal from "./Modal.jsx";
import { buildIndexerPillStyle } from "../utils/sourceColors.js";
import AppIcon from "./AppIcon.jsx";
import { getAppLabel, normalizeRequestMode } from "../utils/appTypes.js";

/**
 * Determines if item should go to Sonarr (series/tv/anime/shows)
 */
function isSonarrItem(item) {
  const raw = String(item?.mediaType || item?.unifiedCategoryKey || "").toLowerCase();
  if (["tv", "series", "serie", "tv_series", "series_tv", "seriestv", "anime", "show", "shows", "emission", "emissions"].includes(raw)) {
    return true;
  }
  return String(item?.unifiedCategoryKey || "").toLowerCase() === "series";
}

/**
 * Determines if item should go to Radarr (movies/films/spectacle)
 */
function isRadarrItem(item) {
  const raw = String(item?.mediaType || item?.unifiedCategoryKey || "").toLowerCase();
  if (["movie", "film", "films", "spectacle"].includes(raw)) {
    return true;
  }
  return String(item?.unifiedCategoryKey || "").toLowerCase() === "films";
}

function formatSeasonEpisode(item) {
  const s = Number(item?.season);
  const e = Number(item?.episode);
  if (Number.isFinite(s) && s > 0 && Number.isFinite(e) && e > 0) {
    return `S${String(s).padStart(2, "0")}E${String(e).padStart(2, "0")}`;
  }
  if (Number.isFinite(s) && s > 0) {
    return `S${String(s).padStart(2, "0")}`;
  }
  return "";
}

function detectLanguageLabel(value) {
  const raw = String(value || "").trim();
  if (!raw) return "";
  const upper = raw.toUpperCase();

  // Keep VFF and VFQ/VQ distinct with strict priority.
  if (/(^|[^A-Z0-9])(VFF|TRUEFRENCH|FR[-_. ]?FR)(?=[^A-Z0-9]|$)/.test(upper)) return "VFF";
  if (/(^|[^A-Z0-9])(VFQ)(?=[^A-Z0-9]|$)/.test(upper)) return "VFQ";
  if (/(^|[^A-Z0-9])(VQ|QCF|FR[-_. ]?CA|QUEBEC)(?=[^A-Z0-9]|$)/.test(upper)) return "VQ";
  if (/(^|[^A-Z0-9])(VOSTFR)(?=[^A-Z0-9]|$)/.test(upper)) return "VOSTFR";
  if (/(^|[^A-Z0-9])(VO)(?=[^A-Z0-9]|$)/.test(upper)) return "VO";
  if (/(^|[^A-Z0-9])(VF|FRENCH|FR)(?=[^A-Z0-9]|$)/.test(upper)) return "VF";
  return "";
}

function pushLanguageCandidates(target, value) {
  if (Array.isArray(value)) {
    value.forEach((entry) => pushLanguageCandidates(target, entry));
    return;
  }
  if (value == null) return;
  if (typeof value === "object") {
    pushLanguageCandidates(target, value?.code);
    pushLanguageCandidates(target, value?.name);
    pushLanguageCandidates(target, value?.label);
    pushLanguageCandidates(target, value?.value);
    pushLanguageCandidates(target, value?.lang);
    pushLanguageCandidates(target, value?.language);
    return;
  }
  const text = String(value).trim();
  if (text) target.push(text);
}

function getLanguageLabel(item) {
  if (!item) return "";

  const explicitCandidates = [];
  pushLanguageCandidates(explicitCandidates, item?.language);
  pushLanguageCandidates(explicitCandidates, item?.lang);
  pushLanguageCandidates(explicitCandidates, item?.languages);
  pushLanguageCandidates(explicitCandidates, item?.audioLanguage);
  pushLanguageCandidates(explicitCandidates, item?.audioLanguages);
  pushLanguageCandidates(explicitCandidates, item?.audioLang);

  for (const candidate of explicitCandidates) {
    const label = detectLanguageLabel(candidate);
    if (label) return label;
  }

  const inferredCandidates = [];
  pushLanguageCandidates(inferredCandidates, item?.title);
  pushLanguageCandidates(inferredCandidates, item?.titleClean);
  pushLanguageCandidates(inferredCandidates, item?.releaseGroup);

  for (const candidate of inferredCandidates) {
    const label = detectLanguageLabel(candidate);
    if (label) return label;
  }

  return "";
}

function getLanguagePillClass(languageLabel) {
  const normalized = String(languageLabel || "").toUpperCase();
  if (normalized === "VFF") return "banner-pill banner-pill--lang-vff";
  if (normalized === "VFQ" || normalized === "VQ") return "banner-pill banner-pill--lang-vfq";
  if (normalized === "VOSTFR") return "banner-pill banner-pill--lang-vostfr";
  if (normalized === "VO") return "banner-pill banner-pill--lang-vo";
  if (normalized) return "banner-pill banner-pill--lang";
  return "banner-pill";
}

function getMediaTypeLabel(item) {
  const raw = String(item?.mediaType || item?.unifiedCategoryKey || "").toLowerCase();
  if (!raw) return item?.unifiedCategoryLabel || "";
  if (["movie", "film", "films"].includes(raw)) return "Films";
  if (["tv", "series", "serie", "tv_series", "series_tv", "seriestv"].includes(raw)) return "Serie TV";
  if (["anime"].includes(raw)) return "Anime";
  if (["show", "shows", "emission", "emissions"].includes(raw)) return "Emission";
  if (["game", "games"].includes(raw)) return "Jeu";
  if (["spectacle"].includes(raw)) return "Spectacle";
  return item?.unifiedCategoryLabel || raw;
}

function formatRuntime(minutes) {
  const n = Number(minutes);
  if (!Number.isFinite(n) || n <= 0) return "";
  const h = Math.floor(n / 60);
  const m = Math.round(n % 60);
  if (h <= 0) return `${m}m`;
  return `${h}h${String(m).padStart(2, "0")}`;
}

function getExternalUrl(item) {
  if (!item) return null;
  if (item.igdbUrl) return item.igdbUrl;
  const provider = String(item.detailsProvider || "").toLowerCase();
  if (provider === "igdb" && item.detailsProviderId) return null;
  const tmdb = Number(item.tmdbId);
  if (Number.isFinite(tmdb) && tmdb > 0) {
    const raw = String(item.mediaType || item.unifiedCategoryKey || "").toLowerCase();
    const isTv = ["tv", "series", "serie", "tv_series", "series_tv", "seriestv", "anime", "show", "shows", "emission", "emissions"].includes(raw);
    return isTv
      ? `https://www.themoviedb.org/tv/${tmdb}`
      : `https://www.themoviedb.org/movie/${tmdb}`;
  }
  return null;
}

function isGameItem(item) {
  const key = String(item?.unifiedCategoryKey || "").toLowerCase();
  const mediaType = String(item?.mediaType || "").toLowerCase();
  const provider = String(item?.detailsProvider || "").toLowerCase();
  return key === "games" || mediaType === "game" || provider === "igdb";
}

function normalizeRatingToTen(value, item) {
  const raw = Number(value);
  if (!Number.isFinite(raw) || raw <= 0) return null;
  if (isGameItem(item)) return raw > 10 ? raw / 10 : raw;
  return raw > 10 ? raw / 10 : raw;
}

function formatRating(item) {
  const v = normalizeRatingToTen(item?.rating, item);
  if (!Number.isFinite(v) || v <= 0) return "";
  const count = Number(item?.ratingVotes);
  const suffix = Number.isFinite(count) && count > 0 ? ` (${count})` : "";
  return `${v.toFixed(1)}/10${suffix}`;
}

function normalizeIndexerName(value) {
  return String(value || "").toUpperCase().replace(/[^A-Z0-9]/g, "");
}

function getIndexerClass(value) {
  const key = normalizeIndexerName(value);
  if (key === "YGEGE") return "banner-pill--indexer-ygege";
  if (key === "C411") return "banner-pill--indexer-c411";
  if (key === "TOS") return "banner-pill--indexer-tos";
  if (key === "LACALE") return "banner-pill--indexer-lacale";
  return "";
}

export default function ReleaseModal({
  open,
  item,
  onClose,
  onDownload,
  categoryLabel,
  indexerLabel,
  indexerColor,
  // Arr integration props
  hasSonarr = false,
  hasRadarr = false,
  hasOverseerr = false,
  hasJellyseerr = false,
  hasSeer = false,
  integrationMode = "arr",
  arrStatus = null, // { inSonarr, inRadarr, inOverseerr, inJellyseerr, inSeer, sonarrUrl, radarrUrl, overseerrUrl, jellyseerrUrl, seerUrl }
  onArrStatusChange,
}) {
  const [page, setPage] = useState("main");
  const [arrAdding, setArrAdding] = useState(false);
  const [arrError, setArrError] = useState("");
  const [arrResult, setArrResult] = useState(null); // { status, openUrl, appName }
  const title = item?.titleClean?.trim() || item?.title || "Details";
  const posterSrc = resolveApiUrl(item?.posterUrl || "");

  const seasonEpisode = useMemo(() => formatSeasonEpisode(item), [item]);
  const mediaTypeLabel = useMemo(() => getMediaTypeLabel(item), [item]);
  const languageLabel = useMemo(() => getLanguageLabel(item), [item]);
  const resolution = item?.resolution || "";
  const pills = useMemo(
    () =>
      [
        mediaTypeLabel,
        seasonEpisode,
        languageLabel,
        resolution,
        item?.codec,
        item?.releaseGroup,
      ].filter(Boolean),
    [item, mediaTypeLabel, seasonEpisode, languageLabel, resolution]
  );
  const indexer = String(indexerLabel || "").trim();
  const indexerClass = getIndexerClass(indexer);
  const indexerStyle = useMemo(
    () => buildIndexerPillStyle(indexerColor),
    [indexerColor]
  );
  const overview =
    item?.overview ||
    item?.synopsis ||
    item?.description ||
    item?.plot ||
    item?.summary ||
    "";
  const infoRows = useMemo(
    () =>
      [
        { label: "Sortie", value: item?.releaseDate },
        { label: "Duree", value: formatRuntime(item?.runtimeMinutes) },
        { label: "Note", value: formatRating(item) },
        { label: "Genres", value: item?.genres },
        { label: "Realisateur", value: item?.directors },
        { label: "Scenaristes", value: item?.writers },
        { label: "Cast", value: item?.cast },
      ].filter((row) => row.value),
    [item]
  );
  const externalUrl = useMemo(() => getExternalUrl(item), [item]);
  const resolutionClass = (() => {
    const lower = String(resolution).toLowerCase();
    if (lower.includes("2160") || lower.includes("4k")) return "banner-pill banner-pill--4k";
    if (lower.includes("1080")) return "banner-pill banner-pill--1080";
    if (lower.includes("720")) return "banner-pill banner-pill--720";
    return "banner-pill";
  })();

  useEffect(() => {
    if (!open) return;
    setPage("main");
    setArrAdding(false);
    setArrError("");
    setArrResult(null);
  }, [open, item?.id]);

  const mode = normalizeRequestMode(integrationMode);

  // Determine which action button to show
  const showSonarrBtn = hasSonarr && isSonarrItem(item);
  const showRadarrBtn = hasRadarr && isRadarrItem(item);
  const arrType = mode === "arr" ? (showSonarrBtn ? "sonarr" : showRadarrBtn ? "radarr" : null) : null;

  const requestType = mode === "overseerr" || mode === "jellyseerr" || mode === "seer" ? mode : null;
  const hasRequestApp =
    requestType === "overseerr"
      ? hasOverseerr
      : requestType === "jellyseerr"
        ? hasJellyseerr
        : requestType === "seer"
          ? hasSeer
          : false;
  const requestMediaType = isRadarrItem(item) ? "movie" : isSonarrItem(item) ? "tv" : null;
  const showRequestBtn = !!requestType && !!requestMediaType;

  const actionType = requestType || arrType;
  const actionLabel = actionType ? getAppLabel(actionType) : "";

  // Check if already in destination from status
  const alreadyInSonarr = arrStatus?.inSonarr || arrResult?.status === "exists" || arrResult?.status === "added";
  const alreadyInRadarr = arrStatus?.inRadarr || arrResult?.status === "exists" || arrResult?.status === "added";
  const alreadyInOverseerr = arrStatus?.inOverseerr || arrResult?.status === "exists" || arrResult?.status === "added";
  const alreadyInJellyseerr = arrStatus?.inJellyseerr || arrResult?.status === "exists" || arrResult?.status === "added";
  const alreadyInSeer = arrStatus?.inSeer || arrResult?.status === "exists" || arrResult?.status === "added";
  const isAlreadyInArr = (showSonarrBtn && alreadyInSonarr) || (showRadarrBtn && alreadyInRadarr);
  const isAlreadyRequested =
    (requestType === "overseerr" && alreadyInOverseerr) ||
    (requestType === "jellyseerr" && alreadyInJellyseerr) ||
    (requestType === "seer" && alreadyInSeer);
  const isAlreadyInTarget = mode === "arr" ? isAlreadyInArr : isAlreadyRequested;
  const arrOpenUrl = arrResult?.openUrl || (
    mode === "arr"
      ? (showSonarrBtn ? arrStatus?.sonarrUrl : arrStatus?.radarrUrl)
      : requestType === "overseerr"
        ? arrStatus?.overseerrUrl
        : requestType === "jellyseerr"
          ? arrStatus?.jellyseerrUrl
          : arrStatus?.seerUrl
  );

  async function handleAddToArr() {
    if (!actionType || !item) return;
    setArrAdding(true);
    setArrError("");
    setArrResult(null);

    try {
      if (mode === "arr") {
        const endpoint = arrType === "sonarr" ? "/api/arr/sonarr/add" : "/api/arr/radarr/add";

        // Build payload based on arr type
        // Sonarr requires tvdbId, Radarr requires tmdbId
        const payload = arrType === "sonarr"
          ? {
              tvdbId: item.tvdbId || 0,
              title: item.titleClean || item.title,
            }
          : {
              tmdbId: item.tmdbId || 0,
              title: item.titleClean || item.title,
              year: item.year || null,
            };

        // Check if we have the required ID
        if (arrType === "sonarr" && !item.tvdbId) {
          setArrError("ID TVDB manquant pour cet élément");
          setArrAdding(false);
          return;
        }
        if (arrType === "radarr" && !item.tmdbId) {
          setArrError("ID TMDB manquant pour cet élément");
          setArrAdding(false);
          return;
        }

        const res = await apiPost(endpoint, payload);

        if (res?.ok) {
          setArrResult({
            status: res.status, // "added" or "exists"
            openUrl: res.openUrl,
            appName: res.appName,
          });
          // Notify parent to update status
          onArrStatusChange?.(item.id, arrType, {
            inSonarr: arrType === "sonarr",
            inRadarr: arrType === "radarr",
            sonarrUrl: arrType === "sonarr" ? res.openUrl : arrStatus?.sonarrUrl,
            radarrUrl: arrType === "radarr" ? res.openUrl : arrStatus?.radarrUrl,
          });
        } else {
          setArrError(res?.message || "Erreur lors de l'ajout");
        }
      } else {
        if (!requestType || !requestMediaType) return;
        if (!hasRequestApp) {
          setArrError(`${getAppLabel(requestType)} non configuré`);
          setArrAdding(false);
          return;
        }
        if (requestMediaType === "movie" && !item.tmdbId) {
          setArrError("ID TMDB manquant pour créer une demande");
          setArrAdding(false);
          return;
        }

        const res = await apiPost("/api/arr/request/add", {
          appType: requestType,
          releaseId: item.id || null,
          tmdbId: item.tmdbId || null,
          tvdbId: item.tvdbId || null,
          mediaType: requestMediaType,
          title: item.titleClean || item.title,
          year: item.year || null,
        });

        if (res?.ok) {
          setArrResult({
            status: res.status,
            openUrl: res.openUrl,
            appName: res.appName || getAppLabel(requestType),
          });

          onArrStatusChange?.(item.id, requestType, {
            inOverseerr: requestType === "overseerr",
            inJellyseerr: requestType === "jellyseerr",
            inSeer: requestType === "seer",
            overseerrUrl: requestType === "overseerr" ? res.openUrl : arrStatus?.overseerrUrl,
            jellyseerrUrl: requestType === "jellyseerr" ? res.openUrl : arrStatus?.jellyseerrUrl,
            seerUrl: requestType === "seer" ? res.openUrl : arrStatus?.seerUrl,
          });
        } else {
          setArrError(res?.message || "Erreur lors de la demande");
        }
      }
    } catch (e) {
      setArrError(e?.message || "Erreur lors de l'action");
    } finally {
      setArrAdding(false);
    }
  }

  return (
    <Modal open={open} onClose={onClose} title={title} width={920} modalClassName="modal--details">
      {!item ? null : (
        <div className="releaseModal">
          <div className="releaseModal__grid">
            <div className="releaseModal__poster">
              {posterSrc ? (
                <img src={posterSrc} alt={title} loading="lazy" />
              ) : (
                <div className="releaseModal__posterFallback">??</div>
              )}
              {externalUrl && (
                <a
                  href={externalUrl}
                  target="_blank"
                  rel="noopener noreferrer"
                  className="releaseModal__externalLink"
                  title="Voir sur TMDB / IGDB"
                >
                  i
                </a>
              )}
            </div>

            <div className="releaseModal__meta">
              <div className="releaseModal__content">
                {page === "main" ? (
                  <>
                    {(pills.length > 0 || indexer) && (
                      <div className="releaseModal__pills">
                        <div className="releaseModal__pillsLeft">
                          {pills.map((p, idx) => (
                            <span
                              key={`${p}-${idx}`}
                              className={
                                p === seasonEpisode
                                  ? "banner-pill banner-pill--episode"
                                  : p === resolution
                                    ? resolutionClass
                                    : p === languageLabel
                                      ? getLanguagePillClass(languageLabel)
                                      : "banner-pill"
                              }
                            >
                              {p}
                            </span>
                          ))}
                        </div>
                        {indexer ? (
                          <div className="releaseModal__pillsRight">
                            <span
                              className={`banner-pill banner-pill--indexer${indexerClass ? ` ${indexerClass}` : ""}`}
                              style={indexerStyle || undefined}
                            >
                              {indexer}
                            </span>
                          </div>
                        ) : null}
                      </div>
                    )}

                    <div className="releaseModal__overview">
                      {infoRows.length > 0 && (
                        <div className="releaseModal__info">
                          {infoRows.map((row) => (
                            <div key={row.label} className="releaseModal__infoRow">
                              <span className="releaseModal__infoLabel">{row.label}</span>
                              <span className="releaseModal__infoValue">{row.value}</span>
                            </div>
                          ))}
                        </div>
                      )}
                      <div className="releaseModal__overviewLabel">Synopsis</div>
                      <div className="releaseModal__overviewText">
                        {overview || "Aucune description disponible."}
                      </div>
                    </div>
                  </>
                ) : (
                  <div className="releaseModal__rows">
                    <div className="releaseModal__row">
                      <span className="releaseModal__label">Titre clean</span>
                      <span className="releaseModal__value">{item.titleClean || "-"}</span>
                    </div>
                    <div className="releaseModal__row">
                      <span className="releaseModal__label">Annee</span>
                      <span className="releaseModal__value">{item.year || "-"}</span>
                    </div>
                    <div className="releaseModal__row">
                      <span className="releaseModal__label">Saison / Episode</span>
                      <span className="releaseModal__value">{seasonEpisode || "-"}</span>
                    </div>
                    <div className="releaseModal__row">
                      <span className="releaseModal__label">Groupe</span>
                      <span className="releaseModal__value">{item.releaseGroup || "-"}</span>
                    </div>
                    <div className="releaseModal__row">
                      <span className="releaseModal__label">Categorie</span>
                      <span className="releaseModal__value">{categoryLabel || item.categoryId || "-"}</span>
                    </div>
                    <div className="releaseModal__row">
                      <span className="releaseModal__label">Media type</span>
                      <span className="releaseModal__value">{item.mediaType || "-"}</span>
                    </div>
                    <div className="releaseModal__row">
                      <span className="releaseModal__label">Qualite</span>
                      <span className="releaseModal__value">{item.resolution || "-"}</span>
                    </div>
                    <div className="releaseModal__row">
                      <span className="releaseModal__label">Langue</span>
                      <span className="releaseModal__value">{languageLabel || "-"}</span>
                    </div>
                    <div className="releaseModal__row">
                      <span className="releaseModal__label">Source</span>
                      <span className="releaseModal__value">{item.source || "-"}</span>
                    </div>
                    <div className="releaseModal__row">
                      <span className="releaseModal__label">Codec</span>
                      <span className="releaseModal__value">{item.codec || "-"}</span>
                    </div>
                    <div className="releaseModal__row">
                      <span className="releaseModal__label">ID</span>
                      <span className="releaseModal__value">{item.id ?? "-"}</span>
                    </div>
                  </div>
                )}
              </div>

              <div className="releaseModal__bottom">
                <div className="releaseModal__footer">
                  <div className="releaseModal__footerItem">
                    <span className="releaseModal__footerLabel">Seeders</span>
                    <span className="releaseModal__footerValue">{item.seeders ?? "-"}</span>
                    <span className="releaseModal__footerSep">•</span>
                    <span className="releaseModal__footerLabel">Leechers</span>
                    <span className="releaseModal__footerValue">{item.leechers ?? "-"}</span>
                  </div>
                  <div className="releaseModal__footerItem releaseModal__footerItem--center">
                    <span className="releaseModal__footerLabel">Taille</span>
                    <span className="releaseModal__footerValue">{item.size || "-"}</span>
                  </div>
                  <div className="releaseModal__footerItem releaseModal__footerItem--right">
                    <span className="releaseModal__footerLabel">Publie</span>
                    <span className="releaseModal__footerValue">{item.date || "-"}</span>
                  </div>
                </div>

                {/* Arr status/error message */}
                {arrError && (
                  <div className="releaseModal__arrError">{arrError}</div>
                )}

                <div className="releaseModal__actions">
                  <div className="releaseModal__actionsLeft">
                    <button
                      className="btn-icon btn-icon--accent"
                      type="button"
                      onClick={() => onDownload?.(item)}
                      aria-label="Télécharger cette release"
                      title="Download"
                    >
                      <Download size={18} strokeWidth={2.5} />
                    </button>
                    {page === "main" ? (
                      <button
                        className="btn-icon btn-icon--details"
                        type="button"
                        onClick={() => setPage("details")}
                        aria-label="Afficher les détails"
                        title="Détails"
                      >
                        <FileText size={18} strokeWidth={2.5} />
                      </button>
                    ) : null}

                    {/* Integration action button */}
                    {(showSonarrBtn || showRadarrBtn || showRequestBtn) && (
                      isAlreadyInTarget ? (
                        <div className="releaseModal__arrStatus">
                          {arrOpenUrl ? (
                            <a
                              href={arrOpenUrl}
                              target="_blank"
                              rel="noopener noreferrer"
                              className={`releaseModal__arrBadge releaseModal__arrBadge--${actionType} releaseModal__arrBadge--link`}
                            >
                              <AppIcon name="check_circle" size={16} />
                              {actionLabel}
                            </a>
                          ) : (
                            <span className={`releaseModal__arrBadge releaseModal__arrBadge--${actionType}`}>
                              <AppIcon name="check_circle" size={16} />
                              {actionLabel}
                            </span>
                          )}
                        </div>
                      ) : mode === "arr" && arrResult?.status === "fallback" ? (
                        <div className="releaseModal__arrStatus">
                          {arrResult.openUrl ? (
                            <a
                              href={arrResult.openUrl}
                              target="_blank"
                              rel="noopener noreferrer"
                              className="releaseModal__arrBadge releaseModal__arrBadge--warn releaseModal__arrBadge--link"
                            >
                              <AppIcon name="warning" size={16} />
                              Non trouvé - Ouvrir {arrResult.appName || actionLabel}
                            </a>
                          ) : (
                            <span className="releaseModal__arrBadge releaseModal__arrBadge--warn">
                              <AppIcon name="warning" size={16} />
                              Non trouvé
                            </span>
                          )}
                        </div>
                      ) : (
                        <button
                          className={`btn-soft btn-soft--${actionType}`}
                          type="button"
                          onClick={handleAddToArr}
                          aria-label={mode === "arr" ? `Ajouter à ${actionLabel}` : `Ajouter à ${actionLabel}`}
                          disabled={arrAdding || (mode !== "arr" && !hasRequestApp)}
                        >
                          {arrAdding ? (
                            <>
                              <Loader2 size={15} strokeWidth={2.5} className="releaseModal__arrSpin" />
                              {mode === "arr" ? "Ajout..." : "Demande..."}
                            </>
                          ) : (
                            <>
                              <CirclePlus size={15} strokeWidth={2.5} />
                              {mode === "arr" ? `Ajouter à ${actionLabel}` : `Ajouter à ${actionLabel}`}
                            </>
                          )}
                        </button>
                      )
                    )}

                  </div>
                  {page !== "main" ? (
                    <button className="btn-soft" type="button" onClick={() => setPage("main")}>
                      Retour
                    </button>
                  ) : (
                    <button className="btn-soft" type="button" onClick={onClose}>
                      Fermer
                    </button>
                  )}
                </div>
              </div>
            </div>
          </div>
        </div>
      )}
    </Modal>
  );
}
