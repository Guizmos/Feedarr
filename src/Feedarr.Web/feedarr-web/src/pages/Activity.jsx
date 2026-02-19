
import React, { useState, useEffect, useCallback } from "react";
import { useSubbarSetter } from "../layout/useSubbar.js";
import SubAction from "../ui/SubAction.jsx";
import { apiGet, apiPost, resolveApiUrl } from "../api/client.js";
import Loader from "../ui/Loader.jsx";
import Modal from "../ui/Modal.jsx";
import AppIcon from "../ui/AppIcon.jsx";
import { getActiveUiLanguage } from "../app/locale.js";
import { tr } from "../app/uiText.js";

export default function Activity() {
  function fmtTs(ts) {
    if (!ts) return "";
    return new Date(ts * 1000).toLocaleString(getActiveUiLanguage());
  }

  const [loading, setLoading] = useState(true);
  const [err, setErr] = useState("");
  const [items, setItems] = useState([]);
  const [sourcesById, setSourcesById] = useState({});
  const [expandedIds, setExpandedIds] = useState(() => new Set());
  const [purgeOpen, setPurgeOpen] = useState(false);
  const [purgeLoading, setPurgeLoading] = useState(false);

  const [limit, setLimit] = useState(100);
  const [eventType, setEventType] = useState("");
  const [level, setLevel] = useState("");
  const setContent = useSubbarSetter();

  const load = useCallback(async () => {
    setLoading(true);
    setErr("");

    try {
      const params = new URLSearchParams();
      params.set("limit", String(limit));
      if (eventType) params.set("eventType", eventType);
      if (level) params.set("level", level);

      const data = await apiGet(`/api/activity?${params.toString()}`);
      setItems(Array.isArray(data) ? data : []);
    } catch (e) {
      setErr(e.message || "Erreur chargement activité");
      setItems([]);
    } finally {
      setLoading(false);
    }
  }, [limit, eventType, level]);

  useEffect(() => {
    load();
  }, [load]);

  useEffect(() => {
    (async () => {
      try {
        const src = await apiGet("/api/sources");
        if (Array.isArray(src)) {
          const map = {};
          src.forEach((s) => {
            const id = s?.id ?? s?.sourceId;
            if (!id) return;
            map[id] = s?.name || `Fournisseur ${id}`;
          });
          setSourcesById(map);
        }
      } catch {
        // ignore
      }
    })();
  }, []);

  function SubSelectIcon({ icon, label, value, onChange, children, title }) {
    return (
      <div className="subdropdown" title={title || label}>
        <AppIcon name={icon} className="subdropdown__icon" />
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

  useEffect(() => {
    setContent(
      <>
        <SubAction icon="refresh" label={tr("Rafraîchir", "Refresh")} onClick={load} />
        <SubAction icon="delete" label={tr("Effacer", "Clear")} onClick={() => setPurgeOpen(true)} disabled={purgeLoading || items.length === 0} />

        <span className="subsep" />
        <div className="subspacer" />

        <SubSelectIcon
          icon="filter_list"
          label={tr("Type", "Type")}
          value={eventType}
          onChange={(e) => setEventType(e.target.value)}
          title={tr("Type de logs", "Log type")}
        >
          <option value="">{tr("Tous", "All")}</option>
          <option value="sync">Sync</option>
          <option value="source">{tr("Source", "Source")}</option>
          <option value="poster_fetch">{tr("Posters", "Posters")}</option>
        </SubSelectIcon>

        <SubSelectIcon
          icon="priority_high"
          label={tr("Niveau", "Level")}
          value={level}
          onChange={(e) => setLevel(e.target.value)}
          title={tr("Niveau de log", "Log level")}
        >
          <option value="">{tr("Tous", "All")}</option>
          <option value="info">Info</option>
          <option value="warn">{tr("Avertissement", "Warning")}</option>
          <option value="error">{tr("Erreur", "Error")}</option>
        </SubSelectIcon>
      </>
    );
    return () => setContent(null);
  }, [setContent, load, eventType, level, purgeLoading, items.length]);

  const filteredItems = items.filter((it) => {
    if (eventType && String(it.eventType || "") !== eventType) return false;
    if (level && String(it.level || "") !== level) return false;
    return true;
  });

  function toggleExpanded(id) {
    setExpandedIds((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  }

  function parseDetails(dataJson) {
    if (!dataJson) return null;
    try {
      return typeof dataJson === "string" ? JSON.parse(dataJson) : dataJson;
    } catch {
      return null;
    }
  }

  function formatDetails(dataJson) {
    if (!dataJson) return null;
    const data = parseDetails(dataJson);
    if (data) return JSON.stringify(data, null, 2);
    return String(dataJson);
  }

  async function purgeLogs() {
    setPurgeLoading(true);
    try {
      await apiPost("/api/activity/purge?scope=all", {});
      setExpandedIds(new Set());
      setPurgeOpen(false);
      await load();
    } catch (e) {
      setErr(e?.message || "Erreur purge logs");
    } finally {
      setPurgeLoading(false);
    }
  }

  return (
    <div className="page">
      <div className="pagehead">
        <div>
          <h1>Logs</h1>
          <div className="muted">{tr("Synchronisations et événements", "Synchronizations and events")}</div>
        </div>

        <div style={{ display: "flex", gap: 8 }}>
          <select value={limit} onChange={(e) => setLimit(e.target.value)}>
            <option value={50}>50</option>
            <option value={100}>100</option>
            <option value={200}>200</option>
          </select>
        </div>
      </div>

      {loading && <Loader label={tr("Chargement des logs...", "Loading logs...")} />}

      {!loading && err && (
        <div className="errorbox">
          <div className="errorbox__title">Erreur</div>
          <div className="muted">{err}</div>
        </div>
      )}

      {!loading && !err && (
        <div style={{ display: "flex", flexDirection: "column", gap: 8 }}>
          {filteredItems.map((it, i) => (
            <div
              key={i}
              className={
                "activityCard" +
                (String(it.level || "").toLowerCase() === "warn"
                  ? " activityCard--warn"
                  : "") +
                (String(it.level || "").toLowerCase() === "error"
                  ? " activityCard--error"
                  : "")
              }
            >
              <div style={{ display: "flex", gap: 12, alignItems: "center" }}>
                <div style={{ display: "flex", flexWrap: "wrap", gap: 8, alignItems: "baseline", flex: "1 1 auto" }}>
                  <div style={{ fontWeight: 700 }}>
                    [{it.level?.toUpperCase()}] {it.message}
                  </div>
                  <div className="muted" style={{ display: "flex", gap: 8, flexWrap: "wrap" }}>
                    <span>
                      {tr("Source", "Source")} : {it.sourceId ?? "-"}
                      {it.sourceId && sourcesById[it.sourceId] ? ` ${sourcesById[it.sourceId]}` : ""}
                    </span>
                    <span>{tr("Type", "Type")} : {it.eventType ?? "-"}</span>
                    <span>#{it.id ?? "-"}</span>
                  </div>
                </div>
                <div className="muted" style={{ marginLeft: "auto", whiteSpace: "nowrap" }}>
                  {fmtTs(it.createdAt)}
                </div>

                <button
                  className="activityCard__toggle"
                  onClick={() => toggleExpanded(it.id ?? i)}
                  title={expandedIds.has(it.id ?? i) ? tr("Masquer détails", "Hide details") : tr("Afficher détails", "Show details")}
                  aria-label={expandedIds.has(it.id ?? i) ? tr("Masquer détails", "Hide details") : tr("Afficher détails", "Show details")}
                >
                  {expandedIds.has(it.id ?? i) ? "–" : "+"}
                </button>
              </div>

              {expandedIds.has(it.id ?? i) && (
                <>
                  <pre className="activityCard__details">
                    {formatDetails(it.dataJson) || tr("Aucun détail", "No details")}
                  </pre>
                  {parseDetails(it.dataJson)?.logFile && (
                    <div className="activityCard__detailsActions">
                      <a
                        className="btn btn-sm"
                        href={resolveApiUrl(
                          `/api/posters/retro-fetch/log/${encodeURIComponent(parseDetails(it.dataJson).logFile)}`
                        )}
                        download
                      >
                        {tr("Télécharger", "Download")}
                      </a>
                    </div>
                  )}
                </>
              )}
            </div>
          ))}
        </div>
      )}

      <Modal
        open={purgeOpen}
        title={tr("Effacer les logs", "Clear logs")}
        onClose={() => setPurgeOpen(false)}
        width={520}
      >
        <div style={{ display: "flex", flexDirection: "column", gap: 12 }}>
          <div className="muted">
            {tr("Confirmer la purge des logs.", "Confirm log purge.")}
          </div>
          <div className="muted">
            {tr("Cette action est définitive.", "This action is permanent.")}
          </div>
          <div style={{ display: "flex", gap: 8, justifyContent: "flex-end" }}>
            <button className="btn" type="button" onClick={() => setPurgeOpen(false)} disabled={purgeLoading}>
              {tr("Annuler", "Cancel")}
            </button>
            <button className="btn btn-accent" type="button" onClick={purgeLogs} disabled={purgeLoading}>
              {purgeLoading ? tr("Suppression...", "Deleting...") : tr("Confirmer", "Confirm")}
            </button>
          </div>
        </div>
      </Modal>
    </div>
  );
}
