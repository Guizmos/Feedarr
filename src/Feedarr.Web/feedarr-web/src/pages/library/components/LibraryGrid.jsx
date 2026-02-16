import React from "react";
import PosterCard from "../../../ui/PosterCard.jsx";

/**
 * Vue grille de la bibliothèque (cartes poster)
 */
export default function LibraryGrid({
  items,
  onDownload,
  onOpen,
  selectionMode,
  selectedIds,
  onToggleSelect,
  onRename,
  sortBy,
  sourceNameById,
  sourceColorById,
  sourceId,
  arrStatusMap,
  integrationMode,
  cardSize,
}) {
  const gridStyle = cardSize
    ? {
        gridTemplateColumns: `repeat(auto-fill, minmax(${Math.round(cardSize)}px, 1fr))`,
        '--card-scale': cardSize / 190,
      }
    : undefined;

  return (
    <div className="grid" style={gridStyle}>
      {items.map((it) => (
        <PosterCard
          key={it.id}
          item={it}
          onDownload={onDownload}
          onOpen={onOpen}
          selectionMode={selectionMode}
          selected={selectedIds.has(it.id)}
          onToggleSelect={onToggleSelect}
          onRename={onRename}
          sortBy={sortBy}
          indexerLabel={sourceNameById.get(Number(it.sourceId)) || ""}
          indexerColor={sourceColorById.get(Number(it.sourceId)) || null}
          showIndexerPill={!sourceId}
          indexerPillPosition="left"
          integrationMode={integrationMode}
          arrStatus={arrStatusMap[it.id]}
        />
      ))}
    </div>
  );
}
