import React from "react";
import { resolveApiUrl } from "../../../api/client.js";
import { formatSeasonEpisode, getSizeLabel, getMediaTypeLabel } from "../utils/formatters.js";
import { getIndexerClass } from "../utils/helpers.js";
import { buildIndexerPillStyle } from "../../../utils/sourceColors.js";

/**
 * Vue banner de la bibliothèque
 */
export default function LibraryBanner({
  items,
  selectionMode,
  selectedIds,
  onToggleSelect,
  onOpen,
  sourceNameById,
  sourceColorById,
}) {
  return (
    <div className="library-banner">
      {items.map((it) => {
        const seasonEpisode = formatSeasonEpisode(it);
        const resolution = it.resolution || "";
        const metaMain = [seasonEpisode, it.year ? String(it.year) : ""].filter(Boolean);
        const metaBadges = [
          resolution,
          it.source || "",
          it.codec || "",
          it.releaseGroup || "",
        ].filter(Boolean);

        const resolutionClass = resolution
          ? `banner-pill banner-pill--${
              String(resolution).toLowerCase().includes("2160") ||
              String(resolution).toLowerCase().includes("4k")
                ? "4k"
                : String(resolution).toLowerCase().includes("1080")
                  ? "1080"
                  : String(resolution).toLowerCase().includes("720")
                    ? "720"
                    : "default"
            }`
          : "banner-pill";

        const sizeLabel = getSizeLabel(it);
        const metaFoot = [
          sizeLabel ? { label: "Taille", value: sizeLabel } : null,
          it.date ? { label: "Date", value: it.date } : null,
          it.seeders != null ? { label: "Seeders", value: String(it.seeders) } : null,
          it.grabs != null ? { label: "Telecharg", value: String(it.grabs) } : null,
        ].filter(Boolean);

        const indexerName = sourceNameById.get(Number(it.sourceId)) || "";
        const indexerClass = getIndexerClass(indexerName);
        const indexerStyle = buildIndexerPillStyle(sourceColorById?.get(Number(it.sourceId)) || null);
        const overview = String(
          it?.overview || it?.synopsis || it?.description || it?.plot || it?.summary || ""
        ).trim();
        const mediaTypeLabel = getMediaTypeLabel(it);

        return (
          <div
            key={it.id}
            className={`banner-row${selectedIds.has(it.id) ? " is-selected" : ""}`}
            onClick={() => {
              if (selectionMode) onToggleSelect(it);
              else onOpen(it);
            }}
          >
            <div className="banner-poster">
              {it.posterUrl ? (
                <img
                  src={resolveApiUrl(`/api/posters/banner/${it.id}`)}
                  alt={it.title || ""}
                  loading="lazy"
                />
              ) : (
                <div className="banner-fallback">??</div>
              )}
            </div>
            <div className="banner-meta">
              <div className="banner-head">
                <div className="banner-title">{it.titleClean || it.title || "-"}</div>
                {mediaTypeLabel ? <div className="banner-type">{mediaTypeLabel}</div> : null}
              </div>
              {metaMain.length > 0 || metaBadges.length > 0 || overview ? (
                <div className={`banner-content${overview ? " has-overview" : ""}`}>
                  <div className="banner-details">
                    {metaMain.length > 0 ? (
                      <div className="banner-sub">
                        {metaMain.map((entry, idx) => (
                          <span
                            key={`${entry}-${idx}`}
                            className={entry === seasonEpisode ? "banner-pill banner-pill--episode" : undefined}
                          >
                            {entry}
                          </span>
                        ))}
                      </div>
                    ) : null}
                    {metaBadges.length > 0 ? (
                      <div className="banner-sub">
                        {metaBadges.map((entry, idx) => (
                          <span
                            key={`${entry}-${idx}`}
                            className={entry === resolution ? resolutionClass : "banner-pill"}
                          >
                            {entry}
                          </span>
                        ))}
                      </div>
                    ) : null}
                  </div>
                  {overview ? (
                    <div className="banner-overview" title={overview}>
                      <div className="banner-overview__title">Synopsis</div>
                      <div className="banner-overview__text">{overview}</div>
                    </div>
                  ) : null}
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
                  {indexerName ? (
                    <div className="banner-indexer">
                      <span
                        className={`banner-pill banner-pill--indexer${indexerClass ? ` ${indexerClass}` : ""}`}
                        style={indexerStyle || undefined}
                      >
                        {indexerName}
                      </span>
                    </div>
                  ) : null}
                </div>
              ) : null}
            </div>
          </div>
        );
      })}
    </div>
  );
}
