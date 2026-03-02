import React, { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { Link } from "react-router-dom";
import { apiGet, apiPost } from "../api/client.js";
import Loader from "../ui/Loader.jsx";
import { useSubbarSetter } from "../layout/useSubbar.js";
import ReleaseModal from "../ui/ReleaseModal.jsx";
import Modal from "../ui/Modal.jsx";
import { openDownloadPath } from "../utils/downloadPath.js";
import { executeAsync } from "../utils/executeAsync.js";
import { fmtBytes, fmtDateFromTs } from "../utils/formatters.js";
import useArrApps from "../hooks/useArrApps.js";
import { normalizeRequestMode } from "../utils/appTypes.js";
import { getSourceColor } from "../utils/sourceColors.js";
import { getActiveUiLanguage } from "../app/locale.js";
import { normalizeCategoryGroupKey } from "../domain/categories/index.js";
import TopReleasesSubSelectIcon from "./topReleases/components/TopReleasesSubSelectIcon.jsx";
import {
  TopReleasesBannerSection,
  TopReleasesGridSection,
  TopReleasesListSection,
  TopReleasesPosterSection,
} from "./topReleases/components/TopReleasesSections.jsx";

const TOP_HOURS = 24;
const TOP_TAKE = 5;

const viewOptions = [
  { value: "grid", label: "Cartes" },
  { value: "poster", label: "Poster" },
  { value: "banner", label: "Banner" },
  { value: "list", label: "Liste" },
];

function TopReleasesSubbar({
  subbarClassName,
  sources,
  sourceId,
  setSourceId,
  enabledSources,
  viewMode,
  setViewMode,
}) {
  return (
    <div className={`top24-subbar-content ${subbarClassName || ""}`}>
      <div className="subspacer" />

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
    </div>
  );
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
  if (!Number.isFinite(v) || v <= 0) return "";
  const count = Number(it?.ratingVotes);
  const suffix = Number.isFinite(count) && count > 0 ? ` (${count})` : "";
  return `${v.toFixed(1)}/10${suffix}`;
}

function isSeriesItem(it) {
  const canonical = normalizeCategoryGroupKey(it?.mediaType || it?.unifiedCategoryKey);
  return canonical === "series" || canonical === "anime" || canonical === "emissions";
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

function mapTopItems(items) {
  return (Array.isArray(items) ? items : []).map((it) => ({
    ...it,
    size: fmtBytes(it.sizeBytes),
    date: fmtDateFromTs(it.publishedAt),
  }));
}

function updateTopSections(prevSections, updater) {
  return (Array.isArray(prevSections) ? prevSections : []).map((section) => ({
    ...section,
    top: (section.top || []).map((row) => updater(row)),
  }));
}

function TopGlobalEmptyState({ title, hours, field }) {
  return (
    <section style={{ marginBottom: 40 }}>
      <div className="top24-sectionHead">
        <h2 style={{ marginBottom: 0, fontSize: 18 }}>{title}</h2>
      </div>
      <div className="top24-empty">
        <strong>Aucun item sur les dernieres {hours}h.</strong>
        <span>Fenetre identique a Library, basee sur {field}.</span>
      </div>
    </section>
  );
}

export default function TopReleases() {
  const setContent = useSubbarSetter();
  const [loading, setLoading] = useState(true);
  const [err, setErr] = useState("");
  const [windowInfo, setWindowInfo] = useState(null);
  const [globalTop, setGlobalTop] = useState([]);
  const [categorySections, setCategorySections] = useState([]);
  const [sourceId, setSourceId] = useState("");
  const [sources, setSources] = useState([]);
  const [viewMode, setViewMode] = useState("grid");
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

  const sourceColorById = useMemo(() => {
    const map = new Map();
    (sources || []).forEach((s) => {
      const id = Number(s.id ?? s.sourceId);
      if (Number.isFinite(id)) map.set(id, getSourceColor(id, s.color));
    });
    return map;
  }, [sources]);

  const {
    hasSonarr,
    hasRadarr,
    hasOverseerr,
    hasJellyseerr,
    hasSeer,
    integrationMode,
  } = useArrApps({ pollMs: 120000 });
  const requestMode = normalizeRequestMode(integrationMode);
  const [arrStatusMap, setArrStatusMap] = useState({});

  const handleArrStatusChange = useCallback((itemId, arrType, newStatus) => {
    setArrStatusMap((prev) => ({ ...prev, [itemId]: { ...prev[itemId], ...newStatus } }));
  }, []);

  const load = useCallback(async () => {
    setLoading(true);
    await executeAsync(async () => {
      const q = new URLSearchParams();
      q.set("hours", String(TOP_HOURS));
      q.set("take", String(TOP_TAKE));
      if (sourceId) q.set("sourceId", sourceId);

      const data = await apiGet(`/api/feed/top?${q.toString()}`);
      setWindowInfo(data?.window ?? null);
      setGlobalTop(mapTopItems(data?.globalTop));
      setCategorySections(
        (Array.isArray(data?.categories) ? data.categories : []).map((section) => ({
          key: String(section?.key || ""),
          label: String(section?.label || section?.key || ""),
          count: Number(section?.count || 0),
          top: mapTopItems(section?.top),
        }))
      );
    }, {
      context: "Failed to load TopReleases feed",
      clearError: () => setErr(""),
      setError: setErr,
      fallbackMessage: "Erreur lors du chargement des tops",
      onFinally: () => setLoading(false),
    });
  }, [sourceId]);

  useEffect(() => {
    load();
  }, [load]);

  useEffect(() => {
    if ((viewMode !== "grid" && viewMode !== "poster") || globalTop.length === 0) return;
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

      const mergeDetails = (row) => (
        row.id === it.id
          ? { ...row, ...details }
          : row
      );

      setGlobalTop((prev) => prev.map(mergeDetails));
      setCategorySections((prev) => updateTopSections(prev, mergeDetails));
      setSelectedItem((prev) => (
        prev?.id === it.id
          ? { ...prev, ...details }
          : prev
      ));
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

      const mergeRename = (row) => (
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

      setGlobalTop((prev) => prev.map(mergeRename));
      setCategorySections((prev) => updateTopSections(prev, mergeRename));
      setSelectedItem((prev) => (
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
      ));

      closeRename();
    } catch (error) {
      setErr(error?.message || "Erreur renommage");
    }
  }

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

  useEffect(() => {
    let cancelled = false;
    apiGet("/api/settings/ui")
      .then((ui) => {
        if (cancelled) return;
        const def = String(ui?.defaultView || "grid").toLowerCase();
        const normalized = def === "cards" ? "grid" : def;
        if (viewOptions.some((opt) => opt.value === normalized)) {
          setViewMode(normalized);
        }
      })
      .catch((error) => {
        console.error("Failed to load UI default view for TopReleases", error);
      });
    return () => {
      cancelled = true;
    };
  }, []);

  useEffect(() => {
    setContent(
      <TopReleasesSubbar
        subbarClassName="subbar--top24h"
        sources={sources}
        sourceId={sourceId}
        setSourceId={setSourceId}
        enabledSources={enabledSources}
        viewMode={viewMode}
        setViewMode={setViewMode}
      />
    );

    return () => setContent(null);
  }, [setContent, sources, sourceId, enabledSources, viewMode]);

  const selectedSourceName = useMemo(() => {
    if (!sourceId) return "";
    const selected = sources.find((s) => String(s.id) === sourceId || String(s.sourceId) === sourceId);
    return selected?.name || selected?.title || "";
  }, [sourceId, sources]);

  const uiLanguage = getActiveUiLanguage();
  const totalItems = useMemo(
    () => categorySections.reduce((sum, section) => sum + Number(section.count || 0), 0),
    [categorySections]
  );
  const sinceLabel = useMemo(() => {
    if (!windowInfo?.sinceUtc) return "";
    const date = new Date(windowInfo.sinceUtc);
    if (Number.isNaN(date.getTime())) return "";
    return date.toLocaleString(uiLanguage, { dateStyle: "short", timeStyle: "short" });
  }, [windowInfo?.sinceUtc, uiLanguage]);
  const topSummary = useMemo(() => {
    const hours = Number(windowInfo?.hours || TOP_HOURS);
    const field = String(windowInfo?.field || "published_at_ts");
    const sourceSummary = selectedSourceName ? ` • Source: ${selectedSourceName}` : " • Tous indexeurs";
    const sinceSummary = sinceLabel ? ` • Depuis ${sinceLabel}` : "";
    return `Base sur ${field} depuis ${hours}h${sinceSummary} • ${totalItems} items • ${categorySections.length} categories${sourceSummary}`;
  }, [windowInfo?.hours, windowInfo?.field, selectedSourceName, sinceLabel, totalItems, categorySections.length]);

  if (loading) return <Loader />;
  if (err) return <div className="error">{err}</div>;

  const globalTitle = `🏆 Top ${TOP_TAKE} Global${selectedSourceName ? ` • ${selectedSourceName}` : ""}`;
  return (
    <div className="page page--top24h">
      <div className="pagehead">
        <div>
          <h1>Top 24h</h1>
          <div className="muted">{topSummary}</div>
        </div>
      </div>

      {viewMode === "grid" && (
        <>
          {globalTop.length > 0 ? (
            <TopReleasesGridSection
              items={globalTop}
              sectionTitle={globalTitle}
              showRank={true}
              rankColor="#ffd700"
              showIndexerPill={true}
              compact={false}
              isGlobalTop={true}
              top5AnimKey={top5AnimKey}
              sortBy="recent"
              sourceNameById={sourceNameById}
              onDownload={download}
              onOpen={openDetails}
            />
          ) : (
            <TopGlobalEmptyState title={globalTitle} hours={windowInfo?.hours || TOP_HOURS} field={windowInfo?.field || "published_at_ts"} />
          )}
          {categorySections.map((section) => (
            <TopReleasesGridSection
              key={section.key}
              items={section.top}
              sectionTitle={`📊 Top ${TOP_TAKE} - ${section.label} (${section.count})`}
              showRank={true}
              rankColor="#fff"
              showIndexerPill={!sourceId}
              compact={true}
              isGlobalTop={false}
              top5AnimKey={top5AnimKey}
              sortBy="recent"
              sourceNameById={sourceNameById}
              onDownload={download}
              onOpen={openDetails}
            />
          ))}
        </>
      )}

      {viewMode === "poster" && (
        <>
          {globalTop.length > 0 ? (
            <TopReleasesPosterSection
              items={globalTop}
              sectionTitle={globalTitle}
              showRank={true}
              rankColor="#ffd700"
              isGlobalTop={true}
              top5AnimKey={top5AnimKey}
              onOpen={openDetails}
            />
          ) : (
            <TopGlobalEmptyState title={globalTitle} hours={windowInfo?.hours || TOP_HOURS} field={windowInfo?.field || "published_at_ts"} />
          )}
          {categorySections.map((section) => (
            <TopReleasesPosterSection
              key={section.key}
              items={section.top}
              sectionTitle={`📊 Top ${TOP_TAKE} - ${section.label} (${section.count})`}
              showRank={true}
              rankColor="#fff"
              isGlobalTop={false}
              top5AnimKey={top5AnimKey}
              onOpen={openDetails}
            />
          ))}
        </>
      )}

      {viewMode === "banner" && (
        <>
          {globalTop.length > 0 ? (
            <TopReleasesBannerSection
              items={globalTop}
              sectionTitle={globalTitle}
              showRank={true}
              rankColor="#ffd700"
              sourceNameById={sourceNameById}
              onOpen={openDetails}
              formatRating={formatRating}
            />
          ) : (
            <TopGlobalEmptyState title={globalTitle} hours={windowInfo?.hours || TOP_HOURS} field={windowInfo?.field || "published_at_ts"} />
          )}
          {categorySections.map((section) => (
            <TopReleasesBannerSection
              key={section.key}
              items={section.top}
              sectionTitle={`📊 Top ${TOP_TAKE} - ${section.label} (${section.count})`}
              showRank={true}
              rankColor="#fff"
              sourceNameById={sourceNameById}
              onOpen={openDetails}
              formatRating={formatRating}
            />
          ))}
        </>
      )}

      {viewMode === "list" && (
        <>
          {globalTop.length > 0 ? (
            <TopReleasesListSection
              items={globalTop}
              sectionTitle={globalTitle}
              sourceNameById={sourceNameById}
              onOpen={openDetails}
              onRename={renameRelease}
              formatRating={formatRating}
              isSeriesItem={isSeriesItem}
            />
          ) : (
            <TopGlobalEmptyState title={globalTitle} hours={windowInfo?.hours || TOP_HOURS} field={windowInfo?.field || "published_at_ts"} />
          )}
          {categorySections.map((section) => (
            <TopReleasesListSection
              key={section.key}
              items={section.top}
              sectionTitle={`📊 Top ${TOP_TAKE} - ${section.label} (${section.count})`}
              sourceNameById={sourceNameById}
              onOpen={openDetails}
              onRename={renameRelease}
              formatRating={formatRating}
              isSeriesItem={isSeriesItem}
            />
          ))}
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
        indexerColor={sourceColorById.get(Number(selectedItem?.sourceId)) || null}
        hasSonarr={hasSonarr}
        hasRadarr={hasRadarr}
        hasOverseerr={hasOverseerr}
        hasJellyseerr={hasJellyseerr}
        hasSeer={hasSeer}
        integrationMode={requestMode}
        arrStatus={selectedItem ? arrStatusMap[selectedItem.id] : null}
        onArrStatusChange={handleArrStatusChange}
      />

      <Modal open={renameOpen} title="Renommer" onClose={closeRename} width={520}>
        <form onSubmit={saveRename} style={{ padding: 12 }}>
          <div className="field" style={{ marginBottom: 12 }}>
            <label className="muted">Titre original</label>
            <div className="rename-original" style={{ padding: "4px 0" }}>
              {renameOriginal || "-"}
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
