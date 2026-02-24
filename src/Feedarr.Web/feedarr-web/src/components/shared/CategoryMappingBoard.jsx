import React, { useEffect, useMemo, useRef, useState } from "react";

export const FEEDARR_GROUPS = [
  { key: "films", label: "Films" },
  { key: "series", label: "Série TV" },
  { key: "animation", label: "Animation" },
  { key: "anime", label: "Anime" },
  { key: "comics", label: "Comics" },
  { key: "books", label: "Livres" },
  { key: "audio", label: "Audio" },
  { key: "spectacle", label: "Spectacle" },
  { key: "emissions", label: "Emissions" },
];

const GROUP_LABELS = Object.fromEntries(FEEDARR_GROUPS.map((group) => [group.key, group.label]));

function normalizeGroupKey(value) {
  const key = String(value || "").trim().toLowerCase();
  return GROUP_LABELS[key] ? key : null;
}

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
  const assigned = normalizeGroupKey(mappings.get(category.id));
  if (mode === "assigned" && !assigned) return false;
  if (mode === "unassigned" && assigned) return false;
  if (!search) return true;

  const haystack = `${category.id} ${category.name}`.toLowerCase();
  return haystack.includes(search.toLowerCase());
}

export default function CategoryMappingBoard({ categories, mappings, onChangeMapping, variant = "default" }) {
  const [query, setQuery] = useState("");
  const [mode, setMode] = useState("all");
  const [openMenuId, setOpenMenuId] = useState(null);
  const rootRef = useRef(null);

  const map = mappings instanceof Map ? mappings : new Map();
  const normalizedCategories = useMemo(() => normalizeCategories(categories), [categories]);

  useEffect(() => {
    const onDocPointerDown = (event) => {
      if (!rootRef.current) return;
      if (rootRef.current.contains(event.target)) return;
      setOpenMenuId(null);
    };
    document.addEventListener("mousedown", onDocPointerDown);
    return () => document.removeEventListener("mousedown", onDocPointerDown);
  }, []);

  const groupCounts = useMemo(() => {
    const counts = Object.fromEntries(FEEDARR_GROUPS.map((group) => [group.key, 0]));
    for (const category of normalizedCategories) {
      const normalized = normalizeGroupKey(map.get(category.id));
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

  const standardCategories = filtered.filter((category) => category.isStandard);
  const specificCategories = filtered.filter((category) => !category.isStandard);
  const boardClassName = `category-mapping-board${variant === "wizard" ? " category-mapping-board--wizard" : ""}`;

  return (
    <div className={boardClassName} ref={rootRef}>
      <div className="category-mapping-board__top">
        <input
          className="category-mapping-board__search"
          value={query}
          onChange={(event) => setQuery(event.target.value)}
          placeholder="Rechercher par id ou nom..."
        />
        <div className="category-mapping-board__filters">
          <button
            type="button"
            className={`type-chip${mode === "all" ? " type-chip--active" : ""}`}
            onClick={() => setMode("all")}
          >
            Tout
          </button>
          <button
            type="button"
            className={`type-chip${mode === "assigned" ? " type-chip--active" : ""}`}
            onClick={() => setMode("assigned")}
          >
            Assignées
          </button>
          <button
            type="button"
            className={`type-chip${mode === "unassigned" ? " type-chip--active" : ""}`}
            onClick={() => setMode("unassigned")}
          >
            Non assignées
          </button>
        </div>
      </div>

      <div className="category-mapping-board__groups">
        {FEEDARR_GROUPS.map((group) => (
          <div key={group.key} className={`category-group-card cat-bubble--${group.key}`}>
            <div className="category-group-card__label">{group.label}</div>
            <div className="category-group-card__count">{groupCounts[group.key] || 0}</div>
          </div>
        ))}
      </div>

      <div className="category-mapping-board__columns">
        <MappingColumn
          title="Standard"
          categories={standardCategories}
          mappings={map}
          openMenuId={openMenuId}
          onToggleMenu={setOpenMenuId}
          onChangeMapping={onChangeMapping}
        />
        <MappingColumn
          title="Spécifiques"
          categories={specificCategories}
          mappings={map}
          openMenuId={openMenuId}
          onToggleMenu={setOpenMenuId}
          onChangeMapping={onChangeMapping}
        />
      </div>
    </div>
  );
}

function MappingColumn({ title, categories, mappings, openMenuId, onToggleMenu, onChangeMapping }) {
  return (
    <div className="category-mapping-board__column">
      <div className="category-mapping-board__column-title">{title}</div>
      <div className="category-mapping-board__chips">
        {categories.length === 0 && <div className="muted">Aucune catégorie.</div>}
        {categories.map((category) => {
          const assignedKey = normalizeGroupKey(mappings.get(category.id));
          const assignedLabel = assignedKey ? GROUP_LABELS[assignedKey] : "—";
          const menuOpen = openMenuId === category.id;
          return (
            <div
              className={`mapping-chip${assignedKey ? ` mapping-chip--assigned mapping-chip--${assignedKey}` : ""}`}
              key={category.id}
            >
              <div className="mapping-chip__line">
                <span className="mapping-chip__id">{category.id}</span>
                <span className="mapping-chip__name">{category.name}</span>
              </div>
              <div className="mapping-chip__line">
                <button
                  type="button"
                  className="mapping-chip__assign"
                  onClick={() => onToggleMenu(menuOpen ? null : category.id)}
                >
                  {assignedLabel}
                </button>
                {menuOpen && (
                  <div className="mapping-chip__menu">
                    {FEEDARR_GROUPS.map((group) => (
                      <button
                        key={group.key}
                        type="button"
                        className={`mapping-chip__menu-item${assignedKey === group.key ? " is-active" : ""}`}
                        onClick={() => {
                          onChangeMapping?.(category.id, group.key);
                          onToggleMenu(null);
                        }}
                      >
                        {group.label}
                      </button>
                    ))}
                    <button
                      type="button"
                      className="mapping-chip__menu-item"
                      onClick={() => {
                        onChangeMapping?.(category.id, null);
                        onToggleMenu(null);
                      }}
                    >
                      Retirer
                    </button>
                  </div>
                )}
              </div>
            </div>
          );
        })}
      </div>
    </div>
  );
}
