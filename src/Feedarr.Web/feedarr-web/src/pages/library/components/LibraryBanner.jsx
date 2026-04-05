import React, { memo, useLayoutEffect, useMemo, useRef, useState } from "react";
import { useVirtualizer } from "@tanstack/react-virtual";
import { resolveApiUrl } from "../../../api/client.js";
import { formatSeasonEpisode, getSizeLabel, getMediaTypeLabel } from "../utils/formatters.js";
import { getIndexerClass } from "../utils/helpers.js";
import { buildIndexerPillStyle } from "../../../utils/sourceColors.js";
import { useScrollContainer } from "../../../context/ScrollContainerContext.js";
import { useContainerWidth } from "../../../hooks/useContainerWidth.js";

/** Desktop: 135px poster + 16px padding + 2px border + 8px row spacing. */
const BANNER_ITEM_SIZE_DESKTOP = 161;
/** Tablet/mobile: compact row dimensions (@media max-width: 768px). */
const BANNER_ITEM_SIZE_TABLET = 137;
/** Small phones: extra compact row dimensions (@media max-width: 480px). */
const BANNER_ITEM_SIZE_MOBILE = 92;

/**
 * Vue banner de la bibliothèque (virtualisée)
 *
 * Uses TanStack Virtual with a flat item-per-row strategy.
 * Fixed estimateSize (no measureElement) to keep getTotalSize() stable
 * during scrollbar drag — same reasoning as the list view fix.
 *
 * Scroll container: the .content div in Shell, via ScrollContainerContext.
 */
function LibraryBanner({
  items,
  selectionMode,
  selectedIds,
  onToggleSelect,
  onOpen,
  sourceNameById,
  sourceColorById,
}) {
  const scrollRef = useScrollContainer();
  const containerRef = useRef(null);
  const containerWidth = useContainerWidth(containerRef);
  const [scrollMargin, setScrollMargin] = useState(0);
  const itemSize = useMemo(() => {
    if (containerWidth > 0 && containerWidth <= 480) return BANNER_ITEM_SIZE_MOBILE;
    if (containerWidth > 0 && containerWidth <= 768) return BANNER_ITEM_SIZE_TABLET;
    return BANNER_ITEM_SIZE_DESKTOP;
  }, [containerWidth]);

  // Distance from the scroll container's top to the virtual container's top.
  useLayoutEffect(() => {
    const scrollEl = scrollRef?.current;
    if (!containerRef.current || !scrollEl) return;
    const containerRect = containerRef.current.getBoundingClientRect();
    const scrollRect = scrollEl.getBoundingClientRect();
    setScrollMargin(containerRect.top - scrollRect.top + scrollEl.scrollTop);
  }, [scrollRef, items.length]);

  const virtualizer = useVirtualizer({
    count: items.length,
    getScrollElement: () => scrollRef?.current ?? null,
    estimateSize: () => itemSize,
    overscan: 5,
    scrollMargin,
  });

  const totalHeight = Math.max(0, virtualizer.getTotalSize() - scrollMargin);

  return (
    <div
      ref={containerRef}
      className="library-banner"
      style={{ height: `${totalHeight}px`, position: "relative", gap: 0 }}
    >
      {virtualizer.getVirtualItems().map((virtualItem) => {
        const it = items[virtualItem.index];
        if (!it) return null;

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
            key={virtualItem.key}
            style={{
              position: "absolute",
              top: 0,
              left: 0,
              width: "100%",
              transform: `translateY(${virtualItem.start - scrollMargin}px)`,
            }}
          >
            <div
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
          </div>
        );
      })}
    </div>
  );
}

export default memo(LibraryBanner);
