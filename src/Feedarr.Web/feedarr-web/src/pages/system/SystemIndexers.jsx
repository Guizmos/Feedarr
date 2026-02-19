import React, { useCallback, useEffect, useMemo, useState } from "react";
import { useSubbarSetter } from "../../layout/useSubbar.js";
import SubAction from "../../ui/SubAction.jsx";
import { apiGet } from "../../api/client.js";
import Loader from "../../ui/Loader.jsx";
import { fmtTs } from "./systemUtils.js";

function parseSyncInfo(list) {
  if (!Array.isArray(list)) return null;
  for (const entry of list) {
    const raw = entry?.dataJson;
    if (!raw) continue;
    let data = raw;
    if (typeof raw === "string") {
      try {
        data = JSON.parse(raw);
      } catch {
        data = null;
      }
    }
    if (!data || typeof data !== "object") continue;
    const itemsCount = Number(data?.itemsCount ?? data?.items ?? null);
    const upserted = Number(data?.upserted ?? null);
    if (Number.isFinite(itemsCount) || Number.isFinite(upserted)) {
      const msg = String(entry?.message ?? "").toLowerCase();
      const mode = msg.includes("autosync")
        ? "Automatique"
        : msg.includes("manual") || msg.includes("sync ok") || msg.includes("sync error")
          ? "Manuel"
          : null;
      const createdAt = Number(entry?.createdAt ?? entry?.created_at_ts ?? 0);
      return {
        itemsCount: Number.isFinite(itemsCount) ? itemsCount : null,
        upserted: Number.isFinite(upserted) ? upserted : null,
        createdAt: Number.isFinite(createdAt) && createdAt > 0 ? createdAt : null,
        mode,
      };
    }
  }
  return null;
}

export default function SystemIndexers() {
  const setContent = useSubbarSetter();
  const [loading, setLoading] = useState(true);
  const [err, setErr] = useState("");
  const [sources, setSources] = useState([]);
  const [general, setGeneral] = useState({
    syncIntervalMinutes: 60,
    autoSyncEnabled: true,
  });
  const [lastSyncInfo, setLastSyncInfo] = useState(null);

  const load = useCallback(async () => {
    setLoading(true);
    setErr("");

    try {
      const [srcRes, generalRes, activityRes] = await Promise.allSettled([
        apiGet("/api/sources"),
        apiGet("/api/settings/general"),
        apiGet("/api/activity?limit=30&eventType=sync&level=info"),
      ]);

      const errors = [];

      if (srcRes.status === "fulfilled") {
        setSources(Array.isArray(srcRes.value) ? srcRes.value : []);
      } else {
        setSources([]);
        errors.push("Fournisseurs indisponibles");
      }

      if (generalRes.status === "fulfilled") {
        setGeneral({
          syncIntervalMinutes: Number(generalRes.value?.syncIntervalMinutes ?? 60),
          autoSyncEnabled: generalRes.value?.autoSyncEnabled !== false,
        });
      } else {
        setGeneral({ syncIntervalMinutes: 60, autoSyncEnabled: true });
      }

      if (activityRes.status === "fulfilled") {
        setLastSyncInfo(parseSyncInfo(activityRes.value));
      } else {
        setLastSyncInfo(null);
      }

      if (errors.length) setErr(errors.join(" - "));
    } catch (e) {
      setSources([]);
      setGeneral({ syncIntervalMinutes: 60, autoSyncEnabled: true });
      setLastSyncInfo(null);
      setErr(e?.message || "Fournisseurs indisponibles");
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    load();
  }, [load]);

  useEffect(() => {
    setContent(
      <>
        <SubAction icon="refresh" label="Rafraîchir" onClick={load} />
      </>
    );
    return () => setContent(null);
  }, [setContent, load]);

  const sourcesSorted = useMemo(() => {
    return (sources || []).slice().sort((a, b) => (a?.name || "").localeCompare(b?.name || ""));
  }, [sources]);
  const lastSyncAt = useMemo(() => {
    const values = (sources || [])
      .map((s) => Number(s?.lastSyncAt ?? 0))
      .filter((n) => Number.isFinite(n) && n > 0);
    return values.length > 0 ? Math.max(...values) : null;
  }, [sources]);
  const nextSyncAt = useMemo(() => {
    const interval = Number(general?.syncIntervalMinutes ?? 0);
    if (!general?.autoSyncEnabled || !Number.isFinite(interval) || interval <= 0) return null;
    if (!Number.isFinite(lastSyncAt) || !lastSyncAt) return null;
    return Math.round(lastSyncAt + interval * 60);
  }, [general, lastSyncAt]);
  const lastSyncItems = useMemo(() => {
    if (!lastSyncInfo) return null;
    if (Number.isFinite(lastSyncInfo.upserted)) return lastSyncInfo.upserted;
    if (Number.isFinite(lastSyncInfo.itemsCount)) return lastSyncInfo.itemsCount;
    return null;
  }, [lastSyncInfo]);
  const lastSyncMode = lastSyncInfo?.mode || "-";
  const fmtCount = (value) => {
    const n = Number(value);
    return Number.isFinite(n) ? String(n) : "-";
  };

  return (
    <div className="page page--system">
      <div className="pagehead">
        <div>
          <h1>Fournisseurs</h1>
          <div className="muted">Configuration de l'application</div>
        </div>
      </div>
      <div className="pagehead__divider" />

      {loading && <Loader label="Chargement des fournisseurs..." />}

      {!loading && err && (
        <div className="errorbox">
          <div className="errorbox__title">Attention</div>
          <div className="muted">{err}</div>
        </div>
      )}

      {!loading && (
        <div className="settings-grid">
          <div className="card-row card-row-third" style={{ gridColumn: "1 / -1" }}>
            <div className="card card-third">
              <div className="card-title">Dernier sync</div>
              <div className="card-value">{fmtTs(lastSyncAt)}</div>
              <div className="muted">Mode: {lastSyncMode}</div>
            </div>
            <div className="card card-third">
              <div className="card-title">Prochain sync</div>
              <div className="card-value">{fmtTs(nextSyncAt)}</div>
            </div>
            <div className="card card-third">
              <div className="card-title">Items dernier sync</div>
              <div className="card-value">{fmtCount(lastSyncItems)}</div>
            </div>
          </div>
          <div className="settings-card settings-card--full">
            <div className="settings-card__title">Santé des fournisseurs</div>
            <div className="health-head">
              <div>Nom</div>
              <div>URL</div>
              <div>Sync</div>
              <div>Status</div>
              <div>Erreur</div>
            </div>

            {sourcesSorted.length === 0 && (
              <div className="indexer-card">
                <div className="indexer-row indexer-row--settings">
                  <span className="indexer-title">Aucune source configurée.</span>
                </div>
              </div>
            )}

            {sourcesSorted.map((s) => {
              const lastStatus = String(s?.lastStatus || "").toLowerCase();
              const statusClass = lastStatus === "ok" ? "pill-ok" : lastStatus ? "pill-warn" : "";
              return (
                <div className={`indexer-card${s?.enabled ? "" : " is-disabled"}`} key={s.id}>
                  <div className="health-row">
                    <div className="health-cell td-name">
                      <span className={`dot ${s?.enabled ? "ok" : "off"}`} />
                      <span className="name">{s?.name || `Fournisseur ${s?.id}`}</span>
                    </div>
                    <div className="health-cell td-url">
                      <span className="muted">{s?.torznabUrl || "-"}</span>
                    </div>
                    <div className="health-cell">
                      <span className="muted">{fmtTs(s?.lastSyncAt)}</span>
                    </div>
                    <div className="health-cell">
                      {lastStatus ? (
                        <span className={`pill ${statusClass}`}>{lastStatus.toUpperCase()}</span>
                      ) : (
                        <span className="muted">-</span>
                      )}
                    </div>
                    <div className="health-cell">
                      <span className="muted">{s?.lastError || "-"}</span>
                    </div>
                  </div>
                </div>
              );
            })}
          </div>
        </div>
      )}
    </div>
  );
}
