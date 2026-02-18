import React from "react";
import SubAction from "../../../ui/SubAction.jsx";
import AppIcon from "../../../ui/AppIcon.jsx";

/**
 * Composant de dropdown pour la subbar
 */
function SubSelectIcon({ icon, label, value, onChange, children, title, active, className = "" }) {
  return (
    <div className={`subdropdown${active ? " is-active" : ""}${className ? ` ${className}` : ""}`} title={title || label}>
      {icon ? (
        <AppIcon name={icon} className="subdropdown__icon" />
      ) : null}
      <span className="subdropdown__label">{label}</span>
      <span className="subdropdown__caret">▾</span>
      <select
        className="subdropdown__select"
        value={value}
        onChange={onChange}
        aria-label={label}
      >
        {children}
      </select>
    </div>
  );
}

/**
 * Contenu de la subbar de la bibliothèque
 */
export default function LibrarySubbar({
  // Selection
  selectionMode,
  selectedIds,
  onToggleSelectionMode,
  onSelectAll,
  // Bulk actions
  posterAutoLoading,
  onBulkFetchPosters,
  onOpenManualPoster,
  onBulkSetSeen,
  // Filters
  sortBy,
  maxAgeDays,
  setMaxAgeDays,
  seen,
  setSeen,
  applicationId,
  setApplicationId,
  quality,
  setQuality,
  qualityOptions,
  filtersOpen,
  setFiltersOpen,
  installedApps,
  // Sources
  sources,
  enabledSources,
  sourceId,
  setSourceId,
  // Categories
  uiSettings,
  categoryId,
  setCategoryId,
  categoriesForDropdown,
  // Sort & View
  setSortBy,
  viewMode,
  setViewMode,
  viewOptions,
  // Limit
  limit,
  setLimit,
  // Defaults (from settings)
  defaultSort = "date",
  defaultMaxAgeDays = "",
  defaultLimit = 100,
}) {
  const hasInstalledApps = (installedApps || []).length > 0;
  const showFilterbar = !selectionMode && filtersOpen;

  return (
    <div className="library-subbar-stack">
      <div className="library-subbar-main">
        <SubAction
          icon={selectionMode ? "close" : "check_circle"}
          label={selectionMode ? "Cancel" : "Select"}
          active={selectionMode}
          className="subaction--select"
          onClick={onToggleSelectionMode}
        />
        {!selectionMode && <div className="subdivider library-subbar__select-mobile-sep" />}

        {selectionMode && selectedIds.size > 0 && (
          <>
            <div className="subaction-group subaction-group--select">
              <SubAction
                icon={posterAutoLoading ? "progress_activity" : "image"}
                label="Poster Auto"
                onClick={onBulkFetchPosters}
                className="subaction--select"
                disabled={posterAutoLoading}
              />
              <SubAction
                icon="search"
                label="Poster Manuel"
                onClick={onOpenManualPoster}
                className="subaction--select"
                disabled={selectedIds.size !== 1}
                title={selectedIds.size !== 1 ? "Sélectionner une seule carte" : "Poster manuel"}
              />
            </div>
            <div className="subaction-group subaction-group--select">
              <SubAction
                icon="visibility"
                label="Seen"
                onClick={() => onBulkSetSeen(true)}
                className="subaction--select"
              />
              <SubAction
                icon="visibility_off"
                label="Unseen"
                onClick={() => onBulkSetSeen(false)}
                className="subaction--select"
              />
            </div>
            <div className="subdivider library-subbar__selection-sep" />
          </>
        )}

        {selectionMode && (
          <SubAction
            icon="select_all"
            label="Select all"
            onClick={onSelectAll}
            className="subaction--select"
          />
        )}

        <div className="subspacer" />

        {!selectionMode && (
          <SubAction
            icon="filter_list"
            label="Filtre"
            active={filtersOpen}
            onClick={() => setFiltersOpen((prev) => !prev)}
          />
        )}

        {/* TRI */}
        <SubSelectIcon
          icon="sort"
          label="Tri"
          value={sortBy}
          active={sortBy !== defaultSort}
          onChange={(e) => setSortBy(e.target.value)}
          title="Tri"
        >
          <option value="date">Date</option>
          <option value="seeders">Seeders</option>
          <option value="downloads">Téléchargé</option>
        </SubSelectIcon>

        {/* VIEW */}
        <SubSelectIcon
          icon="view_module"
          label="Vue"
          value={viewMode}
          onChange={(e) => setViewMode(e.target.value)}
          title="Vue"
        >
          {viewOptions.map((opt) => (
            <option key={opt.value} value={opt.value}>
              {opt.label}
            </option>
          ))}
        </SubSelectIcon>

        {/* LIMIT */}
        <SubSelectIcon
          icon="format_list_numbered"
          label="Limit"
          value={limit}
          active={String(limit) !== String(defaultLimit)}
          onChange={(e) => {
            const next = e.target.value;
            setLimit(next === "all" ? "all" : Number(next));
          }}
          title="Limit"
        >
          <option value={50}>50</option>
          <option value={100}>100</option>
          <option value={200}>200</option>
          <option value={500}>500</option>
          <option value="all">Tous</option>
        </SubSelectIcon>
      </div>

      {showFilterbar && (
        <div className="library-filterbar">
          <SubSelectIcon
            icon="event"
            label="Date"
            value={maxAgeDays}
            active={String(maxAgeDays || "") !== String(defaultMaxAgeDays ?? "")}
            onChange={(e) => setMaxAgeDays(e.target.value)}
            title="Date"
          >
            <option value="">Tous</option>
            <option value="1">1 jour</option>
            <option value="2">2 jours</option>
            <option value="3">3 jours</option>
            <option value="7">7 jours</option>
            <option value="15">15 jours</option>
            <option value="30">30 jours</option>
          </SubSelectIcon>

          <div className="subspacer" />

          {/* SOURCE */}
          {sources.length > 0 ? (
            <SubSelectIcon
              icon="storage"
              label="Source"
              value={sourceId}
              active={!!sourceId}
              onChange={(e) => setSourceId(e.target.value)}
            >
              <option value="">Toutes sources</option>
              {enabledSources.map((s) => {
                const id = s.id ?? s.sourceId;
                const name = s.name ?? s.title ?? `Source ${id}`;
                return (
                  <option key={id} value={String(id)}>
                    {name}
                  </option>
                );
              })}
            </SubSelectIcon>
          ) : (
            <div className="subsearch">
              <span style={{ opacity: 0.75 }}>#</span>
              <input
                value={sourceId}
                onChange={(e) => setSourceId(e.target.value)}
                placeholder="SourceId"
                aria-label="Filtrer par identifiant de source"
                style={{ width: 110 }}
              />
            </div>
          )}

          {/* CATEGORY */}
          {uiSettings?.showCategories !== false && (
            <SubSelectIcon
              icon="category"
              label="Catégorie"
              value={categoryId}
              active={!!categoryId}
              onChange={(e) => setCategoryId(e.target.value)}
            >
              <option value="">Toutes catégories</option>
              {categoriesForDropdown.map((c) => (
                <option key={c.key} value={c.key}>
                  {c.label}
                </option>
              ))}
            </SubSelectIcon>
          )}

          {/* APPLICATION */}
          {hasInstalledApps && (
            <SubSelectIcon
              icon="video_library"
              label="Application"
              value={applicationId}
              active={!!applicationId}
              className="subdropdown--wide"
              onChange={(e) => setApplicationId(e.target.value)}
            >
              <option value="">Toutes apps</option>
              <option value="__hide_apps__">Masquer apps</option>
              {(installedApps || []).map((app) => {
                const id = app.id ?? app.appId;
                const type = String(app.type || "app").toLowerCase();
                const name = app.name || app.title || `${type} ${id}`;
                return (
                  <option key={String(id)} value={String(id)}>
                    {name}
                  </option>
                );
              })}
            </SubSelectIcon>
          )}

          {/* SEEN */}
          <SubSelectIcon
            icon="visibility"
            label="Vu"
            value={seen}
            active={!!seen}
            onChange={(e) => setSeen(e.target.value)}
          >
            <option value="">Tous</option>
            <option value="1">Vu</option>
            <option value="0">Pas vu</option>
          </SubSelectIcon>

          {/* QUALITY */}
          <SubSelectIcon
            icon="military_tech"
            label="Qualité"
            value={quality}
            active={!!quality}
            onChange={(e) => setQuality(e.target.value)}
          >
            <option value="">Toutes qualités</option>
            {(qualityOptions || []).map((value) => (
              <option key={value} value={value}>
                {value}
              </option>
            ))}
          </SubSelectIcon>
        </div>
      )}
    </div>
  );
}
