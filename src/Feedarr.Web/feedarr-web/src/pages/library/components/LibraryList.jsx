import React, { memo, useLayoutEffect, useRef, useState } from "react";
import { useVirtualizer } from "@tanstack/react-virtual";
import { fmtSizeGo, formatSeasonEpisode } from "../utils/formatters.js";
import { isSeriesItem, getIndexerClass, getResolutionClass } from "../utils/helpers.js";
import AppIcon from "../../../ui/AppIcon.jsx";
import { buildIndexerPillStyle } from "../../../utils/sourceColors.js";
import { useScrollContainer } from "../../../context/ScrollContainerContext.js";

/** Fixed row height. Rows are uniform so measureElement is intentionally
 *  disabled — dynamic remeasuring during scrollbar drag shifts getTotalSize()
 *  mid-gesture and causes the cursor to decouple from the scrollbar thumb. */
const ESTIMATED_ROW_HEIGHT = 39;

/** Number of off-screen rows rendered above and below the visible window.
 *  Higher value reduces white-flash during fast scrollbar drag at the cost of
 *  slightly more DOM nodes. 15 covers ~570px of buffer at the average row height. */
const OVERSCAN = 15;

/**
 * Vue liste de la bibliothèque (tableau virtualisé)
 *
 * Uses TanStack Virtual to render only the visible rows. The header stays in
 * the normal document flow; only the tbody region is virtualised.
 *
 * Scroll container: the `.content` div in Shell, exposed via ScrollContainerContext.
 */
function LibraryList({
  items,
  selectionMode,
  selectedIds,
  onToggleSelect,
  onOpen,
  onRename,
  listSortBy,
  listSortDir,
  onToggleSort,
  sourceNameById,
  sourceColorById,
  getUnifiedLabel,
}) {
  const scrollRef = useScrollContainer();
  const tbodyRef = useRef(null);

  // Distance from the top of the scroll container to the top of the virtual
  // tbody. Required so the virtualizer knows which items fall inside the
  // visible viewport. Measured once after mount (layout is stable).
  const [scrollMargin, setScrollMargin] = useState(0);

  useLayoutEffect(() => {
    const scrollEl = scrollRef?.current;
    if (!tbodyRef.current || !scrollEl) return;
    const tbodyRect = tbodyRef.current.getBoundingClientRect();
    const scrollRect = scrollEl.getBoundingClientRect();
    // Add scrollEl.scrollTop so the offset is absolute within the scroll container,
    // not relative to the current viewport position.
    setScrollMargin(tbodyRect.top - scrollRect.top + scrollEl.scrollTop);
  }, [scrollRef, items.length]);

  const virtualizer = useVirtualizer({
    count: items.length,
    getScrollElement: () => scrollRef?.current ?? null,
    estimateSize: () => ESTIMATED_ROW_HEIGHT,
    overscan: OVERSCAN,
    scrollMargin,
  });

  const sortClass = (key) =>
    `library-sort ${listSortBy === key ? "is-active" : ""}${listSortBy === key && listSortDir === "desc" ? " is-desc" : ""}`;

  return (
    <div className="library-table">
      {/* ── Header – stays in normal document flow, never virtualised ── */}
      <div className="library-thead">
        <div className="library-cell library-cell--select" />
        <button type="button" className={sortClass("title")} onClick={() => onToggleSort("title")}>
          <span className="library-sort__label">Titre</span>
          <AppIcon
            name={listSortDir === "desc" ? "arrow_downward" : "arrow_upward"}
            className="library-sort__icon"
          />
        </button>
        <button type="button" className={sortClass("category")} onClick={() => onToggleSort("category")}>
          <span className="library-sort__label">Catégorie</span>
          <AppIcon
            name={listSortDir === "desc" ? "arrow_downward" : "arrow_upward"}
            className="library-sort__icon"
          />
        </button>
        <button type="button" className={sortClass("episode")} onClick={() => onToggleSort("episode")}>
          <span className="library-sort__label">Episode</span>
          <AppIcon
            name={listSortDir === "desc" ? "arrow_downward" : "arrow_upward"}
            className="library-sort__icon"
          />
        </button>
        <button type="button" className={sortClass("quality")} onClick={() => onToggleSort("quality")}>
          <span className="library-sort__label">Qualité</span>
          <AppIcon
            name={listSortDir === "desc" ? "arrow_downward" : "arrow_upward"}
            className="library-sort__icon"
          />
        </button>
        <button type="button" className={sortClass("codec")} onClick={() => onToggleSort("codec")}>
          <span className="library-sort__label">Codec</span>
          <AppIcon
            name={listSortDir === "desc" ? "arrow_downward" : "arrow_upward"}
            className="library-sort__icon"
          />
        </button>
        <button type="button" className={sortClass("size")} onClick={() => onToggleSort("size")}>
          <span className="library-sort__label">Taille</span>
          <AppIcon
            name={listSortDir === "desc" ? "arrow_downward" : "arrow_upward"}
            className="library-sort__icon"
          />
        </button>
        <button type="button" className={sortClass("date")} onClick={() => onToggleSort("date")}>
          <span className="library-sort__label">Date d'ajout</span>
          <AppIcon
            name={listSortDir === "desc" ? "arrow_downward" : "arrow_upward"}
            className="library-sort__icon"
          />
        </button>
        <button type="button" className={sortClass("seeders")} onClick={() => onToggleSort("seeders")}>
          <span className="library-sort__label">Seeders</span>
          <AppIcon
            name={listSortDir === "desc" ? "arrow_downward" : "arrow_upward"}
            className="library-sort__icon"
          />
        </button>
        <button type="button" className={sortClass("downloads")} onClick={() => onToggleSort("downloads")}>
          <span className="library-sort__label">Téléchargé</span>
          <AppIcon
            name={listSortDir === "desc" ? "arrow_downward" : "arrow_upward"}
            className="library-sort__icon"
          />
        </button>
        <button type="button" className={sortClass("source")} onClick={() => onToggleSort("source")}>
          <span className="library-sort__label">Indexeur</span>
          <AppIcon
            name={listSortDir === "desc" ? "arrow_downward" : "arrow_upward"}
            className="library-sort__icon"
          />
        </button>
        <div className="library-cell library-cell--edit" />
      </div>

      {/* ── Virtual tbody ──
          Height = total virtual height so the scroll container shows a correct
          scrollbar. Rows are absolutely positioned inside via translateY. */}
      <div
        ref={tbodyRef}
        className="library-tbody-virtual"
        style={{ height: `${virtualizer.getTotalSize() - scrollMargin}px`, position: "relative" }}
      >
        {virtualizer.getVirtualItems().map((virtualItem) => {
          const it = items[virtualItem.index];
          const isLast = virtualItem.index === items.length - 1;

          return (
            <div
              key={it.id}
              className={`library-trow${selectedIds.has(it.id) ? " is-selected" : ""}`}
              style={{
                position: "absolute",
                top: 0,
                left: 0,
                width: "100%",
                transform: `translateY(${virtualItem.start - scrollMargin}px)`,
                // Replace the CSS :last-child rule which no longer applies to
                // absolutely-positioned rows inside the virtual container.
                ...(isLast ? { borderBottom: 0 } : {}),
              }}
              onClick={() => {
                if (selectionMode) onToggleSelect(it);
                else onOpen(it);
              }}
            >
              <div className="library-cell library-cell--select">
                {selectionMode && (
                  <button
                    type="button"
                    className={`list-select${selectedIds.has(it.id) ? " is-on" : ""}`}
                    onClick={(e) => {
                      e.stopPropagation();
                      onToggleSelect(it);
                    }}
                    title={selectedIds.has(it.id) ? "Retirer" : "Selectionner"}
                  >
                    <AppIcon name={selectedIds.has(it.id) ? "check_box" : "check_box_outline_blank"} />
                  </button>
                )}
              </div>
              <div className="library-cell">{it.titleClean || it.title || "-"}</div>
              <div className="library-cell">
                {(() => {
                  const label = getUnifiedLabel(it);
                  if (!label) return "-";
                  const key = it?.unifiedCategoryKey || "unknown";
                  return <span className={`cat-bubble cat-bubble--${key}`}>{label}</span>;
                })()}
              </div>
              <div className="library-cell">
                {isSeriesItem(it) ? formatSeasonEpisode(it) || "-" : "-"}
              </div>
              <div className="library-cell">
                {it.resolution ? (
                  <div className="library-quality">
                    <span className={`banner-pill ${getResolutionClass(it.resolution)}`}>
                      {it.resolution}
                    </span>
                  </div>
                ) : "-"}
              </div>
              <div className="library-cell">
                {it.codec ? (
                  <div className="library-quality">
                    <span className="banner-pill">{it.codec}</span>
                  </div>
                ) : "-"}
              </div>
              <div className="library-cell">{fmtSizeGo(it.sizeBytes || it.size_bytes || 0) || "-"}</div>
              <div className="library-cell">{it.date || "-"}</div>
              <div className="library-cell">{it.seeders ?? "-"}</div>
              <div className="library-cell">{it.grabs ?? "-"}</div>
              <div className="library-cell">
                {(() => {
                  const indexerName = sourceNameById.get(Number(it.sourceId)) || "";
                  if (!indexerName) return "-";
                  const indexerClass = getIndexerClass(indexerName);
                  const indexerStyle = buildIndexerPillStyle(sourceColorById?.get(Number(it.sourceId)) || null);
                  return (
                    <span
                      className={`banner-pill banner-pill--indexer${indexerClass ? ` ${indexerClass}` : ""}`}
                      style={indexerStyle || undefined}
                    >
                      {indexerName}
                    </span>
                  );
                })()}
              </div>
              <div className="library-cell library-cell--edit">
                {!selectionMode && (
                  <button
                    type="button"
                    className="list-edit"
                    title="Renommer"
                    onClick={(e) => {
                      e.stopPropagation();
                      onRename(it);
                    }}
                  >
                    <AppIcon name="edit" />
                  </button>
                )}
              </div>
            </div>
          );
        })}
      </div>
    </div>
  );
}

export default memo(LibraryList);
