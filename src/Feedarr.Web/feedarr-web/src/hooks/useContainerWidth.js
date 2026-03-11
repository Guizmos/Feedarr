import { useState, useEffect } from "react";

/**
 * Measures the pixel width of a DOM element via ResizeObserver.
 *
 * Returns 0 before the first measurement (before mount or if ref.current is null).
 * Only triggers re-renders when the floored width value actually changes.
 *
 * @param {React.RefObject} ref - Ref attached to the element to observe.
 * @returns {number} Current width in pixels (Math.floor of contentRect.width).
 */
export function useContainerWidth(ref) {
  const [width, setWidth] = useState(0);

  useEffect(() => {
    if (!ref.current) return;

    const obs = new ResizeObserver((entries) => {
      for (const entry of entries) {
        const w = Math.floor(entry.contentRect.width);
        setWidth((prev) => (prev === w ? prev : w));
      }
    });

    obs.observe(ref.current);
    return () => obs.disconnect();
  }, [ref]);

  return width;
}
