import React, { useMemo } from "react";
import ToggleSwitch from "../../ui/ToggleSwitch.jsx";

function isSelectable(category) {
  return !(category?.isStandard && category?.isSupported === false);
}

function isStandardParentId(id) {
  return Number.isFinite(id) && id >= 1000 && id <= 8999 && id % 1000 === 0;
}

function normalizeLeafOnly(ids, categories) {
  const next = new Set(ids);
  const byId = new Map(
    (Array.isArray(categories) ? categories : [])
      .map((cat) => [Number(cat?.id), cat])
      .filter(([id]) => Number.isFinite(id) && id > 0)
  );

  for (const id of Array.from(next)) {
    const cat = byId.get(id);
    const isStandard = cat?.isStandard === true || (id >= 1000 && id <= 8999);
    if (!isStandard || id % 1000 === 0) continue;
    next.delete(Math.floor(id / 1000) * 1000);
  }

  return next;
}

export default function FlatCategorySelector({ categories, selectedIds, onToggleId, onSetIds }) {
  const list = Array.isArray(categories) ? categories : [];

  const standardCategories = useMemo(
    () => list.filter((cat) => cat?.isStandard === true && isSelectable(cat)),
    [list]
  );

  const specificCategories = useMemo(
    () => list.filter((cat) => cat?.isStandard !== true && isSelectable(cat)),
    [list]
  );

  const standardGroups = useMemo(() => {
    const map = new Map();
    const sorted = [...standardCategories].sort((a, b) => Number(a.id) - Number(b.id));

    for (const cat of sorted) {
      const id = Number(cat?.id);
      if (!Number.isFinite(id)) continue;
      const base = Math.floor(id / 1000) * 1000;
      if (!map.has(base)) map.set(base, []);
      map.get(base).push(cat);
    }

    return Array.from(map.entries())
      .sort((a, b) => a[0] - b[0])
      .map(([base, cats]) => {
        const parent = cats.find((c) => Number(c.id) === base) || null;
        const children = cats.filter((c) => Number(c.id) !== base);
        const items = parent ? [parent, ...children] : cats;
        return { base, items };
      });
  }, [standardCategories]);

  const selectableStandardIds = useMemo(
    () => new Set(standardCategories.map((cat) => Number(cat.id))),
    [standardCategories]
  );

  const selectableSpecificIds = useMemo(
    () => new Set(specificCategories.map((cat) => Number(cat.id))),
    [specificCategories]
  );

  const normalizedStandardIds = useMemo(
    () => normalizeLeafOnly(selectableStandardIds, standardCategories),
    [selectableStandardIds, standardCategories]
  );

  const allStandardSelected =
    normalizedStandardIds.size > 0 &&
    Array.from(normalizedStandardIds).every((id) => selectedIds.has(id));

  const allSpecificSelected =
    selectableSpecificIds.size > 0 &&
    Array.from(selectableSpecificIds).every((id) => selectedIds.has(id));

  function toggleAllStandard() {
    if (normalizedStandardIds.size === 0) return;
    const next = new Set(selectedIds);
    if (allStandardSelected) {
      normalizedStandardIds.forEach((id) => next.delete(id));
    } else {
      normalizedStandardIds.forEach((id) => next.add(id));
    }
    onSetIds(next);
  }

  function toggleAllSpecific() {
    if (selectableSpecificIds.size === 0) return;
    const next = new Set(selectedIds);
    if (allSpecificSelected) {
      selectableSpecificIds.forEach((id) => next.delete(id));
    } else {
      selectableSpecificIds.forEach((id) => next.add(id));
    }
    onSetIds(next);
  }

  if (list.length === 0) {
    return <div className="muted">Aucune catégorie disponible.</div>;
  }

  return (
    <div className="flat-category-selector">
      <div className="flat-category-selector__grid">
        <div className="flat-category-selector__col">
          <div className="flat-category-selector__header">
            <span className="category-pill">Standard</span>
            <button
              type="button"
              className="type-chip"
              onClick={toggleAllStandard}
              disabled={normalizedStandardIds.size === 0}
            >
              <span className="type-chip__label">
                {allStandardSelected ? "Tout Désélectionner" : "Tout sélectionner"}
              </span>
            </button>
          </div>
          <div className="category-picker">
            {standardGroups.length === 0 && (
              <div className="muted">Aucune catégorie standard.</div>
            )}
            {standardGroups.map((group, index) => (
              <div
                key={group.base}
                className={`flat-category-selector__std-group${index > 0 ? " flat-category-selector__std-group--with-separator" : ""}`}
              >
                {group.items.map((cat) => {
                  const id = Number(cat?.id);
                  const isParent = isStandardParentId(id);
                  return (
                    <div
                      key={id}
                      className={`category-row ${isParent ? "category-row--parent" : "category-row--child"}`}
                    >
                      <ToggleSwitch
                        checked={selectedIds.has(id)}
                        onIonChange={(e) => onToggleId(id, e.detail.checked)}
                        className="settings-toggle settings-toggle--sm"
                      />
                      <span className="category-id">{id}</span>
                      <span className="category-name">{cat?.name || `Cat ${id}`}</span>
                      {isParent && <span className="category-pill category-pill--parent">Parent</span>}
                    </div>
                  );
                })}
              </div>
            ))}
          </div>
        </div>

        <div className="flat-category-selector__col">
          <div className="flat-category-selector__header">
            <span className="category-pill">Spécifiques indexeur</span>
            <button
              type="button"
              className="type-chip"
              onClick={toggleAllSpecific}
              disabled={selectableSpecificIds.size === 0}
            >
              <span className="type-chip__label">
                {allSpecificSelected ? "Tout Désélectionner" : "Tout sélectionner"}
              </span>
            </button>
          </div>
          <div className="category-picker">
            {specificCategories.length === 0 && (
              <div className="muted">Aucune catégorie spécifique.</div>
            )}
            {specificCategories.map((cat) => {
              const id = Number(cat?.id);
              return (
                <div key={id} className="category-row">
                  <ToggleSwitch
                    checked={selectedIds.has(id)}
                    onIonChange={(e) => onToggleId(id, e.detail.checked)}
                    className="settings-toggle settings-toggle--sm"
                  />
                  <span className="category-id">{id}</span>
                  <span className="category-name">{cat?.name || `Cat ${id}`}</span>
                </div>
              );
            })}
          </div>
        </div>
      </div>
    </div>
  );
}
