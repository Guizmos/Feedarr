import React, { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { apiGet, apiPost } from "../api/client.js";
import Loader from "../ui/Loader.jsx";
import { useSubbarSetter } from "../layout/useSubbar.js";
import ReleaseModal from "../ui/ReleaseModal.jsx";
import Modal from "../ui/Modal.jsx";
import { openDownloadPath } from "../utils/downloadPath.js";
import { executeAsync } from "../utils/executeAsync.js";
import { fmtBytes, fmtDateFromTs } from "../utils/formatters.js";
import TopReleasesSubSelectIcon from "./topReleases/components/TopReleasesSubSelectIcon.jsx";
import {
  TopReleasesBannerSection,
  TopReleasesGridSection,
  TopReleasesListSection,
  TopReleasesPosterSection,
} from "./topReleases/components/TopReleasesSections.jsx";

const CATEGORY_LABELS = {
  films: "Films",
  series: "Series TV",
  anime: "Anime",
  games: "Jeux PC",
  shows: "Emissions",
  spectacle: "Spectacle",
};

const viewOptions = [
  { value: "grid", label: "Cartes" },
  { value: "poster", label: "Poster" },
  { value: "banner", label: "Banner" },
  { value: "list", label: "Liste" },
];

const sortOptions = [
  { value: "seeders", label: "Seeders" },
  { value: "rating", label: "Note" },
  { value: "downloads", label: "Téléchargé" },
  { value: "recent", label: "Récent" },
];

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
  if (!Number.isFinite(v) || v <= 0) return "";
  const count = Number(it?.ratingVotes);
  const suffix = Number.isFinite(count) && count > 0 ? ` (${count})` : "";
  return `${v.toFixed(1)}/10${suffix}`;
}

function getSortLabel(sortBy) {
  const found = sortOptions.find((opt) => opt.value === sortBy);
  return found?.label || "Seeders";
}

function getSortSummaryLabel(sortBy) {
  if (sortBy === "rating") return "les mieux notés";
  if (sortBy === "downloads") return "les plus téléchargés";
  if (sortBy === "recent") return "les plus récents";
  return "les plus seedés";
}

function isSeriesItem(it) {
  const raw = String(it?.mediaType || it?.unifiedCategoryKey || "").toLowerCase();
  if (["tv", "series", "serie", "tv_series", "series_tv", "seriestv"].includes(raw)) return true;
  return String(it?.unifiedCategoryKey || "").toLowerCase() === "series";
}

function isGameCategoryKey(key) {
  return String(key || "").toLowerCase() === "games";
}

function isGameMediaType(mediaType) {
  return String(mediaType || "").toLowerCase() === "game";
}

function hasDetailsPayload(it) {
  return Boolean(
    it?.overview ||
    it?.releaseDate ||
    it?.rating ||
    it?.ratingVotes ||
    it?.genres
  );
}

export default function TopReleases() {
  const setContent = useSubbarSetter();
  const [loading, setLoading] = useState(true);
  const [err, setErr] = useState("");
  const [globalTop, setGlobalTop] = useState([]);
  const [topByCategory, setTopByCategory] = useState({});
  const [sourceId, setSourceId] = useState("");
  const [sources, setSources] = useState([]);
  const [viewMode, setViewMode] = useState("grid");
  const [sortBy, setSortBy] = useState("seeders");
  const [selectedItem, setSelectedItem] = useState(null);
  const [renameOpen, setRenameOpen] = useState(false);
  const [renameValue, setRenameValue] = useState("");
  const [renameTarget, setRenameTarget] = useState(null);
  const [renameOriginal, setRenameOriginal] = useState("");
  const [top5AnimKey, setTop5AnimKey] = useState(0);
  const gameDetailsFetchRef = useRef(new Set());

  const enabledSources = useMemo(
    () => (sources || []).filter((s) => Number(s.enabled ?? 1) === 1),
    [sources]
  );

  const sourceNameById = useMemo(() => {
    const map = new Map();
    (sources || []).forEach((s) => {
      const id = Number(s.id ?? s.sourceId);
      if (!Number.isFinite(id)) return;
      map.set(id, s.name ?? s.title ?? `Source ${id}`);
    });
    return map;
  }, [sources]);

  const load = useCallback(async () => {
    setLoading(true);
    await executeAsync(async () => {
      const q = new URLSearchParams();
      if (sourceId) q.set("sourceId", sourceId);
      if (sortBy) q.set("sortBy", sortBy);
      const params = q.toString();
      const data = await apiGet(`/api/feed/top${params ? `?${params}` : ""}`);
      // Map items to include formatted size and date
      const mapItems = (items) =>
        (items || []).map((it) => ({
          ...it,
          size: fmtBytes(it.sizeBytes),
          date: fmtDateFromTs(it.publishedAt),
        }));
      setGlobalTop(mapItems(data?.global || []));
      const byCategory = {};
      Object.entries(data?.byCategory || {}).forEach(([key, items]) => {
        byCategory[key] = mapItems(items);
      });
      setTopByCategory(byCategory);
    }, {
      context: "Failed to load TopReleases feed",
      clearError: () => setErr(""),
      setError: setErr,
      fallbackMessage: "Erreur lors du chargement des tops",
      onFinally: () => setLoading(false),
    });
  }, [sourceId, sortBy]);

  useEffect(() => {
    load();
  }, [load]);

  useEffect(() => {
    if (viewMode !== "grid" || globalTop.length === 0) return;
    const timer = setTimeout(() => setTop5AnimKey((prev) => prev + 1), 140);
    return () => clearTimeout(timer);
  }, [viewMode, globalTop.length]);

  function download(it) {
    const path =
      it.downloadPath || (it.id ? `/api/releases/${it.id}/download` : "");
    if (!path) return;
    openDownloadPath(path, { onError: setErr });
  }

  function openDetails(it) {
    if (!it) return;
    setSelectedItem(it);
    const isGame =
      isGameCategoryKey(it.unifiedCategoryKey) || isGameMediaType(it.mediaType);
    if (isGame && !hasDetailsPayload(it)) {
      fetchGameDetails(it);
    }
  }

  function closeDetails() {
    setSelectedItem(null);
  }

  async function fetchGameDetails(it) {
    if (!it?.id) return;
    if (gameDetailsFetchRef.current.has(it.id)) return;

    gameDetailsFetchRef.current.add(it.id);

    await executeAsync(async () => {
      const res = await apiPost(`/api/releases/${it.id}/details/igdb`);
      const details = res?.details;
      if (!details) return;

      setGlobalTop((prev) =>
        prev.map((row) =>
          row.id === it.id
            ? { ...row, ...details }
            : row
        )
      );

      setTopByCategory((prev) => {
        const next = { ...prev };
        Object.keys(next).forEach((key) => {
          next[key] = next[key].map((row) =>
            row.id === it.id
              ? { ...row, ...details }
              : row
          );
        });
        return next;
      });

      setSelectedItem((prev) =>
        prev?.id === it.id
          ? { ...prev, ...details }
          : prev
      );
    }, {
      context: "Failed to fetch IGDB details in TopReleases",
      onFinally: () => {
        gameDetailsFetchRef.current.delete(it.id);
      },
    });
  }

  function renameRelease(it) {
    if (!it) return;
    const current = it.titleClean?.trim() ? it.titleClean : it.title;
    setRenameTarget(it);
    setRenameValue(current || "");
    setRenameOriginal(it.title || "");
    setRenameOpen(true);
  }

  function closeRename() {
    setRenameOpen(false);
    setRenameTarget(null);
    setRenameValue("");
    setRenameOriginal("");
  }

  async function saveRename(e) {
    e?.preventDefault?.();
    if (!renameTarget) return;
    const trimmed = renameValue.trim();
    if (!trimmed) return;

    try {
      setErr("");
      const res = await apiPost(`/api/releases/${renameTarget.id}/rename`, { title: trimmed });
      const newEntityId = res?.entityId ?? renameTarget.entityId ?? null;
      const posterUrl = res?.posterUrl ?? null;
      const posterUpdatedAtTs = res?.posterUpdatedAtTs ?? null;

      // Update globalTop
      setGlobalTop((prev) =>
        prev.map((row) =>
          row.id === renameTarget.id || (newEntityId && row.entityId === newEntityId)
            ? {
                ...row,
                ...(row.id === renameTarget.id
                  ? {
                      title: res?.title ?? trimmed,
                      titleClean: res?.titleClean ?? row.titleClean,
                      year: res?.year ?? row.year,
                      season: res?.season ?? row.season,
                      episode: res?.episode ?? row.episode,
                      resolution: res?.resolution ?? row.resolution,
                      source: res?.source ?? row.source,
                      codec: res?.codec ?? row.codec,
                      releaseGroup: res?.releaseGroup ?? row.releaseGroup,
                      mediaType: res?.mediaType ?? row.mediaType,
                      unifiedCategory: res?.unifiedCategory ?? row.unifiedCategory,
                      entityId: newEntityId ?? row.entityId,
                    }
                  : {}),
                posterUrl,
                posterUpdatedAtTs: posterUpdatedAtTs ?? row.posterUpdatedAtTs,
              }
            : row
        )
      );

      // Update topByCategory
      setTopByCategory((prev) => {
        const next = { ...prev };
        Object.keys(next).forEach((key) => {
          next[key] = next[key].map((row) =>
            row.id === renameTarget.id || (newEntityId && row.entityId === newEntityId)
              ? {
                  ...row,
                  ...(row.id === renameTarget.id
                    ? {
                        title: res?.title ?? trimmed,
                        titleClean: res?.titleClean ?? row.titleClean,
                        year: res?.year ?? row.year,
                        season: res?.season ?? row.season,
                        episode: res?.episode ?? row.episode,
                        resolution: res?.resolution ?? row.resolution,
                        source: res?.source ?? row.source,
                        codec: res?.codec ?? row.codec,
                        releaseGroup: res?.releaseGroup ?? row.releaseGroup,
                        mediaType: res?.mediaType ?? row.mediaType,
                        unifiedCategory: res?.unifiedCategory ?? row.unifiedCategory,
                        entityId: newEntityId ?? row.entityId,
                      }
                    : {}),
                  posterUrl,
                  posterUpdatedAtTs: posterUpdatedAtTs ?? row.posterUpdatedAtTs,
                }
              : row
          );
        });
        return next;
      });

      setSelectedItem((prev) =>
        prev?.id === renameTarget.id || (newEntityId && prev?.entityId === newEntityId)
          ? {
              ...prev,
              ...(prev?.id === renameTarget.id
                ? {
                    title: res?.title ?? trimmed,
                    titleClean: res?.titleClean ?? prev.titleClean,
                    year: res?.year ?? prev.year,
                    season: res?.season ?? prev.season,
                    episode: res?.episode ?? prev.episode,
                    resolution: res?.resolution ?? prev.resolution,
                    source: res?.source ?? prev.source,
                    codec: res?.codec ?? prev.codec,
                    releaseGroup: res?.releaseGroup ?? prev.releaseGroup,
                    mediaType: res?.mediaType ?? prev.mediaType,
                    unifiedCategory: res?.unifiedCategory ?? prev.unifiedCategory,
                    entityId: newEntityId ?? prev.entityId,
                  }
                : {}),
              posterUrl,
              posterUpdatedAtTs: posterUpdatedAtTs ?? prev?.posterUpdatedAtTs,
            }
          : prev
      );

      closeRename();
    } catch (e) {
      setErr(e?.message || "Erreur renommage");
    }
  }

  // Charger la liste des sources
  useEffect(() => {
    async function loadSources() {
      const data = await executeAsync(
        () => apiGet("/api/sources"),
        { context: "Failed to load sources in TopReleases" }
      );
      if (Array.isArray(data)) {
        setSources(data);
      } else {
        setSources([]);
      }
    }
    loadSources();
  }, []);

  // Subbar avec Source, Tri et Vue
  useEffect(() => {
    setContent(
      <>
        <div className="subspacer" />

        {/* SOURCE */}
        {sources.length > 0 ? (
          <TopReleasesSubSelectIcon
            icon="storage"
            label="Source"
            value={sourceId}
            active={!!sourceId}
            onChange={(e) => setSourceId(e.target.value)}
          >
            <option value="">Tous les indexeurs</option>
            {enabledSources.map((s) => {
              const id = s.id ?? s.sourceId;
              const name = s.name ?? s.title ?? `Source ${id}`;
              return (
                <option key={id} value={String(id)}>
                  {name}
                </option>
              );
            })}
          </TopReleasesSubSelectIcon>
        ) : null}

        {/* TRI */}
        <TopReleasesSubSelectIcon
          icon="sort"
          label="Tri"
          value={sortBy}
          active={sortBy !== "seeders"}
          onChange={(e) => setSortBy(e.target.value)}
          title="Tri"
        >
          {sortOptions.map((opt) => (
            <option key={opt.value} value={opt.value}>
              {opt.label}
            </option>
          ))}
        </TopReleasesSubSelectIcon>

        {/* VIEW */}
        <TopReleasesSubSelectIcon
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
        </TopReleasesSubSelectIcon>
      </>
    );

    return () => setContent(null);
  }, [setContent, sources, sourceId, enabledSources, sortBy, viewMode]);

  const selectedSourceName = useMemo(() => {
    if (!sourceId) return "";
    const selected = sources.find((s) => String(s.id) === sourceId || String(s.sourceId) === sourceId);
    return selected?.name || selected?.title || "";
  }, [sourceId, sources]);
  const sourceLabelSuffix = selectedSourceName ? ` (${selectedSourceName})` : " (Tous indexeurs)";
  const sortLabel = getSortLabel(sortBy);
  const sortSummaryLabel = getSortSummaryLabel(sortBy);

  if (loading) return <Loader />;
  if (err) return <div className="error">{err}</div>;

  return (
    <div className="page page--top24h">
      <div className="pagehead">
        <div>
          <h1>Top 24h</h1>
          <div className="muted">
            Top 5 des {sortSummaryLabel} - Résultats: {globalTop.length + Object.values(topByCategory).flat().length}
          </div>
        </div>
      </div>

      {viewMode === "grid" && (
        <>
          <TopReleasesGridSection
            items={globalTop}
            sectionTitle={`🏆 Top 5 Global (${sortLabel})`}
            showRank={true}
            rankColor="#ffd700"
            showIndexerPill={true}
            compact={false}
            isGlobalTop={true}
            top5AnimKey={top5AnimKey}
            sortBy={sortBy}
            sourceNameById={sourceNameById}
            onDownload={download}
            onOpen={openDetails}
          />
          {Object.entries(CATEGORY_LABELS).map(([key, label]) => {
            const items = topByCategory[key] || [];
            return (
              <TopReleasesGridSection
                key={key}
                items={items}
                sectionTitle={`📊 Top 5 - ${label}${sourceLabelSuffix} (${sortLabel})`}
                showRank={true}
                rankColor="#fff"
                showIndexerPill={!sourceId}
                compact={true}
                isGlobalTop={false}
                top5AnimKey={top5AnimKey}
                sortBy={sortBy}
                sourceNameById={sourceNameById}
                onDownload={download}
                onOpen={openDetails}
              />
            );
          })}
        </>
      )}

      {viewMode === "poster" && (
        <>
          <TopReleasesPosterSection
            items={globalTop}
            sectionTitle={`🏆 Top 5 Global (${sortLabel})`}
            showRank={true}
            rankColor="#ffd700"
            onOpen={openDetails}
          />
          {Object.entries(CATEGORY_LABELS).map(([key, label]) => {
            const items = topByCategory[key] || [];
            return (
              <TopReleasesPosterSection
                key={key}
                items={items}
                sectionTitle={`📊 Top 5 - ${label}${sourceLabelSuffix} (${sortLabel})`}
                showRank={true}
                rankColor="#fff"
                onOpen={openDetails}
              />
            );
          })}
        </>
      )}

      {viewMode === "banner" && (
        <>
          <TopReleasesBannerSection
            items={globalTop}
            sectionTitle={`🏆 Top 5 Global (${sortLabel})`}
            showRank={true}
            rankColor="#ffd700"
            sourceNameById={sourceNameById}
            onOpen={openDetails}
            formatRating={formatRating}
          />
          {Object.entries(CATEGORY_LABELS).map(([key, label]) => {
            const items = topByCategory[key] || [];
            return (
              <TopReleasesBannerSection
                key={key}
                items={items}
                sectionTitle={`📊 Top 5 - ${label}${sourceLabelSuffix} (${sortLabel})`}
                showRank={true}
                rankColor="#fff"
                sourceNameById={sourceNameById}
                onOpen={openDetails}
                formatRating={formatRating}
              />
            );
          })}
        </>
      )}

      {viewMode === "list" && (
        <>
          <TopReleasesListSection
            items={globalTop}
            sectionTitle={`🏆 Top 5 Global (${sortLabel})`}
            sourceNameById={sourceNameById}
            onOpen={openDetails}
            onRename={renameRelease}
            formatRating={formatRating}
            isSeriesItem={isSeriesItem}
          />
          {Object.entries(CATEGORY_LABELS).map(([key, label]) => {
            const items = topByCategory[key] || [];
            return (
              <TopReleasesListSection
                key={key}
                items={items}
                sectionTitle={`📊 Top 5 - ${label}${sourceLabelSuffix} (${sortLabel})`}
                sourceNameById={sourceNameById}
                onOpen={openDetails}
                onRename={renameRelease}
                formatRating={formatRating}
                isSeriesItem={isSeriesItem}
              />
            );
          })}
        </>
      )}

      <ReleaseModal
        open={!!selectedItem}
        item={selectedItem}
        onClose={closeDetails}
        onDownload={download}
        categoryLabel={
          selectedItem
            ? selectedItem.categoryName || selectedItem.unifiedCategoryLabel || ""
            : ""
        }
        indexerLabel={sourceNameById.get(Number(selectedItem?.sourceId)) || ""}
      />

      <Modal open={renameOpen} title="Renommer" onClose={closeRename} width={520}>
        <form onSubmit={saveRename} style={{ padding: 12 }}>
          <div className="field" style={{ marginBottom: 12 }}>
            <label className="muted">Titre original</label>
            <div style={{ padding: "4px 0", color: "var(--text-primary)" }}>
              {renameOriginal || "—"}
            </div>
          </div>
          <div className="field" style={{ marginBottom: 12 }}>
            <label className="muted">Nouveau titre</label>
            <input
              value={renameValue}
              onChange={(e) => setRenameValue(e.target.value)}
              placeholder="Titre"
              autoFocus
            />
          </div>
          <div className="formactions">
            <button className="btn" type="submit">
              Enregistrer
            </button>
            <button className="btn" type="button" onClick={closeRename}>
              Annuler
            </button>
          </div>
        </form>
      </Modal>
    </div>
  );
}
