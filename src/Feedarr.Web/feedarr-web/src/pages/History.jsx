import React, { useCallback, useEffect, useMemo, useState } from "react";
import { useSubbarSetter } from "../layout/useSubbar.js";
import SubAction from "../ui/SubAction.jsx";
import { apiGet, apiPost } from "../api/client.js";
import Loader from "../ui/Loader.jsx";
import Modal from "../ui/Modal.jsx";
import AppIcon from "../ui/AppIcon.jsx";
import ErrorBoundary from "../ui/ErrorBoundary.jsx";
import { buildIndexerPillStyle, getSourceColor } from "../utils/sourceColors.js";
import { getActiveUiLanguage } from "../app/locale.js";
import { tr } from "../app/uiText.js";
import {
  CATEGORY_GROUP_LABELS,
  RELEASES_GROUP_PRIORITY,
  normalizeCategoryGroupKey,
} from "../domain/categories/index.js";
import { extractCategoryIds } from "./history/historyCategories.js";

const PAGE_SIZE = 15;

function fmtTs(tsSeconds) {
  if (!tsSeconds) return "-";
  return new Date(tsSeconds * 1000).toLocaleString(getActiveUiLanguage());
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

function resolveCategoryInfo(raw) {
  if (!raw || typeof raw !== "object") return null;
  const id = Number(raw.id);
  const normalizedId = Number.isFinite(id) && id > 0 ? id : null;
  const unifiedKey = normalizeCategoryGroupKey(raw.unifiedKey || raw.key) || null;
  if (unifiedKey) {
    return {
      id: normalizedId,
      key: unifiedKey,
      label: String(CATEGORY_GROUP_LABELS[unifiedKey] || raw.label || raw.unifiedLabel || raw.name || unifiedKey),
    };
  }
  if (raw.label || raw.unifiedLabel || raw.name) {
    return {
      id: normalizedId,
      key: null,
      label: String(raw.label || raw.unifiedLabel || raw.name),
    };
  }
  return null;
}

function normalizeCategoryList(rawCategories) {
  const list = Array.isArray(rawCategories) ? rawCategories : [];
  const unique = new Map();

  list.forEach((raw) => {
    const info = resolveCategoryInfo(raw);
    if (!info || !info.label) return;
    const dedupeKey = `${info.id ?? "na"}|${info.key || ""}|${String(info.label).toLowerCase()}`;
    if (!unique.has(dedupeKey)) unique.set(dedupeKey, info);
  });

  return Array.from(unique.values()).sort((a, b) => {
    const orderA = RELEASES_GROUP_PRIORITY.indexOf(a.key);
    const orderB = RELEASES_GROUP_PRIORITY.indexOf(b.key);
    if (orderA !== -1 || orderB !== -1) {
      return (orderA === -1 ? 999 : orderA) - (orderB === -1 ? 999 : orderB);
    }
    return String(a.label).localeCompare(String(b.label), getActiveUiLanguage(), { sensitivity: "base" });
  });
}

function buildFallbackCategoryList(entry, data) {
  return extractCategoryIds(entry, data).map((id) => ({
    id,
    key: null,
    label: `Cat ${id}`,
  }));
}

function mergeCategories(previous, incoming) {
  const merged = new Map();
  (Array.isArray(previous) ? previous : []).forEach((cat) => {
    const key = `${cat.id ?? "na"}|${cat.key || ""}|${String(cat.label || "").toLowerCase()}`;
    if (!merged.has(key)) merged.set(key, cat);
  });
  (Array.isArray(incoming) ? incoming : []).forEach((cat) => {
    const key = `${cat.id ?? "na"}|${cat.key || ""}|${String(cat.label || "").toLowerCase()}`;
    if (!merged.has(key)) merged.set(key, cat);
  });
  return normalizeCategoryList(Array.from(merged.values()));
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
        sourceNameById[id] = src?.name || `Fournisseur ${id}`;
        sourceColorById[id] = getSourceColor(id, src?.color);
      });

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
            categories: [],
          };
          grouped.set(key, group);
          orderedKeys.push(key);
        }
        const data = parseDataJson(entry?.dataJson ?? entry?.data_json);
        const itemsCount = extractItemsCount(entry, data);
        const responseMs = extractResponseMs(entry, data);
        const entryCategories = normalizeCategoryList(entry?.categories);
        const fallbackCategories = entryCategories.length === 0
          ? buildFallbackCategoryList(entry, data)
          : [];
        const categories = entryCategories.length > 0 ? entryCategories : fallbackCategories;
        if (Number.isFinite(itemsCount)) {
          group.itemsCount = itemsCount;
        }
        if (Number.isFinite(responseMs) && group.responseMs == null) {
          group.responseMs = responseMs;
        }
        if (categories.length > 0) {
          group.categories = mergeCategories(group.categories, categories);
        }
      });

      const mapped = orderedKeys.map((key) => {
        const group = grouped.get(key);
        const categories = group.itemsCount > 0 ? normalizeCategoryList(group.categories) : [];
        return {
          id: key,
          sourceId: group.sourceId,
          createdAt: group.createdAt,
          indexer: sourceNameById[group.sourceId] || `Fournisseur ${group.sourceId}`,
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

  const renderCategories = useCallback((row) => {
    if (!Array.isArray(row?.categories) || row.categories.length === 0) {
      return <span className="muted">-</span>;
    }
    return row.categories.map((cat) => (
      <span
        key={`${row.id}-${cat.id ?? "na"}-${cat.key || cat.label}`}
        className={`cat-bubble cat-bubble--${cat.key || "unknown"}`}
      >
        {cat.label}
      </span>
    ));
  }, []);

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
        <SubAction icon="refresh" label={tr("Rafraîchir", "Refresh")} onClick={refresh} />
        <SubAction icon="delete" label={tr("Effacer", "Clear")} onClick={() => setPurgeOpen(true)} disabled={purgeLoading || rows.length === 0} />
      </>
    );
    return () => setContent(null);
  }, [setContent, refresh, purgeLoading, rows.length]);

  useEffect(() => {
    if (page !== currentPage) setPage(currentPage);
  }, [page, currentPage]);

  return (
    <ErrorBoundary label="Historique">
    <div className="page">
      <div className="pagehead">
        <div>
          <h1>{tr("Historique", "History")}</h1>
          <div className="muted">{tr("Requêtes et synchronisations", "Requests and synchronizations")}</div>
        </div>
      </div>

      {loading && <Loader label={tr("Chargement de l'historique...", "Loading history...")} />}

      {!loading && err && (
        <div className="errorbox">
          <div className="errorbox__title">Erreur</div>
          <div className="muted">{err}</div>
        </div>
      )}

      {!loading && !err && (
      <>
        <div className="history-table history-table--desktop table">
          <div className="thead">
            <div className="th">{tr("Fournisseurs", "Providers")}</div>
            <div className="th">{tr("Éléments", "Items")}</div>
            <div className="th">{tr("Catégories sync", "Sync categories")}</div>
            <div className="th">{tr("Date", "Date")}</div>
            <div className="th th-right">{tr("Temps de réponse", "Response time")}</div>
          </div>
          {pagedRows.length === 0 ? (
            <div className="trow">
              <div className="td history-empty">{tr("Aucune entrée d'historique", "No history entries")}</div>
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
                  {renderCategories(row)}
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
        <div className="history-cards history-cards--mobile">
          {pagedRows.length === 0 ? (
            <div className="history-card history-card--empty">{tr("Aucune entrée d'historique", "No history entries")}</div>
          ) : (
            pagedRows.map((row) => {
              const indexerClass = getIndexerClass(row.indexer);
              const indexerStyle = buildIndexerPillStyle(row.indexerColor);
              return (
                <article className="history-card" key={`mobile-${row.id}`}>
                  <div className="history-card__header">
                    <span
                      className={`banner-pill banner-pill--indexer${indexerClass ? ` ${indexerClass}` : ""}`}
                      style={indexerStyle || undefined}
                    >
                      {row.indexer}
                    </span>
                    <span className="history-card__date">{row.date}</span>
                  </div>

                  <div className="history-card__kv">
                    <div className="history-card__k">{tr("Éléments", "Items")}</div>
                    <div className="history-card__v">{row.itemsCount != null ? row.itemsCount : "-"}</div>
                    <div className="history-card__k">{tr("Temps de réponse", "Response time")}</div>
                    <div className="history-card__v">{Number.isFinite(row.responseMs) ? `${row.responseMs}ms` : "-"}</div>
                  </div>

                  <div className="history-card__categories">
                    <div className="history-card__k">{tr("Catégories sync", "Sync categories")}</div>
                    <div className="history-card__chips">{renderCategories(row)}</div>
                  </div>
                </article>
              );
            })
          )}
        </div>
      </>
      )}

      {!loading && !err && (
      <div className="history-footer">
        <div className="history-meta muted">{tr("Total entrées", "Total records")}: {totalRecords}</div>
        <div className="history-pager">
          <button
            className="iconbtn"
            type="button"
            onClick={() => setPage(1)}
            disabled={currentPage <= 1}
            title={tr("Première page", "First page")}
            aria-label={tr("Première page", "First page")}
          >
            <AppIcon name="first_page" />
          </button>
          <button
            className="iconbtn"
            type="button"
            onClick={() => setPage((p) => Math.max(1, p - 1))}
            disabled={currentPage <= 1}
            title={tr("Page précédente", "Previous page")}
            aria-label={tr("Page précédente", "Previous page")}
          >
            <AppIcon name="chevron_left" />
          </button>
          <div className="history-pagecount">{currentPage} / {totalPages}</div>
          <button
            className="iconbtn"
            type="button"
            onClick={() => setPage((p) => Math.min(totalPages, p + 1))}
            disabled={currentPage >= totalPages}
            title={tr("Page suivante", "Next page")}
            aria-label={tr("Page suivante", "Next page")}
          >
            <AppIcon name="chevron_right" />
          </button>
          <button
            className="iconbtn"
            type="button"
            onClick={() => setPage(totalPages)}
            disabled={currentPage >= totalPages}
            title={tr("Dernière page", "Last page")}
            aria-label={tr("Dernière page", "Last page")}
          >
            <AppIcon name="last_page" />
          </button>
        </div>
      </div>
      )}

      <Modal
        open={purgeOpen}
        title={tr("Effacer l'historique", "Clear history")}
        onClose={() => setPurgeOpen(false)}
        width={520}
      >
        <div style={{ display: "flex", flexDirection: "column", gap: 12 }}>
          <div className="muted">
            {tr("Confirmer la suppression de l'historique.", "Confirm history deletion.")}
          </div>
          <div className="muted">
            {tr("Cette action est définitive.", "This action is permanent.")}
          </div>
          <div style={{ display: "flex", gap: 8, justifyContent: "flex-end" }}>
            <button className="btn" type="button" onClick={() => setPurgeOpen(false)} disabled={purgeLoading}>
              {tr("Annuler", "Cancel")}
            </button>
            <button className="btn btn-accent" type="button" onClick={clear} disabled={purgeLoading}>
              {purgeLoading ? tr("Suppression...", "Deleting...") : tr("Confirmer", "Confirm")}
            </button>
          </div>
        </div>
      </Modal>
    </div>
    </ErrorBoundary>
  );
}
