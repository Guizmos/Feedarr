import { useEffect, useRef } from "react";

/**
 * Visibility-aware setInterval hook.
 *
 * - Calls `fn` immediately on mount, then every `intervalMs` milliseconds.
 * - Pauses (clears the interval) while `document.hidden` is true.
 * - Resumes with an immediate call when the tab becomes visible again.
 * - Wraps `fn` in a ref so changing the callback never re-creates the timer.
 *
 * @param {() => void} fn          Callback to invoke on each tick.
 * @param {number}     intervalMs  Polling interval in milliseconds.
 * @param {boolean}    [enabled=true]  Set to false to disable polling entirely.
 */
export default function usePolling(fn, intervalMs, enabled = true) {
  const fnRef = useRef(fn);
  fnRef.current = fn;

  useEffect(() => {
    if (!enabled || typeof window === "undefined") return undefined;

    let timer = null;

    function clearTimer() {
      if (timer) {
        clearInterval(timer);
        timer = null;
      }
    }

    function schedule() {
      timer = setInterval(() => {
        if (!document.hidden) fnRef.current();
      }, intervalMs);
    }

    function onVisibility() {
      if (document.hidden) {
        clearTimer();
      } else {
        // Tab became visible: fire immediately then restart the interval
        fnRef.current();
        clearTimer();
        schedule();
      }
    }

    document.addEventListener("visibilitychange", onVisibility);
    fnRef.current(); // initial call on mount
    schedule();

    return () => {
      document.removeEventListener("visibilitychange", onVisibility);
      clearTimer();
    };
  }, [intervalMs, enabled]);
}
