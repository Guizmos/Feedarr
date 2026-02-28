import React, { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { createPortal } from "react-dom";
import {
  CATEGORY_GROUPS,
  normalizeCategoryGroupKey,
} from "../../domain/categories/index.js";
import { tr } from "../../app/uiText.js";
import AppIcon from "../../ui/AppIcon.jsx";
import CategoryPreviewModal from "./CategoryPreviewModal.jsx";

const CATEGORY_GROUP_LABELS_EN = {
  films: "Movies",
  series: "TV Series",
  animation: "Animation",
  anime: "Anime",
  games: "Video Games",
  comics: "Comics",
  books: "Books",
  audio: "Audio",
  spectacle: "Live Shows",
  emissions: "Shows",
};

function normalizeCategories(categories) {
  const seen = new Map();
  for (const category of Array.isArray(categories) ? categories : []) {
    const id = Number(category?.id);
    if (!Number.isFinite(id) || id <= 0) continue;
    if (seen.has(id)) continue;
    if (category?.isSupported === false) continue;
    seen.set(id, {
      id,
      name: String(category?.name || `Cat ${id}`),
      isStandard: category?.isStandard === true || (id >= 1000 && id <= 8999),
      isSupported: category?.isSupported !== false,
    });
  }
  return [...seen.values()].sort((a, b) => a.id - b.id);
}

function categoryMatchesFilter(category, search, mode, mappings) {
  const assigned = normalizeCategoryGroupKey(mappings.get(category.id));
  if (mode === "assigned" && !assigned) return false;
  if (mode === "unassigned" && assigned) return false;
  if (!search) return true;

  const haystack = `${category.id} ${category.name}`.toLowerCase();
  return haystack.includes(search.toLowerCase());
}

export default function CategoryMappingBoard({
  categories,
  mappings,
  onChangeMapping,
  variant = "default",
  sourceId,
  previewCredentials,
  infoNote = null,
}) {
  const [query, setQuery] = useState("");
  const [mode, setMode] = useState("all");
  const [openMenuId, setOpenMenuId] = useState(null);
  const [menuPosition, setMenuPosition] = useState(null);
  const [previewCat, setPreviewCat] = useState(null); // { id, name }
  const rootRef = useRef(null);
  const assignButtonRefs = useRef(new Map());

  const map = useMemo(
    () => (mappings instanceof Map ? mappings : new Map()),
    [mappings]
  );
  const normalizedCategories = useMemo(() => normalizeCategories(categories), [categories]);
  const groupLabels = Object.fromEntries(
    CATEGORY_GROUPS.map((group) => [
      group.key,
      tr(group.label, CATEGORY_GROUP_LABELS_EN[group.key] || group.label),
    ])
  );
  const categoryNameMap = useMemo(
    () =>
      Object.fromEntries(
        normalizedCategories.map((category) => [category.id, category.name])
      ),
    [normalizedCategories]
  );
  const groupsByLabel = [...CATEGORY_GROUPS]
    .map((group) => ({ ...group, label: groupLabels[group.key] || group.label }))
    .sort((a, b) => a.label.localeCompare(b.label, "en", { sensitivity: "base" }));

  const closeMenu = useCallback(() => {
    setOpenMenuId(null);
    setMenuPosition(null);
  }, []);

  const registerAssignButton = useCallback((id, node) => {
    if (node) assignButtonRefs.current.set(id, node);
    else assignButtonRefs.current.delete(id);
  }, []);

  const resolveMenuPosition = useCallback((id) => {
    const anchor = assignButtonRefs.current.get(id);
    if (!anchor) return null;

    const rect = anchor.getBoundingClientRect();
    const menuWidth = 180;
    const estimatedMenuHeight = CATEGORY_GROUPS.length * 34 + 44;
    const viewportPadding = 8;
    const offset = 6;

    let left = rect.right - menuWidth;
    left = Math.max(viewportPadding, left);
    left = Math.min(left, window.innerWidth - menuWidth - viewportPadding);

    const spaceAbove = rect.top - viewportPadding;
    const spaceBelow = window.innerHeight - rect.bottom - viewportPadding;
    const shouldOpenTop =
      spaceBelow < estimatedMenuHeight && spaceAbove > spaceBelow;

    const placement = shouldOpenTop ? "top" : "bottom";
    const top = shouldOpenTop
      ? rect.top - offset
      : rect.bottom + offset;

    return { top, left, placement };
  }, []);

  const toggleMenu = useCallback((id) => {
    if (openMenuId === id) {
      closeMenu();
      return;
    }
    const nextPosition = resolveMenuPosition(id);
    if (!nextPosition) {
      closeMenu();
      return;
    }
    setOpenMenuId(id);
    setMenuPosition(nextPosition);
  }, [closeMenu, openMenuId, resolveMenuPosition]);

  useEffect(() => {
    const onDocPointerDown = (event) => {
      if (!rootRef.current) return;
      if (event.target?.closest?.("[data-mapping-menu-root='true']")) return;
      if (rootRef.current.contains(event.target)) return;
      closeMenu();
    };
    document.addEventListener("mousedown", onDocPointerDown);
    return () => document.removeEventListener("mousedown", onDocPointerDown);
  }, [closeMenu]);

  useEffect(() => {
    if (!openMenuId) return undefined;

    const updateMenuPosition = () => {
      const nextPosition = resolveMenuPosition(openMenuId);
      if (!nextPosition) {
        closeMenu();
        return;
      }
      setMenuPosition(nextPosition);
    };

    const closeOnEscape = (event) => {
      if (event.key === "Escape") closeMenu();
    };

    window.addEventListener("resize", updateMenuPosition);
    window.addEventListener("scroll", updateMenuPosition, true);
    window.addEventListener("keydown", closeOnEscape);
    return () => {
      window.removeEventListener("resize", updateMenuPosition);
      window.removeEventListener("scroll", updateMenuPosition, true);
      window.removeEventListener("keydown", closeOnEscape);
    };
  }, [closeMenu, openMenuId, resolveMenuPosition]);

  const groupCounts = useMemo(() => {
    const counts = Object.fromEntries(CATEGORY_GROUPS.map((group) => [group.key, 0]));
    for (const category of normalizedCategories) {
      const normalized = normalizeCategoryGroupKey(map.get(category.id));
      if (!normalized) continue;
      counts[normalized] += 1;
    }
    return counts;
  }, [map, normalizedCategories]);

  const filtered = useMemo(
    () =>
      normalizedCategories.filter((category) =>
        categoryMatchesFilter(category, query, mode, map)
      ),
    [normalizedCategories, query, mode, map]
  );

  useEffect(() => {
    if (!openMenuId) return;
    if (filtered.some((category) => category.id === openMenuId)) return;
    closeMenu();
  }, [closeMenu, filtered, openMenuId]);

  const standardCategories = filtered.filter((category) => category.isStandard);
  const specificCategories = filtered.filter((category) => !category.isStandard);
  const boardClassName = `category-mapping-board${variant === "wizard" ? " category-mapping-board--wizard" : ""}`;
  const currentAssignedKey = openMenuId ? normalizeCategoryGroupKey(map.get(openMenuId)) : null;

  return (
    <div className={boardClassName} ref={rootRef}>
      <div className="category-mapping-board__top">
        <input
          className="category-mapping-board__search"
          value={query}
          onChange={(event) => setQuery(event.target.value)}
          placeholder={tr("Rechercher par id ou nom...", "Search by id or name...")}
        />
        <div className="category-mapping-board__filters">
          <button
            type="button"
            className={`type-chip${mode === "all" ? " type-chip--active" : ""}`}
            onClick={() => setMode("all")}
          >
            {tr("Tout", "All")}
          </button>
          <button
            type="button"
            className={`type-chip${mode === "assigned" ? " type-chip--active" : ""}`}
            onClick={() => setMode("assigned")}
          >
            {tr("Assignées", "Assigned")}
          </button>
          <button
            type="button"
            className={`type-chip${mode === "unassigned" ? " type-chip--active" : ""}`}
            onClick={() => setMode("unassigned")}
          >
            {tr("Non assignées", "Unassigned")}
          </button>
        </div>
      </div>

      <div className="category-mapping-board__groups">
        {groupsByLabel.map((group) => (
          <div key={group.key} className={`category-group-card cat-bubble--${group.key}`}>
            <div className="category-group-card__label">{group.label}</div>
            <div className="category-group-card__count">{groupCounts[group.key] || 0}</div>
          </div>
        ))}
      </div>

      {infoNote}

      <div className="category-mapping-board__columns">
        <MappingColumn
          title={tr("Standard", "Standard")}
          categories={standardCategories}
          mappings={map}
          groupLabels={groupLabels}
          openMenuId={openMenuId}
          onToggleMenu={toggleMenu}
          registerAssignButton={registerAssignButton}
          onPreview={sourceId || previewCredentials ? setPreviewCat : null}
        />
        <MappingColumn
          title={tr("Spécifiques", "Specific")}
          categories={specificCategories}
          mappings={map}
          groupLabels={groupLabels}
          openMenuId={openMenuId}
          onToggleMenu={toggleMenu}
          registerAssignButton={registerAssignButton}
          onPreview={sourceId || previewCredentials ? setPreviewCat : null}
        />
      </div>
      {openMenuId && menuPosition && (
        <MappingMenuPortal
          groups={groupsByLabel}
          position={menuPosition}
          assignedKey={currentAssignedKey}
          onAssign={(groupKey) => {
            onChangeMapping?.(openMenuId, groupKey);
            closeMenu();
          }}
          onClear={() => {
            onChangeMapping?.(openMenuId, null);
            closeMenu();
          }}
        />
      )}
      {previewCat && (sourceId || previewCredentials) && (
        <CategoryPreviewModal
          sourceId={sourceId ?? null}
          previewCredentials={!sourceId ? previewCredentials : null}
          catId={previewCat.id}
          catName={previewCat.name}
          categoryNameMap={categoryNameMap}
          onClose={() => setPreviewCat(null)}
        />
      )}
    </div>
  );
}

function MappingColumn({ title, categories, mappings, groupLabels, openMenuId, onToggleMenu, registerAssignButton, onPreview }) {
  return (
    <div className="category-mapping-board__column">
      <div className="category-mapping-board__column-title">{title}</div>
      <div className="category-mapping-board__chips">
        {categories.length === 0 && <div className="muted">{tr("Aucune catégorie.", "No category.")}</div>}
        {categories.map((category) => {
          const assignedKey = normalizeCategoryGroupKey(mappings.get(category.id));
          const assignedLabel = assignedKey ? groupLabels[assignedKey] : "—";
          const menuOpen = openMenuId === category.id;
          return (
            <div
              className={`mapping-chip${assignedKey ? ` mapping-chip--assigned mapping-chip--${assignedKey}` : ""}`}
              key={category.id}
            >
              <div className="mapping-chip__header">
                <div className="mapping-chip__identity">
                  <span className="mapping-chip__id">{category.id}</span>
                  <span className="mapping-chip__name">{category.name}</span>
                </div>
                <div className="mapping-chip__actions">
                  <button
                    type="button"
                    className="mapping-chip__assign"
                    ref={(node) => registerAssignButton(category.id, node)}
                    onClick={() => onToggleMenu(category.id)}
                    aria-haspopup="menu"
                    aria-expanded={menuOpen ? "true" : "false"}
                  >
                    {assignedLabel}
                  </button>
                  {onPreview && (
                    <button
                      type="button"
                      className="iconbtn mapping-chip__preview"
                      title={tr("Aperçu des 20 derniers résultats", "Preview last 20 results")}
                      aria-label={tr("Aperçu des 20 derniers résultats", "Preview last 20 results")}
                      onClick={() => onPreview({ id: category.id, name: category.name })}
                    >
                      <AppIcon name="search" />
                    </button>
                  )}
                </div>
              </div>
            </div>
          );
        })}
      </div>
    </div>
  );
}

function MappingMenuPortal({ groups, position, assignedKey, onAssign, onClear }) {
  if (typeof document === "undefined") return null;

  return createPortal(
    <div
      className={`mapping-chip__menu mapping-chip__menu--portal${
        position.placement === "top" ? " mapping-chip__menu--top" : ""
      }`}
      data-mapping-menu-root="true"
      style={{ top: `${position.top}px`, left: `${position.left}px` }}
      role="menu"
    >
      {groups.map((group) => (
        <button
          key={group.key}
          type="button"
          className={`mapping-chip__menu-item${assignedKey === group.key ? " is-active" : ""}`}
          onClick={() => onAssign(group.key)}
        >
          {group.label}
        </button>
      ))}
      <button
        type="button"
        className="mapping-chip__menu-item"
        onClick={onClear}
      >
        {tr("Retirer", "Remove")}
      </button>
    </div>,
    document.body
  );
}
