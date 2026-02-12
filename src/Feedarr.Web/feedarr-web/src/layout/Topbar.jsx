import React, { useCallback, useEffect, useRef, useState } from "react";
import { useLocation, useSearchParams } from "react-router-dom";
import AppIcon from "../ui/AppIcon.jsx";

export default function Topbar({ onToggleNav }) {
  const location = useLocation();
  const [searchParams, setSearchParams] = useSearchParams();
  const isLibrary = location.pathname === "/library";
  const q = searchParams.get("q") || "";
  const searchInputRef = useRef(null);
  const [searchOpen, setSearchOpen] = useState(false);
  const [searchDraft, setSearchDraft] = useState(q);
  const searchInputId = "topbar-library-search";

  const setQuery = useCallback((next) => {
    const normalized = String(next || "").trim();
    const params = new URLSearchParams(searchParams);
    if (normalized) params.set("q", normalized);
    else params.delete("q");
    setSearchParams(params, { replace: true });
  }, [searchParams, setSearchParams]);

  useEffect(() => {
    setSearchDraft((prev) => (prev === q ? prev : q));
  }, [q]);

  useEffect(() => {
    if (!isLibrary) return undefined;
    if (searchDraft === q) return undefined;
    const timer = window.setTimeout(() => {
      setQuery(searchDraft);
    }, 280);
    return () => window.clearTimeout(timer);
  }, [isLibrary, searchDraft, q, setQuery]);

  const openSearch = useCallback(() => {
    setSearchOpen(true);
    requestAnimationFrame(() => searchInputRef.current?.focus());
  }, []);

  const closeSearch = useCallback(() => {
    setSearchOpen(false);
  }, []);

  useEffect(() => {
    if (!searchOpen) return;
    function onKey(e) {
      if (e.key === "Escape") closeSearch();
    }
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [searchOpen, closeSearch]);

  // Close search when navigating away from library
  useEffect(() => {
    if (!isLibrary) setSearchOpen(false);
  }, [isLibrary]);

  return (
    <header className={`topbar${searchOpen ? " is-search-open" : ""}`}>
      <div className="topbar-left">
        <button
          type="button"
          className="topbar-burger"
          aria-label="Ouvrir le menu"
          onClick={onToggleNav}
        >
          <AppIcon name="menu" />
        </button>
        <div className="brand">
          <img
            className="brand-logo"
            src="/favicon-ios.png"
            alt="Feedarr"
          />
        </div>
      </div>

      <div className="topbar-center">
        {isLibrary && (
          <>
            <div
              className="topbar-search"
              role="search"
              onClick={() => searchInputRef.current?.focus()}
            >
              <AppIcon name="search" className="topbar-search__icon" />
              <input
                id={searchInputId}
                ref={searchInputRef}
                value={searchDraft}
                onChange={(e) => setSearchDraft(e.target.value)}
                onKeyDown={(e) => {
                  if (e.key === "Enter") {
                    setQuery(searchDraft);
                  }
                }}
                aria-label="Rechercher dans la bibliothèque"
                placeholder="Recherche"
              />
            </div>
            <button
              type="button"
              className="topbar-search-close"
              aria-label="Fermer la recherche"
              onClick={closeSearch}
            >
              <AppIcon name="close" />
            </button>
          </>
        )}
      </div>

      <div className="topbar-right">
        {isLibrary && (
          <button
            type="button"
            className="topbar-search-toggle"
            aria-label="Recherche"
            aria-controls={searchInputId}
            aria-expanded={searchOpen}
            onClick={openSearch}
          >
            <AppIcon name="search" />
          </button>
        )}
      </div>
    </header>
  );
}
