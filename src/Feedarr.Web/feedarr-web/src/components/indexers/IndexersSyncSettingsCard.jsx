import React, { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { apiGet, apiPut } from "../../api/client.js";
import Loader from "../../ui/Loader.jsx";
import ToggleSwitch from "../../ui/ToggleSwitch.jsx";

const DEFAULT_GENERAL = {
  syncIntervalMinutes: "60",
  rssLimitPerCategory: "50",
  rssLimitGlobalPerSource: "250",
  autoSyncEnabled: true,
};

const toInt = (value, fallback) => {
  const raw = String(value ?? "").trim();
  if (!raw) return fallback;
  const n = Number(raw);
  return Number.isFinite(n) ? Math.trunc(n) : fallback;
};

export default function IndexersSyncSettingsCard({ onStateChange, showSaveButton = false }) {
  const [loading, setLoading] = useState(true);
  const [err, setErr] = useState("");
  const [saveState, setSaveState] = useState("idle");
  const [general, setGeneral] = useState(DEFAULT_GENERAL);
  const [initialGeneral, setInitialGeneral] = useState(DEFAULT_GENERAL);
  const [pulseKeys, setPulseKeys] = useState(() => new Set());
  const [pulseKind, setPulseKind] = useState("ok");
  const pulseTimerRef = useRef(null);
  const syncIntervalRef = useRef(null);
  const rssLimitPerCatRef = useRef(null);
  const rssLimitGlobalRef = useRef(null);

  const load = useCallback(async () => {
    setLoading(true);
    setErr("");
    try {
      const g = await apiGet("/api/settings/general");
      const perCat = toInt(g?.rssLimitPerCategory ?? g?.rssLimit, 50);
      const global = toInt(g?.rssLimitGlobalPerSource, 250);
      const next = {
        syncIntervalMinutes: String(toInt(g?.syncIntervalMinutes, 60)),
        rssLimitPerCategory: String(perCat),
        rssLimitGlobalPerSource: String(global),
        autoSyncEnabled: g?.autoSyncEnabled !== false,
      };
      setGeneral(next);
      setInitialGeneral(next);
    } catch (e) {
      setErr(e?.message || "Erreur chargement paramètres");
      setGeneral(DEFAULT_GENERAL);
      setInitialGeneral(DEFAULT_GENERAL);
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    load();
    return () => {
      if (pulseTimerRef.current) clearTimeout(pulseTimerRef.current);
    };
  }, [load]);

  const isDirty = useMemo(() => {
    return (
      !!general.autoSyncEnabled !== initialGeneral.autoSyncEnabled
      || String(general.syncIntervalMinutes) !== initialGeneral.syncIntervalMinutes
      || String(general.rssLimitPerCategory) !== initialGeneral.rssLimitPerCategory
      || String(general.rssLimitGlobalPerSource) !== initialGeneral.rssLimitGlobalPerSource
    );
  }, [general, initialGeneral]);

  const pulseClass = useCallback((key) => {
    if (!pulseKeys.has(key)) return "";
    return pulseKind === "err" ? " pulse-err" : " pulse-ok";
  }, [pulseKeys, pulseKind]);

  const handleSave = useCallback(async () => {
    if (saveState === "loading") return;
    setErr("");
    const syncRaw = syncIntervalRef.current?.value ?? general.syncIntervalMinutes;
    const perCatRaw = rssLimitPerCatRef.current?.value ?? general.rssLimitPerCategory;
    const globalRaw = rssLimitGlobalRef.current?.value ?? general.rssLimitGlobalPerSource;
    const changed = new Set();
    if (!!general.autoSyncEnabled !== initialGeneral.autoSyncEnabled) changed.add("general.autoSync");
    if (String(syncRaw) !== initialGeneral.syncIntervalMinutes) changed.add("general.sync");
    if (String(perCatRaw) !== initialGeneral.rssLimitPerCategory) changed.add("general.rssPerCat");
    if (String(globalRaw) !== initialGeneral.rssLimitGlobalPerSource) changed.add("general.rssGlobal");

    const startedAt = Date.now();
    let ok = false;
    setSaveState("loading");
    try {
      const current = await apiGet("/api/settings/general");
      await apiPut("/api/settings/general", {
        ...current,
        syncIntervalMinutes: toInt(syncRaw, 60),
        rssLimit: toInt(perCatRaw, 50),
        rssLimitPerCategory: toInt(perCatRaw, 50),
        rssLimitGlobalPerSource: toInt(globalRaw, 250),
        autoSyncEnabled: !!general.autoSyncEnabled,
      });

      setInitialGeneral({
        syncIntervalMinutes: String(toInt(syncRaw, 60)),
        rssLimitPerCategory: String(toInt(perCatRaw, 50)),
        rssLimitGlobalPerSource: String(toInt(globalRaw, 250)),
        autoSyncEnabled: !!general.autoSyncEnabled,
      });
      setGeneral((prev) => ({
        ...prev,
        syncIntervalMinutes: String(syncRaw ?? ""),
        rssLimitPerCategory: String(perCatRaw ?? ""),
        rssLimitGlobalPerSource: String(globalRaw ?? ""),
      }));
      ok = true;
    } catch (e) {
      setErr(e?.message || "Erreur sauvegarde paramètres");
    } finally {
      const elapsed = Date.now() - startedAt;
      if (elapsed < 800) {
        await new Promise((resolve) => setTimeout(resolve, 800 - elapsed));
      }
      setSaveState(ok ? "success" : "error");
      if (changed.size > 0) {
        if (pulseTimerRef.current) clearTimeout(pulseTimerRef.current);
        setPulseKind(ok ? "ok" : "err");
        setPulseKeys(new Set(changed));
        pulseTimerRef.current = setTimeout(() => {
          setPulseKeys(new Set());
        }, 1200);
      }
      setTimeout(() => setSaveState("idle"), 1000);
    }
  }, [general, initialGeneral, saveState]);

  useEffect(() => {
    if (!onStateChange) return;
    onStateChange({
      isDirty,
      saveState,
      onSave: handleSave,
    });
  }, [onStateChange, isDirty, saveState, handleSave]);

  return (
    <div className="settings-card" id="indexers-sync">
      <div className="settings-card__head">
        <div className="settings-card__title">Synchronisation</div>
      </div>

      {err && (
        <div className="errorbox" style={{ marginBottom: 12 }}>
          <div className="errorbox__title">Erreur</div>
          <div className="muted">{err}</div>
        </div>
      )}

      {loading ? (
        <Loader label="Chargement des paramètres..." />
      ) : (
        <div className="indexer-list">
          <div className={`indexer-card${pulseClass("general.autoSync")}`}>
            <div className="indexer-row indexer-row--settings">
              <span className="indexer-title">Synchronisation auto</span>
              <div className="indexer-actions">
                <span className="indexer-status">
                  {general.autoSyncEnabled ? "Actif" : "Desactive"}
                </span>
                <ToggleSwitch
                  checked={general.autoSyncEnabled}
                  onIonChange={(e) => setGeneral((g) => ({ ...g, autoSyncEnabled: e.detail.checked }))}
                  className="settings-toggle"
                  title={general.autoSyncEnabled ? "Désactiver" : "Activer"}
                />
              </div>
            </div>
          </div>

          <div className={`indexer-card${pulseClass("general.sync")}${general.autoSyncEnabled ? "" : " is-disabled"}`}>
            <div className="indexer-row indexer-row--settings">
              <span className="indexer-title">Intervalle Sync RSS (minutes)</span>
              <div className="indexer-actions">
                <input
                  type="number"
                  min={1}
                  max={1440}
                  value={general.syncIntervalMinutes}
                  disabled={!general.autoSyncEnabled}
                  ref={syncIntervalRef}
                  onChange={(e) => {
                    setGeneral((g) => ({ ...g, syncIntervalMinutes: e.target.value }));
                  }}
                />
              </div>
            </div>
          </div>

          <div className={`indexer-card${pulseClass("general.rssPerCat")}`}>
            <div className="indexer-row indexer-row--settings">
              <span
                className="indexer-title"
                title="Remplissage progressif par catégorie, purge des plus anciens."
              >
                Limite RSS par catégorie
              </span>
              <div className="indexer-actions">
                <input
                  type="number"
                  min={1}
                  max={200}
                  value={general.rssLimitPerCategory}
                  ref={rssLimitPerCatRef}
                  onChange={(e) => {
                    setGeneral((g) => ({ ...g, rssLimitPerCategory: e.target.value }));
                  }}
                />
              </div>
            </div>
            <div className="settings-help">Remplissage progressif par catégorie, purge des plus anciens.</div>
          </div>

          <div className={`indexer-card${pulseClass("general.rssGlobal")}`}>
            <div className="indexer-row indexer-row--settings">
              <span
                className="indexer-title"
                title="Plafond global par source, purge des plus anciens."
              >
                Limite RSS globale (par source)
              </span>
              <div className="indexer-actions">
                <input
                  type="number"
                  min={1}
                  max={2000}
                  value={general.rssLimitGlobalPerSource}
                  ref={rssLimitGlobalRef}
                  onChange={(e) => {
                    setGeneral((g) => ({ ...g, rssLimitGlobalPerSource: e.target.value }));
                  }}
                />
              </div>
            </div>
            <div className="settings-help">Plafond global par source, purge des plus anciens.</div>
          </div>
        </div>
      )}

      {showSaveButton && !loading && isDirty && (
        <div className="formactions" style={{ marginTop: 16 }}>
          <button
            className={`btn ${saveState === "success" ? "btn-hover-ok" : saveState === "error" ? "btn-fixed-danger btn-nohover" : "btn-hover-ok"}`}
            type="button"
            onClick={handleSave}
            disabled={!isDirty || saveState === "loading"}
          >
            {saveState === "loading" ? "Enregistrement..." : saveState === "success" ? "Enregistré" : saveState === "error" ? "Erreur" : "Enregistrer"}
          </button>
        </div>
      )}
    </div>
  );
}
