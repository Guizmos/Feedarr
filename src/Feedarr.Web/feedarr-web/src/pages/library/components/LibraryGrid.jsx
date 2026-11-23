import React, { useMemo, useState } from "react";
import PosterCard from "../../../ui/PosterCard.jsx";
import { useVirtualGrid } from "../hooks/useVirtualGrid.js";

const GAP = 10;
const GRID_MIN_COLS_FN = (width) => (width > 0 && width <= 768 ? 2 : 1);

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
  const [hoveredRowIndex, setHoveredRowIndex] = useState(null);
  const { containerRef, rows, numCols, scrollMargin, virtualizer, gap, rowGap } = useVirtualGrid(
    items,
    cardSize,
    {
      gap: GAP,
      measureRows: true,
      minColsFn: GRID_MIN_COLS_FN,
    },
  );

  // --card-scale drives all scaled dimensions inside PosterCard.
  // gridTemplateColumns is computed per virtual row to match numCols exactly.
  const scaleStyle = useMemo(
    () => (cardSize ? { "--card-scale": cardSize / 190 } : undefined),
    [cardSize],
  );

  const totalHeight = Math.max(0, virtualizer.getTotalSize());

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
            data-index={virtualRow.index}
            ref={virtualizer.measureElement}
            style={{
              position: "absolute",
              top: 0,
              left: 0,
              width: "100%",
              transform: `translateY(${virtualRow.start - scrollMargin}px)`,
              display: "grid",
              gridTemplateColumns: `repeat(${numCols}, 1fr)`,
              columnGap: `${gap}px`,
              rowGap: `${rowGap}px`,
              alignItems: "start",
              zIndex: hoveredRowIndex === virtualRow.index ? 2 : 1,
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
                onHoverStart={() => setHoveredRowIndex(virtualRow.index)}
                onHoverEnd={() => {
                  setHoveredRowIndex((current) => (current === virtualRow.index ? null : current));
                }}
              />
            ))}
          </div>
        );
      })}
    </div>
  );
}

export default React.memo(LibraryGrid);
