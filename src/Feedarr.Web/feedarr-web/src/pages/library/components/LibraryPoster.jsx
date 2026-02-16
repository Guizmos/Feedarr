import React, { useMemo } from "react";
import { CheckCircle, Image, ImageOff } from "lucide-react";
import usePosterRetryOn404 from "../../../hooks/usePosterRetryOn404.js";
import { buildIndexerPillStyle } from "../../../utils/sourceColors.js";
import { getAppLabel, normalizeRequestMode } from "../../../utils/appTypes.js";
import AppIcon from "../../../ui/AppIcon.jsx";

function PosterViewCard({
  item,
  onOpen,
  selectionMode,
  selected,
  onToggleSelect,
  onRename,
  indexerLabel,
  indexerColor,
  showIndexerPill,
  integrationMode,
  arrStatus,
}) {
  const mode = normalizeRequestMode(integrationMode);

  const displayTitle = useMemo(
    () => (item.titleClean?.trim() ? item.titleClean : item.title),
    [item.titleClean, item.title]
  );

  const basePosterUrl = item.posterUrl || (item.id ? `/api/posters/release/${item.id}` : "");
  const {
    posterSrc,
    imgError,
    handleImageError,
    handleImageLoad,
  } = usePosterRetryOn404(item.id, basePosterUrl);

  const indexer = String(indexerLabel || "").trim();
  const showIndexer = showIndexerPill && indexer;
  const indexerClass = getIndexerClass(indexer);
  const indexerStyle = useMemo(
    () => buildIndexerPillStyle(indexerColor),
    [indexerColor]
  );

  const lastError = String(item?.posterLastError || "").toLowerCase();
  const hadError = !!lastError && lastError !== "pending";
  const hasPoster = !!item?.posterUrl && !!posterSrc && !imgError;
  const isPending = !hasPoster && lastError === "pending";
  const isFailed = !hasPoster && hadError;
  const isEmpty = !hasPoster && !isPending && !isFailed;

  // Resolve arr badge
  let arrType = null;
  let arrUrl = null;
  if (!selectionMode && arrStatus) {
    if (mode === "overseerr" && arrStatus.inOverseerr) {
      arrType = "overseerr";
      arrUrl = arrStatus.overseerrUrl || null;
    } else if (mode === "jellyseerr" && arrStatus.inJellyseerr) {
      arrType = "jellyseerr";
      arrUrl = arrStatus.jellyseerrUrl || null;
    } else if (mode === "seer" && arrStatus.inSeer) {
      arrType = "seer";
      arrUrl = arrStatus.seerUrl || null;
    } else if (mode === "arr" && (arrStatus.inSonarr || arrStatus.inRadarr)) {
      const isSonarr = !!arrStatus.inSonarr;
      arrType = isSonarr ? "sonarr" : "radarr";
      arrUrl = isSonarr ? arrStatus.sonarrUrl || null : arrStatus.radarrUrl || null;
    }
  }

  return (
    <div
      className={"posterViewCard" + (selected ? " posterViewCard--selected" : "")}
      onClick={() => {
        if (selectionMode) onToggleSelect?.(item);
        else onOpen?.(item);
      }}
      style={selectionMode || onOpen ? { cursor: "pointer" } : undefined}
      title={selectionMode ? "Cliquer pour sélectionner" : displayTitle || ""}
    >
      {hasPoster ? (
        <img
          src={posterSrc}
          alt={displayTitle || ""}
          loading="lazy"
          onError={handleImageError}
          onLoad={handleImageLoad}
        />
      ) : (
        <div className="posterFallback">
          {isPending ? (
            <div className="posterLoader" />
          ) : isFailed ? (
            <ImageOff className="posterFallback__icon" />
          ) : isEmpty ? (
            <Image className="posterFallback__icon" />
          ) : null}
        </div>
      )}

      {/* Indexer badge - top left */}
      {showIndexer ? (
        <div className="posterIndexerPill posterIndexerPill--left">
          <span
            className={`banner-pill banner-pill--indexer${indexerClass ? ` ${indexerClass}` : ""}`}
            style={indexerStyle || undefined}
          >
            {indexer}
          </span>
        </div>
      ) : null}

      {/* Arr badge - bottom left */}
      {arrType ? (() => {
        const arrLabel = getAppLabel(arrType);
        const cls = `posterArrBadge posterArrBadge--${arrType}` + (arrUrl ? " posterArrBadge--link" : "");
        return arrUrl ? (
          <a
            href={arrUrl}
            target="_blank"
            rel="noopener noreferrer"
            className={cls}
            onClick={(e) => e.stopPropagation()}
            title={`Ouvrir dans ${arrLabel}`}
          >
            <CheckCircle className="posterArrBadge__icon" />
            <span className="posterArrBadge__label">{arrLabel}</span>
          </a>
        ) : (
          <div className={cls}>
            <CheckCircle className="posterArrBadge__icon" />
            <span className="posterArrBadge__label">{arrLabel}</span>
          </div>
        );
      })() : null}

      {/* Rename button */}
      {!selectionMode && onRename && (
        <button
          type="button"
          className="posterRenameBtn"
          title="Renommer"
          onClick={(e) => {
            e.stopPropagation();
            onRename?.(item);
          }}
        >
          <AppIcon name="edit" />
        </button>
      )}

      {/* Selection badge */}
      {selectionMode && (
        <div className={"selectBadge" + (selected ? " on" : "")}>
          {selected ? <AppIcon name="check" /> : ""}
        </div>
      )}

      {/* Hover overlay */}
      <div className={`posterViewOverlay${arrType ? " posterViewOverlay--has-badge" : ""}`}>
        <div className="posterViewOverlay__title">{displayTitle}</div>
        <div className="posterViewOverlay__meta">
          <span>{item.date || "-"}</span>
        </div>
        <div className="posterViewOverlay__meta">
          <span>Download: {item.grabs ?? 0}</span>
        </div>
      </div>
    </div>
  );
}

function normalizeIndexerName(value) {
  return String(value || "").toUpperCase().replace(/[^A-Z0-9]/g, "");
}

function getIndexerClass(value) {
  const key = normalizeIndexerName(value);
  if (key === "YGEGE") return "banner-pill--indexer-ygege";
  if (key === "C411") return "banner-pill--indexer-c411";
  if (key === "TOS") return "banner-pill--indexer-tos";
  if (key === "LACALE") return "banner-pill--indexer-lacale";
  return "";
}

export default function LibraryPoster({
  items,
  onOpen,
  selectionMode,
  selectedIds,
  onToggleSelect,
  onRename,
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
        '--card-scale': cardSize / 180,
      }
    : undefined;

  return (
    <div className="grid grid--poster" style={gridStyle}>
      {items.map((it) => (
        <PosterViewCard
          key={it.id}
          item={it}
          onOpen={onOpen}
          selectionMode={selectionMode}
          selected={selectedIds.has(it.id)}
          onToggleSelect={onToggleSelect}
          onRename={onRename}
          indexerLabel={sourceNameById.get(Number(it.sourceId)) || ""}
          indexerColor={sourceColorById.get(Number(it.sourceId)) || null}
          showIndexerPill={!sourceId}
          integrationMode={integrationMode}
          arrStatus={arrStatusMap[it.id]}
        />
      ))}
    </div>
  );
}
