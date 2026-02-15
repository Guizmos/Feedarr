import React, { useCallback, useEffect, useMemo, useState } from "react";
import { useSubbarSetter } from "../layout/useSubbar.js";
import SubAction from "../ui/SubAction.jsx";
import { apiGet, apiPost } from "../api/client.js";
import Loader from "../ui/Loader.jsx";
import Modal from "../ui/Modal.jsx";
import AppIcon from "../ui/AppIcon.jsx";
import { buildIndexerPillStyle, getSourceColor } from "../utils/sourceColors.js";

const PAGE_SIZE = 15;

const UNIFIED_LABELS = {
  films: "Films",
  series: "Series TV",
  anime: "Animation",
  games: "Jeux PC",
  spectacle: "Spectacle",
  shows: "Emissions",
};

const UNIFIED_PRIORITY = ["series", "anime", "films", "games", "spectacle", "shows"];

const EXCLUDED_TOKENS = [
  "audio",
  "music",
  "mp3",
  "flac",
  "ebook",
  "book",
  "livre",
  "audiobook",
  "podcast",
  "software",
  "app",
  "application",
  "mobile",
  "console",
  "xbox",
  "ps4",
  "ps5",
  "playstation",
  "nintendo",
  "switch",
  "xxx",
  "adult",
  "sport",
];

function fmtTs(tsSeconds) {
  if (!tsSeconds) return "-";
  return new Date(tsSeconds * 1000).toLocaleString("fr-FR");
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

function normalizeCategoryLabel(value) {
  return String(value || "")
    .toLowerCase()
    .normalize("NFD")
    .replace(/[\u0300-\u036f]/g, "")
    .replace(/[^a-z0-9]+/g, " ")
    .trim();
}

function hasAnyToken(tokens, list) {
  return list.some((t) => tokens.has(t));
}

function categorizeLabel(name) {
  const normalized = normalizeCategoryLabel(name);
  if (!normalized) return null;
  const tokens = new Set(normalized.split(/\s+/).filter(Boolean));
  if (hasAnyToken(tokens, EXCLUDED_TOKENS)) return null;

  const scores = {
    series: 0,
    films: 0,
    anime: 0,
    games: 0,
    spectacle: 0,
    shows: 0,
  };

  if (hasAnyToken(tokens, ["serie", "series", "tv", "tele"])) scores.series = 3;
  const hasFilmToken = hasAnyToken(tokens, ["film", "films", "movie", "movies", "cinema"]);
  const hasVideoToken = tokens.has("video");
  const hasSpectacleToken = hasAnyToken(tokens, ["spectacle", "concert", "opera", "theatre", "ballet", "symphonie", "orchestr", "philharmon", "ring", "choregraph", "danse"]);

  if (hasAnyToken(tokens, ["anime", "animation"])) scores.anime = 4;
  if (hasSpectacleToken) scores.spectacle = 4;
  if (hasAnyToken(tokens, ["emission", "show", "talk", "reality", "documentaire", "docu", "magazine", "reportage", "enquete", "quotidien", "quotidienne"])) scores.shows = 4;

  const hasPc = hasAnyToken(tokens, ["pc", "windows", "win32", "win64"]);
  const hasGame = hasAnyToken(tokens, ["jeu", "jeux", "game", "games"]);
  const isGame = hasPc && hasGame;
  if (isGame) scores.games = 3;

  if (!hasSpectacleToken && (hasFilmToken || (hasVideoToken && !hasGame && !isGame))) scores.films = 3;

  const maxScore = Math.max(...Object.values(scores));
  if (maxScore < 3) return null;

  const bestKey = UNIFIED_PRIORITY.find((key) => scores[key] === maxScore);
  if (!bestKey) return null;

  return {
    unifiedKey: bestKey,
    unifiedLabel: UNIFIED_LABELS[bestKey] || bestKey,
  };
}

function parseDataJson(raw) {
  if (!raw) return null;
  if (typeof raw === "object") return raw;
  if (typeof raw !== "string") return null;
  try {
    return JSON.parse(raw);
  } catch {
    return null;
  }
}

const RE_FETCHED = /fetched=(\d+)/i;
const RE_ITEMS = /\((\d+)\s*items/i;
const RE_MS = /(\d+)\s*ms/i;

function extractItemsCount(entry, data) {
  const directRaw = data?.itemsCount ?? data?.items;
  if (directRaw !== null && directRaw !== undefined) {
    const direct = Number(directRaw);
    if (Number.isFinite(direct)) return direct;
  }
  const message = String(entry?.message ?? "");
  const fetchedMatch = message.match(RE_FETCHED);
  if (fetchedMatch) return Number(fetchedMatch[1]);
  const itemsMatch = message.match(RE_ITEMS);
  if (itemsMatch) return Number(itemsMatch[1]);
  return null;
}

function extractResponseMs(entry, data) {
  const directRaw = data?.elapsedMs ?? data?.responseMs ?? data?.elapsed;
  if (directRaw !== null && directRaw !== undefined) {
    const direct = Number(directRaw);
    if (Number.isFinite(direct)) return direct;
  }
  const message = String(entry?.message ?? "");
  const msMatch = message.match(RE_MS);
  if (msMatch) return Number(msMatch[1]);
  return null;
}

function extractCategoryIds(entry) {
  const message = String(entry?.message ?? "").toLowerCase();
  const parts = message.split("cats=");
  if (parts.length < 2) return [];
  const catsSection = parts[1].split("missing=")[0];
  if (!catsSection) return [];
  return catsSection
    .split(",")
    .map((raw) => raw.trim().split("=")[0])
    .map((value) => Number(value))
    .filter((value) => Number.isFinite(value));
}

function resolveCategoryInfo(raw) {
  if (!raw) return null;
  const unifiedKey = raw.unifiedKey || null;
  const unifiedLabel = raw.unifiedLabel || UNIFIED_LABELS[unifiedKey] || null;
  if (unifiedKey) {
    return {
      key: unifiedKey,
      label: unifiedLabel || raw.name || unifiedKey,
    };
  }
  const derived = categorizeLabel(raw.unifiedLabel || raw.name);
  if (derived) {
    return {
      key: derived.unifiedKey,
      label: derived.unifiedLabel,
    };
  }
  if (raw.name) {
    return {
      key: null,
      label: raw.name,
    };
  }
  return null;
}

function buildCategoryList(catIds, lookup) {
  if (!lookup || catIds.length === 0) return [];
  const unique = new Map();
  catIds.forEach((id) => {
    const info = resolveCategoryInfo(lookup[id]);
    if (!info || !info.label) return;
    const key = info.key || info.label;
    if (!unique.has(key)) unique.set(key, info);
  });
  const list = Array.from(unique.values());
  return list.sort((a, b) => {
    const orderA = UNIFIED_PRIORITY.indexOf(a.key);
    const orderB = UNIFIED_PRIORITY.indexOf(b.key);
    if (orderA !== -1 || orderB !== -1) {
      return (orderA === -1 ? 999 : orderA) - (orderB === -1 ? 999 : orderB);
    }
    return String(a.label).localeCompare(String(b.label));
  });
}

export default function History() {
  const setContent = useSubbarSetter();
  const [rows, setRows] = useState([]);
  const [page, setPage] = useState(1);
  const [purgeOpen, setPurgeOpen] = useState(false);
  const [purgeLoading, setPurgeLoading] = useState(false);
  const [loading, setLoading] = useState(true);
  const [err, setErr] = useState("");

  const load = useCallback(async () => {
    setLoading(true);
    setErr("");

    try {
      const [activityRes, sourcesRes] = await Promise.allSettled([
        apiGet("/api/activity?limit=500&eventType=sync"),
        apiGet("/api/sources"),
      ]);

      const activity = activityRes.status === "fulfilled" && Array.isArray(activityRes.value)
        ? activityRes.value
        : [];
      const sources = sourcesRes.status === "fulfilled" && Array.isArray(sourcesRes.value)
        ? sourcesRes.value
        : [];

      if (activityRes.status !== "fulfilled") {
        setErr("Historique indisponible");
      }

      const sourceNameById = {};
      const sourceColorById = {};
      sources.forEach((src) => {
        const id = src?.id ?? src?.sourceId;
        if (!id) return;
        sourceNameById[id] = src?.name || `Source ${id}`;
        sourceColorById[id] = getSourceColor(id, src?.color);
      });

      const categoriesBySourceId = {};
      const categoryFetchIds = new Set();
      activity.forEach((entry) => {
        const sourceId = entry?.sourceId ?? entry?.source_id;
        if (!sourceId) return;
        if (extractCategoryIds(entry).length > 0) {
          categoryFetchIds.add(sourceId);
        }
      });

      if (categoryFetchIds.size > 0) {
        const sourceIds = Array.from(categoryFetchIds);
        const categoryResults = await Promise.allSettled(
          sourceIds.map((sourceId) =>
            apiGet(`/api/categories/${sourceId}`)
          )
        );
        sourceIds.forEach((sourceId, idx) => {
          const res = categoryResults[idx];
          if (res.status !== "fulfilled" || !Array.isArray(res.value)) return;
          const map = {};
          res.value.forEach((cat) => {
            if (!cat?.id) return;
            map[cat.id] = {
              name: cat?.name || String(cat.id),
              unifiedKey: cat?.unifiedKey || null,
              unifiedLabel: cat?.unifiedLabel || null,
            };
          });
          categoriesBySourceId[sourceId] = map;
        });
      }

      const grouped = new Map();
      const orderedKeys = [];

      activity.forEach((entry) => {
        const sourceId = entry?.sourceId ?? entry?.source_id;
        const createdAt = Number(entry?.createdAt ?? entry?.created_at_ts ?? 0);
        if (!sourceId || !Number.isFinite(createdAt) || createdAt <= 0) return;
        const key = `${sourceId}-${createdAt}`;
        let group = grouped.get(key);
        if (!group) {
          group = {
            sourceId,
            createdAt,
            itemsCount: null,
            responseMs: null,
            catIds: new Set(),
          };
          grouped.set(key, group);
          orderedKeys.push(key);
        }
        const data = parseDataJson(entry?.dataJson ?? entry?.data_json);
        const itemsCount = extractItemsCount(entry, data);
        const responseMs = extractResponseMs(entry, data);
        const catIds = extractCategoryIds(entry);
        if (Number.isFinite(itemsCount)) {
          group.itemsCount = itemsCount;
        }
        if (Number.isFinite(responseMs) && group.responseMs == null) {
          group.responseMs = responseMs;
        }
        catIds.forEach((id) => group.catIds.add(id));
      });

      const mapped = orderedKeys.map((key) => {
        const group = grouped.get(key);
        const lookup = categoriesBySourceId[group.sourceId];
        const catIds = Array.from(group.catIds || []);
        const categories = group.itemsCount > 0 ? buildCategoryList(catIds, lookup) : [];
        return {
          id: key,
          sourceId: group.sourceId,
          createdAt: group.createdAt,
          indexer: sourceNameById[group.sourceId] || `Source ${group.sourceId}`,
          indexerColor: sourceColorById[group.sourceId] || getSourceColor(group.sourceId, null),
          itemsCount: group.itemsCount,
          categories,
          date: fmtTs(group.createdAt),
          responseMs: group.responseMs,
        };
      });

      const merged = [];
      mapped.forEach((row) => {
        const prev = merged[merged.length - 1];
        if (prev && prev.sourceId === row.sourceId && Math.abs(prev.createdAt - row.createdAt) <= 10) {
          const prevCount = Number(prev.itemsCount);
          const rowCount = Number(row.itemsCount);
          if (!Number.isFinite(prevCount) || prevCount <= 0) {
            prev.itemsCount = Number.isFinite(rowCount) ? rowCount : prev.itemsCount;
          }
          if ((!prev.categories || prev.categories.length === 0) && row.categories.length > 0) {
            prev.categories = row.categories;
          }
          if (prev.responseMs == null && row.responseMs != null) {
            prev.responseMs = row.responseMs;
          }
          return;
        }
        merged.push(row);
      });

      setRows(merged);
    } catch (e) {
      setErr(e?.message || "Erreur chargement historique");
      setRows([]);
    } finally {
      setLoading(false);
    }
  }, []);

  const totalRecords = rows.length;
  const totalPages = Math.max(1, Math.ceil(totalRecords / PAGE_SIZE));
  const currentPage = Math.min(page, totalPages);
  const pagedRows = useMemo(() => {
    const start = (currentPage - 1) * PAGE_SIZE;
    return rows.slice(start, start + PAGE_SIZE);
  }, [rows, currentPage]);

  const refresh = useCallback(() => {
    setPage(1);
    load();
  }, [load]);

  const clear = useCallback(async () => {
    setPurgeLoading(true);
    try {
      await apiPost("/api/activity/purge?scope=history", {});
      setPurgeOpen(false);
      setPage(1);
      await load();
    } catch (e) {
      setErr(e?.message || "Erreur purge historique");
    } finally {
      setPurgeLoading(false);
    }
  }, [load]);

  useEffect(() => {
    load();
  }, [load]);

  useEffect(() => {
    setContent(
      <>
        <SubAction icon="refresh" label="Rafraîchir" onClick={refresh} />
        <SubAction icon="delete" label="Effacer" onClick={() => setPurgeOpen(true)} disabled={purgeLoading || rows.length === 0} />
      </>
    );
    return () => setContent(null);
  }, [setContent, refresh, purgeLoading, rows.length]);

  useEffect(() => {
    if (page !== currentPage) setPage(currentPage);
  }, [page, currentPage]);

  return (
    <div className="page">
      <div className="pagehead">
        <div>
          <h1>Historique</h1>
          <div className="muted">Requêtes et synchronisations</div>
        </div>
      </div>

      {loading && <Loader label="Chargement de l'historique..." />}

      {!loading && err && (
        <div className="errorbox">
          <div className="errorbox__title">Erreur</div>
          <div className="muted">{err}</div>
        </div>
      )}

      {!loading && !err && (
      <div className="history-table table">
        <div className="thead">
          <div className="th">Indexeurs</div>
          <div className="th">Items</div>
          <div className="th">Categories sync</div>
          <div className="th">Date</div>
          <div className="th th-right">Temps réponses</div>
        </div>
        {pagedRows.length === 0 ? (
          <div className="trow">
            <div className="td history-empty">Aucune entrée d'historique</div>
          </div>
        ) : (
          pagedRows.map((row) => {
            const indexerClass = getIndexerClass(row.indexer);
            const indexerStyle = buildIndexerPillStyle(row.indexerColor);
            return (
            <div className="trow" key={row.id}>
              <div className="td">
                <span
                  className={`banner-pill banner-pill--indexer${indexerClass ? ` ${indexerClass}` : ""}`}
                  style={indexerStyle || undefined}
                >
                  {row.indexer}
                </span>
              </div>
              <div className="td td-query" title={row.itemsCount != null ? `${row.itemsCount}` : "-"}>
                {row.itemsCount != null ? row.itemsCount : "-"}
              </div>
              <div className="td td-categories">
                {row.categories.length > 0 ? (
                  row.categories.map((cat) => (
                    <span
                      key={`${row.id}-${cat.key || cat.label}`}
                      className={`cat-bubble cat-bubble--${cat.key || "unknown"}`}
                    >
                      {cat.label}
                    </span>
                  ))
                ) : (
                  <span className="muted">-</span>
                )}
              </div>
              <div className="td td-date">{row.date}</div>
              <div className="td td-right td-response">
                {Number.isFinite(row.responseMs) ? `${row.responseMs}ms` : "-"}
              </div>
            </div>
            );
          })
        )}
      </div>
      )}

      {!loading && !err && (
      <div className="history-footer">
        <div className="history-meta muted">Total records: {totalRecords}</div>
        <div className="history-pager">
          <button
            className="iconbtn"
            type="button"
            onClick={() => setPage(1)}
            disabled={currentPage <= 1}
            title="Première page"
            aria-label="Première page"
          >
            <AppIcon name="first_page" />
          </button>
          <button
            className="iconbtn"
            type="button"
            onClick={() => setPage((p) => Math.max(1, p - 1))}
            disabled={currentPage <= 1}
            title="Page précédente"
            aria-label="Page précédente"
          >
            <AppIcon name="chevron_left" />
          </button>
          <div className="history-pagecount">{currentPage} / {totalPages}</div>
          <button
            className="iconbtn"
            type="button"
            onClick={() => setPage((p) => Math.min(totalPages, p + 1))}
            disabled={currentPage >= totalPages}
            title="Page suivante"
            aria-label="Page suivante"
          >
            <AppIcon name="chevron_right" />
          </button>
          <button
            className="iconbtn"
            type="button"
            onClick={() => setPage(totalPages)}
            disabled={currentPage >= totalPages}
            title="Dernière page"
            aria-label="Dernière page"
          >
            <AppIcon name="last_page" />
          </button>
        </div>
      </div>
      )}

      <Modal
        open={purgeOpen}
        title="Effacer l'historique"
        onClose={() => setPurgeOpen(false)}
        width={520}
      >
        <div style={{ display: "flex", flexDirection: "column", gap: 12 }}>
          <div className="muted">
            Voulez-vous effacer l'historique ? Cette action est definitive.
          </div>
          <div style={{ display: "flex", gap: 8, justifyContent: "flex-end" }}>
            <button className="btn" type="button" onClick={() => setPurgeOpen(false)} disabled={purgeLoading}>
              Annuler
            </button>
            <button className="btn btn-accent" type="button" onClick={clear} disabled={purgeLoading}>
              {purgeLoading ? "Suppression..." : "Confirmer"}
            </button>
          </div>
        </div>
      </Modal>
    </div>
  );
}
