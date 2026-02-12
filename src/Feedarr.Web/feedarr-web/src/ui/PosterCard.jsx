import React, { useMemo } from "react";
import usePosterRetryOn404 from "../hooks/usePosterRetryOn404.js";
import { CheckCircle, Image, ImageOff } from "lucide-react";
import { buildIndexerPillStyle } from "../utils/sourceColors.js";
import AppIcon from "./AppIcon.jsx";

export default function PosterCard({
  item,
  onOpen,
  selectionMode = false,
  selected = false,
  onToggleSelect,
  onRename,
  sortBy = "date",
  hideRename = false,
  indexerLabel,
  indexerColor,
  showIndexerPill = false,
  indexerPillPosition = "left",
  arrStatus = null, // { inSonarr, inRadarr }
}) {
  const seen = !!item.seen;

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

  return (
    <div
      className={"posterCard" + (selected ? " posterCard--selected" : "")}
      onClick={() => {
        if (selectionMode) onToggleSelect?.(item);
        else onOpen?.(item);
      }}
      style={selectionMode || onOpen ? { cursor: "pointer" } : undefined}
      title={selectionMode ? "Cliquer pour sélectionner" : "Ouvrir les détails"}
    >
      <div className="poster">
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

        {showIndexer ? (
          <div className={`posterIndexerPill posterIndexerPill--${indexerPillPosition === "right" ? "right" : "left"}`}>
            <span
              className={`banner-pill banner-pill--indexer${indexerClass ? ` ${indexerClass}` : ""}`}
              style={indexerStyle || undefined}
            >
              {indexer}
            </span>
          </div>
        ) : null}

        {/* Arr badge - shows if item is in Sonarr or Radarr */}
        {(arrStatus?.inSonarr || arrStatus?.inRadarr) && !selectionMode && (() => {
          const isSonarr = !!arrStatus.inSonarr;
          const arrUrl = isSonarr ? arrStatus.sonarrUrl : arrStatus.radarrUrl;
          const arrLabel = isSonarr ? "Sonarr" : "Radarr";
          const arrType = isSonarr ? "sonarr" : "radarr";

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
        })()}


        {!selectionMode && !hideRename && onRename && (
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

        {selectionMode && (
          <div className={"selectBadge" + (selected ? " on" : "")}>
            {selected ? <AppIcon name="check" /> : ""}
          </div>
        )}
      </div>

      <div className={"statusline" + (!seen ? " on" : "")} />

      <div className="posterMeta">
        <div className="posterTitle" title={item.title}>
          {displayTitle}
        </div>

        {sortBy === "seeders" ? (
          <>
            <div className="posterSub posterSubAlt">
              <span>Date: {item.date || "-"}</span>
            </div>
            <div className="posterSub">
              <span>Seeders: {item.seeders ?? "-"}</span>
            </div>
          </>
        ) : sortBy === "rating" ? (
          <>
            <div className="posterSub posterSubAlt">
              <span>Date: {item.date || "-"}</span>
            </div>
            <div className="posterSub">
              <span>Note: {formatRating(item)}</span>
            </div>
          </>
        ) : sortBy === "downloads" ? (
          <>
            <div className="posterSub posterSubAlt">
              <span>Date: {item.date || "-"}</span>
            </div>
            <div className="posterSub">
              <span>Téléchargé: {item.grabs ?? "-"}</span>
            </div>
          </>
        ) : sortBy === "recent" ? (
          <>
            <div className="posterSub posterSubAlt">
              <span>Seeders: {item.seeders ?? "-"}</span>
            </div>
            <div className="posterSub">
              <span>Date: {item.date || "-"}</span>
            </div>
          </>
        ) : (
          <>
            <div className="posterSub posterSubAlt">
              <span>Seeders: {item.seeders ?? "-"}</span>
            </div>
            <div className="posterSub">
              <span>Date: {item.date || "-"}</span>
            </div>
          </>
        )}

        {/* Actions retirees sur les cartes */}
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

function isGameItem(it) {
  const key = String(it?.unifiedCategoryKey || "").toLowerCase();
  const mediaType = String(it?.mediaType || "").toLowerCase();
  const provider = String(it?.detailsProvider || "").toLowerCase();
  return key === "games" || mediaType === "game" || provider === "igdb";
}

function normalizeRatingToTen(value, it) {
  const raw = Number(value);
  if (!Number.isFinite(raw) || raw <= 0) return null;
  if (isGameItem(it)) return raw > 10 ? raw / 10 : raw;
  return raw > 10 ? raw / 10 : raw;
}

function formatRating(it) {
  const v = normalizeRatingToTen(it?.rating, it);
  if (!Number.isFinite(v) || v <= 0) return "-";
  const count = Number(it?.ratingVotes);
  const suffix = Number.isFinite(count) && count > 0 ? ` (${count})` : "";
  return `${v.toFixed(1)}/10${suffix}`;
}
