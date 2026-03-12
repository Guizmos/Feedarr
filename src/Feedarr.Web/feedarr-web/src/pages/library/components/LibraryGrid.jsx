import React, { useMemo } from "react";
import PosterCard from "../../../ui/PosterCard.jsx";
import { useVirtualGrid } from "../hooks/useVirtualGrid.js";

/** Matches .grid { gap: 20px } in styles.css */
const GAP = 20;

/**
 * Vue grille de la bibliothèque (cartes poster virtualisées)
 *
 * Uses TanStack Virtual with a row-based strategy: items are chunked into
 * rows of numCols and only the visible rows are rendered in the DOM.
 *
 * Scroll container: the .content div in Shell, via ScrollContainerContext.
 */
function LibraryGrid({
  items,
  onDownload,
  onOpen,
  selectionMode,
  selectedIds,
  onToggleSelect,
  onRename,
  sortBy,
  sourceNameById,
  sourceColorById,
  sourceId,
  arrStatusMap,
  integrationMode,
  cardSize,
}) {
  // PosterCard height is fixed by --card-scale, not proportional to colWidth.
  // Base geometry @ cardSize=190 (scale=1):
  //   poster 240 + statusline 5 + meta ~94 = ~326px at rest (posterSubAlt collapsed).
  //   On hover, posterSubAlt expands by 20px (non-scaled, fixed px).
  //   360 = 326(rest) + 20(hover) + 14(safety buffer for subpixel/line-height variance).
  // This ensures virtual row height absorbs both states at all cardSize values.
  const estimatedCardHeight = useMemo(
    () => Math.round((360 * cardSize) / 190),
    [cardSize],
  );

  const { containerRef, rows, numCols, scrollMargin, virtualizer } = useVirtualGrid(
    items,
    cardSize,
    { gap: GAP, estimatedCardHeight },
  );

  // --card-scale drives all scaled dimensions inside PosterCard.
  // gridTemplateColumns is computed per virtual row to match numCols exactly.
  const scaleStyle = useMemo(
    () => (cardSize ? { "--card-scale": cardSize / 190 } : undefined),
    [cardSize],
  );

  const totalHeight = Math.max(0, virtualizer.getTotalSize() - scrollMargin);

  return (
    <div
      ref={containerRef}
      className="grid-virtualized"
      style={{ ...scaleStyle, height: `${totalHeight}px`, position: "relative" }}
    >
      {virtualizer.getVirtualItems().map((virtualRow) => {
        const rowItems = rows[virtualRow.index];
        if (!rowItems) return null;
        return (
          <div
            key={virtualRow.key}
            style={{
              position: "absolute",
              top: 0,
              left: 0,
              width: "100%",
              transform: `translateY(${virtualRow.start - scrollMargin}px)`,
              display: "grid",
              gridTemplateColumns: `repeat(${numCols}, 1fr)`,
              gap: `${GAP}px`,
            }}
          >
            {rowItems.map((it, colIndex) => (
              <PosterCard
                key={it.id}
                item={it}
                itemIndex={virtualRow.index * numCols + colIndex}
                cardSize={cardSize}
                onDownload={onDownload}
                onOpen={onOpen}
                selectionMode={selectionMode}
                selected={selectedIds.has(it.id)}
                onToggleSelect={onToggleSelect}
                onRename={onRename}
                sortBy={sortBy}
                indexerLabel={sourceNameById.get(Number(it.sourceId)) || ""}
                indexerColor={sourceColorById.get(Number(it.sourceId)) || null}
                showIndexerPill={!sourceId}
                indexerPillPosition="left"
                integrationMode={integrationMode}
                arrStatus={arrStatusMap[it.id]}
              />
            ))}
          </div>
        );
      })}
    </div>
  );
}

export default React.memo(LibraryGrid);
