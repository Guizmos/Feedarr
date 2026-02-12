import React, { useEffect, useMemo, useState } from "react";
import { Download, CirclePlus, Loader2 } from "lucide-react";
import { apiPost, resolveApiUrl } from "../api/client.js";
import Modal from "./Modal.jsx";
import { buildIndexerPillStyle } from "../utils/sourceColors.js";
import AppIcon from "./AppIcon.jsx";

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
  arrStatus = null, // { inSonarr, inRadarr, sonarrId, radarrId, sonarrUrl, radarrUrl }
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
  const resolution = item?.resolution || "";
  const pills = useMemo(
    () =>
      [
        mediaTypeLabel,
        seasonEpisode,
        resolution,
        item?.codec,
        item?.releaseGroup,
      ].filter(Boolean),
    [item, mediaTypeLabel, seasonEpisode, resolution]
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

  // Determine which arr button to show
  const showSonarrBtn = hasSonarr && isSonarrItem(item);
  const showRadarrBtn = hasRadarr && isRadarrItem(item);
  const arrType = showSonarrBtn ? "sonarr" : showRadarrBtn ? "radarr" : null;

  // Check if already in arr from status
  const alreadyInSonarr = arrStatus?.inSonarr || arrResult?.status === "exists" || arrResult?.status === "added";
  const alreadyInRadarr = arrStatus?.inRadarr || arrResult?.status === "exists" || arrResult?.status === "added";
  const isAlreadyInArr = (showSonarrBtn && alreadyInSonarr) || (showRadarrBtn && alreadyInRadarr);
  const arrOpenUrl = arrResult?.openUrl || (showSonarrBtn ? arrStatus?.sonarrUrl : arrStatus?.radarrUrl);

  async function handleAddToArr() {
    if (!arrType || !item) return;
    setArrAdding(true);
    setArrError("");
    setArrResult(null);

    try {
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
    } catch (e) {
      setArrError(e?.message || "Erreur lors de l'ajout");
    } finally {
      setArrAdding(false);
    }
  }

  return (
    <Modal open={open} onClose={onClose} title={title} width={840}>
      {!item ? null : (
        <div className="releaseModal">
          <div className="releaseModal__grid">
            <div className="releaseModal__poster">
              {posterSrc ? (
                <img src={posterSrc} alt={title} loading="lazy" />
              ) : (
                <div className="releaseModal__posterFallback">??</div>
              )}
            </div>

            <div className="releaseModal__meta">
              {page === "main" ? (
                <>
                  {(pills.length > 0 || indexer) && (
                    <div className="releaseModal__pills">
                      <div className="releaseModal__pillsLeft">
                        {pills.map((p) => (
                          <span
                            key={p}
                            className={
                              p === seasonEpisode
                                ? "banner-pill banner-pill--episode"
                                : p === resolution
                                ? resolutionClass
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
                    <span className="releaseModal__label">Source</span>
                    <span className="releaseModal__value">{item.source || "-"}</span>
                  </div>
                  <div className="releaseModal__row">
                    <span className="releaseModal__label">Codec</span>
                    <span className="releaseModal__value">{item.codec || "-"}</span>
                  </div>
                  <div className="releaseModal__row">
                    <span className="releaseModal__label">Seeders</span>
                    <span className="releaseModal__value">{item.seeders ?? "-"}</span>
                  </div>
                  <div className="releaseModal__row">
                    <span className="releaseModal__label">Leechers</span>
                    <span className="releaseModal__value">{item.leechers ?? "-"}</span>
                  </div>
                  <div className="releaseModal__row">
                    <span className="releaseModal__label">Taille</span>
                    <span className="releaseModal__value">{item.size || "-"}</span>
                  </div>
                  <div className="releaseModal__row">
                    <span className="releaseModal__label">Publie</span>
                    <span className="releaseModal__value">{item.date || "-"}</span>
                  </div>
                  <div className="releaseModal__row">
                    <span className="releaseModal__label">ID</span>
                    <span className="releaseModal__value">{item.id ?? "-"}</span>
                  </div>
                </div>
              )}

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

                  {/* Sonarr/Radarr button - only show if app is available */}
                  {(showSonarrBtn || showRadarrBtn) && (
                    isAlreadyInArr ? (
                      <div className="releaseModal__arrStatus">
                        {arrOpenUrl ? (
                          <a
                            href={arrOpenUrl}
                            target="_blank"
                            rel="noopener noreferrer"
                            className={`releaseModal__arrBadge releaseModal__arrBadge--${arrType} releaseModal__arrBadge--link`}
                          >
                            <AppIcon name="check_circle" size={16} />
                            {arrType === "sonarr" ? "Sonarr" : "Radarr"}
                          </a>
                        ) : (
                          <span className={`releaseModal__arrBadge releaseModal__arrBadge--${arrType}`}>
                            <AppIcon name="check_circle" size={16} />
                            {arrType === "sonarr" ? "Sonarr" : "Radarr"}
                          </span>
                        )}
                      </div>
                    ) : arrResult?.status === "fallback" ? (
                      <div className="releaseModal__arrStatus">
                        {arrResult.openUrl ? (
                          <a
                            href={arrResult.openUrl}
                            target="_blank"
                            rel="noopener noreferrer"
                            className="releaseModal__arrBadge releaseModal__arrBadge--warn releaseModal__arrBadge--link"
                          >
                            <AppIcon name="warning" size={16} />
                            Non trouvé - Ouvrir {arrResult.appName || (arrType === "sonarr" ? "Sonarr" : "Radarr")}
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
                        className={`btn-soft btn-soft--${arrType}`}
                        type="button"
                        onClick={handleAddToArr}
                        aria-label={arrType === "sonarr" ? "Ajouter à Sonarr" : "Ajouter à Radarr"}
                        disabled={arrAdding}
                      >
                        {arrAdding ? (
                          <>
                            <Loader2 size={15} strokeWidth={2.5} className="releaseModal__arrSpin" />
                            Ajout...
                          </>
                        ) : (
                          <>
                            <CirclePlus size={15} strokeWidth={2.5} />
                            {arrType === "sonarr" ? "Sonarr" : "Radarr"}
                          </>
                        )}
                      </button>
                    )
                  )}

                  {page === "main" ? (
                    <button className="btn-soft" type="button" onClick={() => setPage("details")}>
                      Details
                    </button>
                  ) : (
                    <button className="btn-soft" type="button" onClick={() => setPage("main")}>
                      Retour
                    </button>
                  )}
                </div>
                <button className="btn-soft" type="button" onClick={onClose}>
                  Fermer
                </button>
              </div>
            </div>
          </div>
        </div>
      )}
    </Modal>
  );
}
