import React, { createContext, useContext } from "react";

/**
 * Provides the main page scroll container (the `.content` div in Shell) to any
 * descendant component that needs it — primarily virtualizers in Library views.
 *
 * The provider receives a stable React ref and shares it directly.
 * Consumers call `useScrollContainer()` and get the ref object whose `.current`
 * points to the scrollable DOM element.
 *
 * This context is intentionally free of any business logic.
 * It will be reused by LibraryGrid and LibraryPoster in later PRs.
 */
const ScrollContainerContext = createContext(null);

/**
 * Wrap the scrollable content area (and its children) with this provider.
 *
 * @param {{ children: React.ReactNode, scrollRef: React.RefObject<HTMLElement> }} props
 */
export function ScrollContainerProvider({ children, scrollRef }) {
  return React.createElement(
    ScrollContainerContext.Provider,
    { value: scrollRef },
    children,
  );
}

/**
 * Returns the ref object pointing to the main scroll container element.
 * `ref.current` is the DOM element (or null before mount).
 *
 * @returns {React.RefObject<HTMLElement> | null}
 */
export function useScrollContainer() {
  return useContext(ScrollContainerContext);
}
