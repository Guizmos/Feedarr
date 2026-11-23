import { useCallback, useLayoutEffect, useMemo, useRef, useState } from "react";
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
 * Poster rows have a predictable aspect ratio. Card rows opt into measurement
 * so font metrics, responsive CSS and the hover expansion determine their size.
 * TanStack owns the gap separately from the row height (no trailing empty row gap).
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
 * @param {Function} [opts.minColsFn=null]       - Optional function (containerWidth) => minCols.
 *   Use this to enforce a minimum column count at specific breakpoints (e.g. mobile).
 * @returns {{ containerRef, rows, numCols, scrollMargin, virtualizer }}
 */
export function useVirtualGrid(items, cardSize, {
  gap = 20,
  gapFn = null,
  rowGap = null,
  rowGapFn = null,
  cardHeightRatio = 1.5,
  titleAreaPx = 44,
  estimatedCardHeight = null,
  estimatedCardHeightFn = null,
  overscan = 5,
  minColsFn = null,
  measureRows = false,
} = {}) {
  const scrollRef = useScrollContainer();
  const containerRef = useRef(null);
  const containerWidth = useContainerWidth(containerRef);
  const [scrollMargin, setScrollMargin] = useState(0);

  const minCols = minColsFn ? minColsFn(containerWidth) : 1;
  const resolvedGap = gapFn ? gapFn(containerWidth) : gap;
  const resolvedRowGap = rowGapFn
    ? rowGapFn(containerWidth)
    : (rowGap != null ? rowGap : resolvedGap);

  const { numCols, colWidth, estimatedRowHeight } = useGridLayout(containerWidth, cardSize, {
    gap: resolvedGap,
    cardHeightRatio,
    titleAreaPx,
    minCols,
  });
  const resolvedEstimatedCardHeight = estimatedCardHeightFn
    ? estimatedCardHeightFn({
        containerWidth,
        cardSize,
        numCols,
        colWidth,
        gap: resolvedGap,
        rowGap: resolvedRowGap,
      })
    : estimatedCardHeight;

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
  }, [scrollRef, items.length, containerWidth]);

  const geometryKey = `${cardSize}:${numCols}:${colWidth}`;
  const [measuredEstimate, setMeasuredEstimate] = useState(null);
  const fallbackHeight = resolvedEstimatedCardHeight ?? (estimatedRowHeight > 0 ? estimatedRowHeight : 200);
  const rowHeight = measureRows && measuredEstimate?.key === geometryKey
    ? measuredEstimate.height : fallbackHeight;

  const measureRow = useCallback((element, entry) => {
    // Keep fractional CSS pixels; rounding creates visible drift between posters.
    const height = entry?.borderBoxSize?.[0]?.blockSize ?? element.getBoundingClientRect().height;
    // Seed off-screen estimates from actual resting rows, never a future hover size.
    if (height > 0 && !element.matches(":hover")) {
      setMeasuredEstimate(previous => previous?.key === geometryKey
        ? previous : { key: geometryKey, height });
    }
    return height;
  }, [geometryKey]);

  const virtualizer = useVirtualizer({
    count: rows.length,
    getScrollElement: () => scrollRef?.current ?? null,
    estimateSize: () => rowHeight,
    gap: resolvedRowGap,
    ...(measureRows ? { measureElement: measureRow } : {}),
    overscan,
    scrollMargin,
  });

  useLayoutEffect(() => {
    // Updating estimateSize alone does not invalidate TanStack's cached sizes.
    virtualizer.measure();
    if (measureRows) {
      for (const element of virtualizer.elementsCache.values()) {
        virtualizer.measureElement(element);
      }
    }
  }, [virtualizer, cardSize, numCols, colWidth, rowHeight, measureRows]);

  return {
    containerRef,
    rows,
    numCols,
    scrollMargin,
    virtualizer,
    gap: resolvedGap,
    rowGap: resolvedRowGap,
  };
}
