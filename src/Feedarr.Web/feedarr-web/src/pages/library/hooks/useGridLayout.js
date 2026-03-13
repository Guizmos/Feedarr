import { useMemo } from "react";

/**
 * Pure calculation: reproduces the browser's CSS auto-fill minmax() logic in JS.
 *
 * This matches the behaviour of:
 *   grid-template-columns: repeat(auto-fill, minmax(cardSize, 1fr))
 * with a fixed gap between columns.
 *
 * estimatedRowHeight is an initial approximation for the virtualizer's row height
 * seed. It will be replaced by real measurements (measureElement) in later PRs.
 *
 * @param {number} containerWidth  - Width of the grid container in pixels.
 * @param {number} cardSize        - Minimum card width (the minmax() min value), in pixels.
 * @param {object} [opts]
 * @param {number} [opts.gap=20]              - Column gap in pixels (matches CSS .grid { gap: 20px }).
 * @param {number} [opts.cardHeightRatio=1.5] - Poster height-to-width ratio (height = width * ratio).
 * @param {number} [opts.titleAreaPx=44]      - Extra height below poster for title/badges area.
 * @returns {{ numCols: number, colWidth: number, estimatedRowHeight: number }}
 */
export function computeGridLayout(containerWidth, cardSize, { gap = 20, cardHeightRatio = 1.5, titleAreaPx = 44 } = {}) {
  if (containerWidth <= 0 || cardSize <= 0) {
    return { numCols: 1, colWidth: 0, estimatedRowHeight: 0 };
  }

  const numCols = Math.max(1, Math.floor((containerWidth + gap) / (cardSize + gap)));
  const colWidth = (containerWidth - (numCols - 1) * gap) / numCols;
  const estimatedRowHeight = Math.round(colWidth * cardHeightRatio + titleAreaPx);

  return { numCols, colWidth, estimatedRowHeight };
}

/**
 * Hook wrapper around computeGridLayout.
 * Re-computes only when containerWidth, cardSize, or any option value changes.
 *
 * Designed to be used by both LibraryGrid (baseline 190px) and LibraryPoster
 * (baseline 180px) — the baseline is NOT part of this hook; it remains a
 * CSS concern (--card-scale) in the grid components.
 *
 * @param {number} containerWidth - Width of the grid container in pixels.
 * @param {number} cardSize       - Minimum card width in pixels.
 * @param {object} [opts]         - Same options as computeGridLayout.
 * @returns {{ numCols: number, colWidth: number, estimatedRowHeight: number }}
 */
export function useGridLayout(containerWidth, cardSize, opts = {}) {
  const { gap = 20, cardHeightRatio = 1.5, titleAreaPx = 44 } = opts;

  return useMemo(
    () => computeGridLayout(containerWidth, cardSize, { gap, cardHeightRatio, titleAreaPx }),
    [containerWidth, cardSize, gap, cardHeightRatio, titleAreaPx],
  );
}
