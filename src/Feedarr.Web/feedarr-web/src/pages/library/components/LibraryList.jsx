import React from "react";
import { fmtSizeGo, formatSeasonEpisode } from "../utils/formatters.js";
import { isSeriesItem, getIndexerClass, getResolutionClass } from "../utils/helpers.js";
import AppIcon from "../../../ui/AppIcon.jsx";
import { buildIndexerPillStyle } from "../../../utils/sourceColors.js";

/**
 * Vue liste de la bibliothèque (tableau)
 */
export default function LibraryList({
  items,
  selectionMode,
  selectedIds,
  onToggleSelect,
  onOpen,
  onRename,
  listSortBy,
  listSortDir,
  onToggleSort,
  sourceNameById,
  sourceColorById,
  getUnifiedLabel,
}) {
  const sortClass = (key) =>
    `library-sort ${listSortBy === key ? "is-active" : ""}${listSortBy === key && listSortDir === "desc" ? " is-desc" : ""}`;

  return (
    <div className="library-table">
      <div className="library-thead">
        <div className="library-cell library-cell--select" />
        <button type="button" className={sortClass("title")} onClick={() => onToggleSort("title")}>
          <span className="library-sort__label">Titre</span>
          <AppIcon
            name={listSortDir === "desc" ? "arrow_downward" : "arrow_upward"}
            className="library-sort__icon"
          />
        </button>
        <button type="button" className={sortClass("category")} onClick={() => onToggleSort("category")}>
          <span className="library-sort__label">Catégorie</span>
          <AppIcon
            name={listSortDir === "desc" ? "arrow_downward" : "arrow_upward"}
            className="library-sort__icon"
          />
        </button>
        <button type="button" className={sortClass("episode")} onClick={() => onToggleSort("episode")}>
          <span className="library-sort__label">Episode</span>
          <AppIcon
            name={listSortDir === "desc" ? "arrow_downward" : "arrow_upward"}
            className="library-sort__icon"
          />
        </button>
        <button type="button" className={sortClass("quality")} onClick={() => onToggleSort("quality")}>
          <span className="library-sort__label">Qualité</span>
          <AppIcon
            name={listSortDir === "desc" ? "arrow_downward" : "arrow_upward"}
            className="library-sort__icon"
          />
        </button>
        <button type="button" className={sortClass("codec")} onClick={() => onToggleSort("codec")}>
          <span className="library-sort__label">Codec</span>
          <AppIcon
            name={listSortDir === "desc" ? "arrow_downward" : "arrow_upward"}
            className="library-sort__icon"
          />
        </button>
        <button type="button" className={sortClass("size")} onClick={() => onToggleSort("size")}>
          <span className="library-sort__label">Taille</span>
          <AppIcon
            name={listSortDir === "desc" ? "arrow_downward" : "arrow_upward"}
            className="library-sort__icon"
          />
        </button>
        <button type="button" className={sortClass("date")} onClick={() => onToggleSort("date")}>
          <span className="library-sort__label">Date d'ajout</span>
          <AppIcon
            name={listSortDir === "desc" ? "arrow_downward" : "arrow_upward"}
            className="library-sort__icon"
          />
        </button>
        <button type="button" className={sortClass("seeders")} onClick={() => onToggleSort("seeders")}>
          <span className="library-sort__label">Seeders</span>
          <AppIcon
            name={listSortDir === "desc" ? "arrow_downward" : "arrow_upward"}
            className="library-sort__icon"
          />
        </button>
        <button type="button" className={sortClass("downloads")} onClick={() => onToggleSort("downloads")}>
          <span className="library-sort__label">Téléchargé</span>
          <AppIcon
            name={listSortDir === "desc" ? "arrow_downward" : "arrow_upward"}
            className="library-sort__icon"
          />
        </button>
        <button type="button" className={sortClass("source")} onClick={() => onToggleSort("source")}>
          <span className="library-sort__label">Indexeur</span>
          <AppIcon
            name={listSortDir === "desc" ? "arrow_downward" : "arrow_upward"}
            className="library-sort__icon"
          />
        </button>
        <div className="library-cell library-cell--edit" />
      </div>

      {items.map((it) => (
        <div
          key={it.id}
          className={`library-trow${selectedIds.has(it.id) ? " is-selected" : ""}`}
          onClick={() => {
            if (selectionMode) onToggleSelect(it);
            else onOpen(it);
          }}
        >
          <div className="library-cell library-cell--select">
            {selectionMode && (
              <button
                type="button"
                className={`list-select${selectedIds.has(it.id) ? " is-on" : ""}`}
                onClick={(e) => {
                  e.stopPropagation();
                  onToggleSelect(it);
                }}
                title={selectedIds.has(it.id) ? "Retirer" : "Selectionner"}
              >
                <AppIcon name={selectedIds.has(it.id) ? "check_box" : "check_box_outline_blank"} />
              </button>
            )}
          </div>
          <div className="library-cell">{it.titleClean || it.title || "-"}</div>
          <div className="library-cell">
            {(() => {
              const label = getUnifiedLabel(it);
              if (!label) return "-";
              const key = it?.unifiedCategoryKey || "unknown";
              return <span className={`cat-bubble cat-bubble--${key}`}>{label}</span>;
            })()}
          </div>
          <div className="library-cell">
            {isSeriesItem(it) ? formatSeasonEpisode(it) || "-" : "-"}
          </div>
          <div className="library-cell">
            {it.resolution ? (
              <div className="library-quality">
                <span className={`banner-pill ${getResolutionClass(it.resolution)}`}>
                  {it.resolution}
                </span>
              </div>
            ) : "-"}
          </div>
          <div className="library-cell">
            {it.codec ? (
              <div className="library-quality">
                <span className="banner-pill">{it.codec}</span>
              </div>
            ) : "-"}
          </div>
          <div className="library-cell">{fmtSizeGo(it.sizeBytes || it.size_bytes || 0) || "-"}</div>
          <div className="library-cell">{it.date || "-"}</div>
          <div className="library-cell">{it.seeders ?? "-"}</div>
          <div className="library-cell">{it.grabs ?? "-"}</div>
          <div className="library-cell">
            {(() => {
              const indexerName = sourceNameById.get(Number(it.sourceId)) || "";
              if (!indexerName) return "-";
              const indexerClass = getIndexerClass(indexerName);
              const indexerStyle = buildIndexerPillStyle(sourceColorById?.get(Number(it.sourceId)) || null);
              return (
                <span
                  className={`banner-pill banner-pill--indexer${indexerClass ? ` ${indexerClass}` : ""}`}
                  style={indexerStyle || undefined}
                >
                  {indexerName}
                </span>
              );
            })()}
          </div>
          <div className="library-cell library-cell--edit">
            {!selectionMode && (
              <button
                type="button"
                className="list-edit"
                title="Renommer"
                onClick={(e) => {
                  e.stopPropagation();
                  onRename(it);
                }}
              >
                <AppIcon name="edit" />
              </button>
            )}
          </div>
        </div>
      ))}
    </div>
  );
}
