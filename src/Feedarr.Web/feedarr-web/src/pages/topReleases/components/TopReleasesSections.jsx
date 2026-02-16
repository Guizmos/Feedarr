import React, { useMemo } from "react";
import { resolveApiUrl } from "../../../api/client.js";
import PosterCard from "../../../ui/PosterCard.jsx";
import AppIcon from "../../../ui/AppIcon.jsx";
import usePosterRetryOn404 from "../../../hooks/usePosterRetryOn404.js";
import { Image, ImageOff } from "lucide-react";
import {
  fmtSizeGo,
  formatSeasonEpisode,
  getMediaTypeLabel,
  getSizeLabel,
} from "../../../utils/formatters.js";

function getResolutionClass(resolution) {
  const value = String(resolution || "").toLowerCase();
  if (value.includes("2160") || value.includes("4k")) return "banner-pill banner-pill--4k";
  if (value.includes("1080")) return "banner-pill banner-pill--1080";
  if (value.includes("720")) return "banner-pill banner-pill--720";
  return "banner-pill";
}

export function TopReleasesGridSection({
  items,
  sectionTitle,
  showRank = true,
  rankColor = "#fff",
  showIndexerPill = false,
  compact = false,
  isGlobalTop = false,
  top5AnimKey = 0,
  sortBy,
  sourceNameById,
  onDownload,
  onOpen,
}) {
  if (items.length === 0) return null;
  return (
    <section
      key={sectionTitle}
      className={isGlobalTop ? "top5-global-section" : ""}
      style={{ marginBottom: 40 }}
    >
      <h2
        className={isGlobalTop ? "top5-global-title" : ""}
        style={{ marginBottom: 16, fontSize: 18 }}
      >
        {sectionTitle}
      </h2>
      <div
        className={`grid${compact ? " grid--compact" : ""}${isGlobalTop ? " grid--spotlight" : ""}`}
      >
        {items.map((item, idx) => (
          <div
            key={isGlobalTop ? `${item.id}-${top5AnimKey}` : item.id}
            className={isGlobalTop ? "top5-anim-item" : ""}
            style={{
              position: "relative",
              "--top5-delay": `${0.16 + idx * 0.06}s`,
            }}
          >
            {showRank ? (
              <div
                style={{
                  position: "absolute",
                  top: 8,
                  left: 8,
                  background: "rgba(0,0,0,0.8)",
                  color: rankColor,
                  padding: "4px 8px",
                  borderRadius: 4,
                  fontWeight: "bold",
                  fontSize: sectionTitle.includes("Global") ? 14 : 12,
                  zIndex: 10,
                }}
              >
                #{idx + 1}
              </div>
            ) : null}
            <PosterCard
              item={item}
              onDownload={onDownload}
              onOpen={onOpen}
              hideRename={true}
              sortBy={sortBy}
              indexerLabel={sourceNameById.get(Number(item.sourceId)) || ""}
              showIndexerPill={showIndexerPill}
              indexerPillPosition="right"
            />
          </div>
        ))}
      </div>
    </section>
  );
}

export function TopReleasesBannerSection({
  items,
  sectionTitle,
  showRank = true,
  rankColor = "#fff",
  sourceNameById,
  onOpen,
  formatRating,
}) {
  if (items.length === 0) return null;
  return (
    <section key={sectionTitle} style={{ marginBottom: 40 }}>
      <h2 style={{ marginBottom: 16, fontSize: 18 }}>{sectionTitle}</h2>
      <div className="library-banner">
        {items.map((item, idx) => {
          const seasonEpisode = formatSeasonEpisode(item);
          const resolution = item.resolution || "";
          const metaMain = [seasonEpisode, item.year ? String(item.year) : ""].filter(Boolean);
          const metaBadges = [resolution, item.source || "", item.codec || "", item.releaseGroup || ""].filter(Boolean);
          const sizeLabel = getSizeLabel(item);
          const ratingLabel = formatRating(item);
          const metaFoot = [
            sizeLabel ? { label: "Taille", value: sizeLabel } : null,
            item.date ? { label: "Date", value: item.date } : null,
            ratingLabel ? { label: "Note", value: ratingLabel } : null,
            item.seeders != null ? { label: "Seeders", value: String(item.seeders) } : null,
            item.grabs != null ? { label: "Telecharg", value: String(item.grabs) } : null,
          ].filter(Boolean);
          const indexerName = sourceNameById.get(Number(item.sourceId)) || "";
          const mediaTypeLabel = getMediaTypeLabel(item);
          return (
            <div key={item.id} className="banner-row" onClick={() => onOpen(item)} style={{ position: "relative" }}>
              {showRank ? (
                <div
                  style={{
                    position: "absolute",
                    top: 8,
                    left: 8,
                    background: "rgba(0,0,0,0.8)",
                    color: rankColor,
                    padding: "4px 8px",
                    borderRadius: 4,
                    fontWeight: "bold",
                    fontSize: sectionTitle.includes("Global") ? 14 : 12,
                    zIndex: 10,
                  }}
                >
                  #{idx + 1}
                </div>
              ) : null}
              <div className="banner-poster">
                {item.posterUrl ? (
                  <img src={resolveApiUrl(`/api/posters/banner/${item.id}`)} alt={item.title || ""} loading="lazy" />
                ) : (
                  <div className="banner-fallback">??</div>
                )}
              </div>
              <div className="banner-meta">
                <div className="banner-head">
                  <div className="banner-title">{item.titleClean || item.title || "-"}</div>
                  {mediaTypeLabel ? <div className="banner-type">{mediaTypeLabel}</div> : null}
                </div>
                {metaMain.length > 0 ? (
                  <div className="banner-sub">
                    {metaMain.map((entry, i) => (
                      <span key={`${entry}-${i}`} className={entry === seasonEpisode ? "banner-pill banner-pill--episode" : undefined}>
                        {entry}
                      </span>
                    ))}
                  </div>
                ) : null}
                {metaBadges.length > 0 ? (
                  <div className="banner-sub">
                    {metaBadges.map((entry, i) => (
                      <span key={`${entry}-${i}`} className={entry === resolution ? getResolutionClass(resolution) : "banner-pill"}>
                        {entry}
                      </span>
                    ))}
                  </div>
                ) : null}
                {metaFoot.length > 0 || indexerName ? (
                  <div className="banner-bottom">
                    {metaFoot.length > 0 ? (
                      <div className="banner-foot">
                        {metaFoot.map((entry) => (
                          <span key={`${entry.label}-${entry.value}`}>
                            {entry.label}: <strong>{entry.value}</strong>
                          </span>
                        ))}
                      </div>
                    ) : null}
                    {indexerName ? <div className="banner-indexer">{indexerName}</div> : null}
                  </div>
                ) : null}
              </div>
            </div>
          );
        })}
      </div>
    </section>
  );
}

export function TopReleasesListSection({
  items,
  sectionTitle,
  sourceNameById,
  onOpen,
  onRename,
  formatRating,
  isSeriesItem,
}) {
  if (items.length === 0) return null;
  return (
    <section key={sectionTitle} style={{ marginBottom: 40 }}>
      <h2 style={{ marginBottom: 16, fontSize: 18 }}>{sectionTitle}</h2>
      <div className="library-table">
        <div className="library-thead">
          <div className="library-cell" style={{ width: 60 }}>Rank</div>
          <div className="library-cell">Titre</div>
          <div className="library-cell">Catégorie</div>
          <div className="library-cell">Episode</div>
          <div className="library-cell">Qualité</div>
          <div className="library-cell">Codec</div>
          <div className="library-cell">Taille</div>
          <div className="library-cell">Date</div>
          <div className="library-cell">Note</div>
          <div className="library-cell">Seeders</div>
          <div className="library-cell">Téléchargé</div>
          <div className="library-cell">Indexeur</div>
          <div className="library-cell library-cell--edit" />
        </div>
        {items.map((item, idx) => (
          <div key={item.id} className="library-trow" onClick={() => onOpen(item)}>
            <div className="library-cell" style={{ width: 60 }}>#{idx + 1}</div>
            <div className="library-cell">{item.titleClean || item.title || "-"}</div>
            <div className="library-cell">{item.unifiedCategoryLabel || "-"}</div>
            <div className="library-cell">{isSeriesItem(item) ? formatSeasonEpisode(item) || "-" : "-"}</div>
            <div className="library-cell">
              {item.resolution ? (
                <div className="library-quality">
                  <span className={getResolutionClass(item.resolution)}>
                    {item.resolution}
                  </span>
                </div>
              ) : (
                "-"
              )}
            </div>
            <div className="library-cell">
              {item.codec ? (
                <div className="library-quality">
                  <span className="banner-pill">{item.codec}</span>
                </div>
              ) : (
                "-"
              )}
            </div>
            <div className="library-cell">{fmtSizeGo(item.sizeBytes || item.size_bytes || 0) || "-"}</div>
            <div className="library-cell">{item.date || "-"}</div>
            <div className="library-cell">{formatRating(item) || "-"}</div>
            <div className="library-cell">{item.seeders ?? "-"}</div>
            <div className="library-cell">{item.grabs ?? "-"}</div>
            <div className="library-cell">{sourceNameById.get(Number(item.sourceId)) || "-"}</div>
            <div className="library-cell library-cell--edit">
              <button
                type="button"
                className="list-edit"
                title="Renommer"
                onClick={(e) => {
                  e.stopPropagation();
                  onRename(item);
                }}
              >
                <AppIcon name="edit" />
              </button>
            </div>
          </div>
        ))}
      </div>
    </section>
  );
}

function TopReleasesPosterCard({ item, onOpen, showRank, rankColor, rankIdx, sectionTitle }) {
  const displayTitle = useMemo(
    () => (item.titleClean?.trim() ? item.titleClean : item.title),
    [item.titleClean, item.title]
  );
  const basePosterUrl = item.posterUrl || (item.id ? `/api/posters/release/${item.id}` : "");
  const { posterSrc, imgError, handleImageError, handleImageLoad } = usePosterRetryOn404(item.id, basePosterUrl);
  const lastError = String(item?.posterLastError || "").toLowerCase();
  const hadError = !!lastError && lastError !== "pending";
  const hasPoster = !!item?.posterUrl && !!posterSrc && !imgError;
  const isPending = !hasPoster && lastError === "pending";
  const isFailed = !hasPoster && hadError;
  const isEmpty = !hasPoster && !isPending && !isFailed;

  return (
    <div
      className="posterViewCard"
      onClick={() => onOpen?.(item)}
      style={{ cursor: "pointer" }}
      title={displayTitle || ""}
    >
      {hasPoster ? (
        <img src={posterSrc} alt={displayTitle || ""} loading="lazy" onError={handleImageError} onLoad={handleImageLoad} />
      ) : (
        <div className="posterFallback">
          {isPending ? <div className="posterLoader" /> : isFailed ? <ImageOff className="posterFallback__icon" /> : isEmpty ? <Image className="posterFallback__icon" /> : null}
        </div>
      )}
      {showRank ? (
        <div
          style={{
            position: "absolute", top: 8, left: 8,
            background: "rgba(0,0,0,0.8)", color: rankColor,
            padding: "4px 8px", borderRadius: 4, fontWeight: "bold",
            fontSize: sectionTitle?.includes("Global") ? 14 : 12, zIndex: 10,
          }}
        >
          #{rankIdx + 1}
        </div>
      ) : null}
      <div className="posterViewOverlay">
        <div className="posterViewOverlay__title">{displayTitle}</div>
        <div className="posterViewOverlay__meta">
          <span>{item.date || "-"}</span>
          <span className="posterViewOverlay__sep">&middot;</span>
          <span>{item.grabs ?? 0} DL</span>
        </div>
      </div>
    </div>
  );
}

export function TopReleasesPosterSection({
  items,
  sectionTitle,
  showRank = true,
  rankColor = "#fff",
  onOpen,
}) {
  if (items.length === 0) return null;
  return (
    <section key={sectionTitle} style={{ marginBottom: 40 }}>
      <h2 style={{ marginBottom: 16, fontSize: 18 }}>{sectionTitle}</h2>
      <div className="grid grid--poster">
        {items.map((item, idx) => (
          <TopReleasesPosterCard
            key={item.id}
            item={item}
            onOpen={onOpen}
            showRank={showRank}
            rankColor={rankColor}
            rankIdx={idx}
            sectionTitle={sectionTitle}
          />
        ))}
      </div>
    </section>
  );
}
