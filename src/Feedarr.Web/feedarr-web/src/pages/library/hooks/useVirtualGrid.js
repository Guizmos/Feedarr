import { useLayoutEffect, useMemo, useRef, useState } from "react";
import { useVirtualizer } from "@tanstack/react-virtual";
import { useScrollContainer } from "../../../context/ScrollContainerContext.js";
import { useContainerWidth } from "../../../hooks/useContainerWidth.js";
import { useGridLayout } from "./useGridLayout.js";

/**
 * Shared virtualisation hook for LibraryGrid and LibraryPoster.
 *
 * Encapsulates:
 *  - scroll-container access via ScrollContainerContext
 *  - container-width measurement via ResizeObserver
 *  - CSS-grid layout calculation (numCols, estimatedRowHeight)
 *  - scrollMargin computation (distance from .content top to grid top)
 *  - item chunking into rows of numCols items
 *  - TanStack Virtual row-based virtualizer instance
 *
 * estimateSize is fixed (no measureElement) to keep getTotalSize() stable
 * during scrollbar drag — same reasoning as the PR2 list view fix.
 *
 * The row height seed includes the gap so virtualizer row boundaries
 * match the rendered CSS grid spacing. The trailing gap on the last row
 * adds a negligible ~gap-px over-estimation to the total height.
 *
 * @param {object[]} items        - Flat array of items to virtualise.
 * @param {number}   cardSize     - Minimum card width in px (minmax min).
 * @param {object}   [opts]
 * @param {number}   [opts.gap=20]              - Grid gap in px (row + column).
 * @param {number}   [opts.cardHeightRatio=1.5] - height / width ratio for one card.
 * @param {number}   [opts.titleAreaPx=44]      - Extra height below the card image.
 * @param {number}   [opts.estimatedCardHeight=null] - When provided, overrides the
 *   colWidth-based formula. Use this for cards whose height depends on cardSize
 *   (fixed scaling) rather than on column width (aspect-ratio).
 * @param {number}   [opts.overscan=5]          - Off-screen rows to keep mounted.
 * @returns {{ containerRef, rows, numCols, scrollMargin, virtualizer }}
 */
export function useVirtualGrid(items, cardSize, {
  gap = 20,
  cardHeightRatio = 1.5,
  titleAreaPx = 44,
  estimatedCardHeight = null,
  overscan = 5,
} = {}) {
  const scrollRef = useScrollContainer();
  const containerRef = useRef(null);
  const containerWidth = useContainerWidth(containerRef);
  const [scrollMargin, setScrollMargin] = useState(0);

  const { numCols, estimatedRowHeight } = useGridLayout(containerWidth, cardSize, {
    gap,
    cardHeightRatio,
    titleAreaPx,
  });

  // Chunk flat items into rows of numCols.
  const rows = useMemo(() => {
    if (numCols <= 0 || items.length === 0) return [];
    const result = [];
    for (let i = 0; i < items.length; i += numCols) {
      result.push(items.slice(i, i + numCols));
    }
    return result;
  }, [items, numCols]);

  // Distance from the scroll container's top to the virtual container's top.
  // Re-measured when items.length changes (filter/limit change may shift layout).
  useLayoutEffect(() => {
    const scrollEl = scrollRef?.current;
    if (!containerRef.current || !scrollEl) return;
    const containerRect = containerRef.current.getBoundingClientRect();
    const scrollRect = scrollEl.getBoundingClientRect();
    setScrollMargin(containerRect.top - scrollRect.top + scrollEl.scrollTop);
  }, [scrollRef, items.length]);

  // Row height = card area + gap. The gap accounts for the vertical spacing
  // between rows in the CSS grid. Fallback to 200 before first measurement.
  // When estimatedCardHeight is provided (fixed-scale cards like PosterCard),
  // it takes precedence over the colWidth-based estimatedRowHeight.
  const rowHeight = estimatedCardHeight != null
    ? Math.max(1, estimatedCardHeight) + gap
    : (estimatedRowHeight > 0 ? estimatedRowHeight + gap : 200);

  const virtualizer = useVirtualizer({
    count: rows.length,
    getScrollElement: () => scrollRef?.current ?? null,
    estimateSize: () => rowHeight,
    overscan,
    scrollMargin,
  });

  return { containerRef, rows, numCols, scrollMargin, virtualizer };
}
