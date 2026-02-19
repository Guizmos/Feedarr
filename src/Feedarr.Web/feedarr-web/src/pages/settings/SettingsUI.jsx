import React, { useEffect, useMemo, useState } from "react";
import ToggleSwitch from "../../ui/ToggleSwitch.jsx";
import { apiGet } from "../../api/client.js";
import { LANGUAGE_OPTIONS } from "../../app/locale.js";

const FILTER_QUALITY_OPTIONS = [
  "2160p",
  "1080p",
  "720p",
  "480p",
  "WEB-DL",
  "BluRay",
  "HDTV",
];

export default function SettingsUI({
  ui,
  setUi,
  pulseKeys,
  handleThemeChange,
}) {
  const [sources, setSources] = useState([]);
  const [apps, setApps] = useState([]);
  const [categoryStats, setCategoryStats] = useState([]);

  useEffect(() => {
    let alive = true;
    (async () => {
      try {
        const [src, arrApps, cats] = await Promise.all([
          apiGet("/api/sources").catch(() => []),
          apiGet("/api/apps").catch(() => []),
          apiGet("/api/categories/stats").catch(() => ({ stats: [] })),
        ]);
        if (!alive) return;
        setSources(Array.isArray(src) ? src : []);
        setApps(Array.isArray(arrApps) ? arrApps : []);
        setCategoryStats(Array.isArray(cats?.stats) ? cats.stats : []);
      } catch {
        if (!alive) return;
        setSources([]);
        setApps([]);
        setCategoryStats([]);
      }
    })();
    return () => {
      alive = false;
    };
  }, []);

  const sourceOptions = useMemo(
    () =>
      (sources || [])
        .filter((s) => Number(s.enabled ?? 1) === 1)
        .map((s) => ({
          value: String(s.id ?? s.sourceId),
          label: s.name ?? s.title ?? `Source ${s.id ?? s.sourceId}`,
        })),
    [sources]
  );

  const categoryOptions = useMemo(
    () =>
      (categoryStats || [])
        .map((c) => ({
          value: String(c?.key || "").trim().toLowerCase(),
          label: String(c?.name || c?.key || "").trim(),
          count: Number(c?.count || 0),
        }))
        .filter((c) => !!c.value)
        .sort((a, b) => b.count - a.count),
    [categoryStats]
  );

  const appOptions = useMemo(
    () =>
      (apps || [])
        .filter((a) => a && a.id != null && a.isEnabled !== false && a.hasApiKey !== false)
        .map((a) => ({
          value: String(a.id),
          label: a.name || a.title || `${String(a.type || "App").toLowerCase()} ${a.id}`,
        }))
        .sort((a, b) => a.label.localeCompare(b.label, ui?.uiLanguage || "fr-FR", { sensitivity: "base" })),
    [apps, ui?.uiLanguage]
  );

  const cardClass = (pulseKey, enabled) =>
    `indexer-card${pulseKey && pulseKeys.has(pulseKey) ? " pulse-ok" : ""}${enabled ? "" : " is-disabled"}`;

  return (
    <>
      <div className="settings-card" id="language">
        <div className="settings-card__title">Language</div>
        <div className="indexer-list">
          <div className={cardClass("ui.mediaInfoLanguage", true)}>
            <div className="indexer-row indexer-row--settings">
              <span className="indexer-title">Media Info Language</span>
              <div className="indexer-actions">
                <select
                  value={ui.mediaInfoLanguage || "fr-FR"}
                  onChange={(e) => setUi((u) => ({ ...u, mediaInfoLanguage: e.target.value }))}
                >
                  {LANGUAGE_OPTIONS.map((language) => (
                    <option key={language.value} value={language.value}>
                      {language.label}
                    </option>
                  ))}
                </select>
              </div>
            </div>
            <div className="settings-help">
              Langue utilisee pour les infos media (TMDB: synopsis, titres, cast).
            </div>
            <div className="settings-help" style={{ color: "#f59e0b" }}>
              Rechargement du navigateur requis
            </div>
          </div>

          <div className={cardClass("ui.uiLanguage", true)}>
            <div className="indexer-row indexer-row--settings">
              <span className="indexer-title">UI Language</span>
              <div className="indexer-actions">
                <select
                  value={ui.uiLanguage || "fr-FR"}
                  onChange={(e) => setUi((u) => ({ ...u, uiLanguage: e.target.value }))}
                >
                  {LANGUAGE_OPTIONS.map((language) => (
                    <option key={language.value} value={language.value}>
                      {language.label}
                    </option>
                  ))}
                </select>
              </div>
            </div>
            <div className="settings-help">
              Langue de l&apos;interface (formatage dates/heures et preference UI).
            </div>
            <div className="settings-help" style={{ color: "#f59e0b" }}>
              Rechargement du navigateur requis
            </div>
          </div>
        </div>
      </div>

      <div className="settings-card" id="theme">
        <div className="settings-card__title">Thème</div>
        <div className={`indexer-card${pulseKeys.has("ui.theme") ? " pulse-ok" : ""}`}>
          <div className="indexer-row indexer-row--settings">
            <span className="indexer-title">Apparence</span>
            <div className="indexer-actions">
              <select
                value={ui.theme || "light"}
                onChange={(e) => handleThemeChange(e.target.value)}
              >
                <option value="system">Système</option>
                <option value="light">Clair</option>
                <option value="dark">Sombre</option>
              </select>
            </div>
          </div>
        </div>
      </div>

      <div className="settings-card" id="ui">
        <div className="settings-card__title">UI</div>
        <div className="indexer-list">
          <div className={cardClass("ui.hideSeen", !!ui.hideSeenByDefault)}>
            <div className="indexer-row indexer-row--settings">
              <span className="indexer-title">Marquer "vu" par défaut</span>
              <div className="indexer-actions">
                <span className="indexer-status">
                  {ui.hideSeenByDefault ? "Actif" : "Désactivé"}
                </span>
                <ToggleSwitch
                  checked={ui.hideSeenByDefault}
                  onIonChange={(e) => setUi((u) => ({ ...u, hideSeenByDefault: e.detail.checked }))}
                  className="settings-toggle"
                />
              </div>
            </div>
          </div>

          <div className={cardClass("ui.showCategories", !!ui.showCategories)}>
            <div className="indexer-row indexer-row--settings">
              <span className="indexer-title">Afficher les catégories</span>
              <div className="indexer-actions">
                <span className="indexer-status">
                  {ui.showCategories ? "Actif" : "Désactivé"}
                </span>
                <ToggleSwitch
                  checked={ui.showCategories}
                  onIonChange={(e) => setUi((u) => ({ ...u, showCategories: e.detail.checked }))}
                  className="settings-toggle"
                />
              </div>
            </div>
          </div>

          <div className={cardClass("ui.missingPosterView", !!ui.enableMissingPosterView)}>
            <div className="indexer-row indexer-row--settings">
              <span className="indexer-title">Activer Vue Sans poster</span>
              <div className="indexer-actions">
                <span className="indexer-status">
                  {ui.enableMissingPosterView ? "Actif" : "Désactivé"}
                </span>
                <ToggleSwitch
                  checked={ui.enableMissingPosterView}
                  onIonChange={(e) =>
                    setUi((u) => ({ ...u, enableMissingPosterView: e.detail.checked }))
                  }
                  className="settings-toggle"
                />
              </div>
            </div>
          </div>

          <div className={cardClass("ui.animations", !!ui.animationsEnabled)}>
            <div className="indexer-row indexer-row--settings">
              <span className="indexer-title">Animations de l'interface</span>
              <div className="indexer-actions">
                <span className="indexer-status">
                  {ui.animationsEnabled ? "Actif" : "Désactivé"}
                </span>
                <ToggleSwitch
                  checked={ui.animationsEnabled}
                  onIonChange={(e) =>
                    setUi((u) => ({ ...u, animationsEnabled: e.detail.checked }))
                  }
                  className="settings-toggle"
                />
              </div>
            </div>
          </div>
        </div>
      </div>

      <div className="settings-card" id="logs">
        <div className="settings-card__title">Logs</div>
        <div className="indexer-list">
          <div className={cardClass(null, !!ui.badgeInfo)}>
            <div className="indexer-row indexer-row--settings">
              <span className="indexer-title">Badge pour Info</span>
              <div className="indexer-actions">
                <span className="indexer-status">
                  {ui.badgeInfo ? "Actif" : "Désactivé"}
                </span>
                <ToggleSwitch
                  checked={ui.badgeInfo}
                  onIonChange={(e) => setUi((u) => ({ ...u, badgeInfo: e.detail.checked }))}
                  className="settings-toggle"
                />
              </div>
            </div>
          </div>

          <div className={cardClass(null, !!ui.badgeWarn)}>
            <div className="indexer-row indexer-row--settings">
              <span className="indexer-title">Badge pour Warn</span>
              <div className="indexer-actions">
                <span className="indexer-status">
                  {ui.badgeWarn ? "Actif" : "Désactivé"}
                </span>
                <ToggleSwitch
                  checked={ui.badgeWarn}
                  onIonChange={(e) => setUi((u) => ({ ...u, badgeWarn: e.detail.checked }))}
                  className="settings-toggle"
                />
              </div>
            </div>
          </div>

          <div className={cardClass(null, !!ui.badgeError)}>
            <div className="indexer-row indexer-row--settings">
              <span className="indexer-title">Badge pour Error</span>
              <div className="indexer-actions">
                <span className="indexer-status">
                  {ui.badgeError ? "Actif" : "Désactivé"}
                </span>
                <ToggleSwitch
                  checked={ui.badgeError}
                  onIonChange={(e) => setUi((u) => ({ ...u, badgeError: e.detail.checked }))}
                  className="settings-toggle"
                />
              </div>
            </div>
          </div>
        </div>
      </div>

      <div className="settings-card" id="defaults">
        <div className="settings-card__title">Bibliothèque</div>
        <div className="indexer-list">
          <div className={`indexer-card${pulseKeys.has("ui.defaultSort") ? " pulse-ok" : ""}`}>
            <div className="indexer-row indexer-row--settings">
              <span className="indexer-title">Tri</span>
              <div className="indexer-actions">
                <select
                  value={ui.defaultSort || "date"}
                  onChange={(e) => setUi((u) => ({ ...u, defaultSort: e.target.value }))}
                >
                  <option value="date">Date</option>
                  <option value="seeders">Seeders</option>
                  <option value="downloads">Téléchargé</option>
                </select>
              </div>
            </div>
          </div>

          <div className={`indexer-card${pulseKeys.has("ui.defaultView") ? " pulse-ok" : ""}`}>
            <div className="indexer-row indexer-row--settings">
              <span className="indexer-title">Vue</span>
              <div className="indexer-actions">
                <select
                  value={ui.defaultView || "grid"}
                  onChange={(e) => setUi((u) => ({ ...u, defaultView: e.target.value }))}
                >
                  <option value="grid">Cartes</option>
                  <option value="poster">Poster</option>
                  <option value="banner">Banner</option>
                  <option value="list">Liste</option>
                </select>
              </div>
            </div>
          </div>

          <div className={`indexer-card${pulseKeys.has("ui.defaultLimit") ? " pulse-ok" : ""}`}>
            <div className="indexer-row indexer-row--settings">
              <span className="indexer-title">Limite</span>
              <div className="indexer-actions">
                <select
                  value={ui.defaultLimit ?? 100}
                  onChange={(e) => {
                    const v = e.target.value;
                    setUi((u) => ({ ...u, defaultLimit: v === "0" ? 0 : Number(v) }));
                  }}
                >
                  <option value="50">50</option>
                  <option value="100">100</option>
                  <option value="200">200</option>
                  <option value="500">500</option>
                  <option value="0">Tous</option>
                </select>
              </div>
            </div>
          </div>
        </div>
      </div>

      <div className="settings-card" id="filter-defaults">
        <div className="settings-card__title">Filtre</div>
        <div className="indexer-list">
          <div className={`indexer-card${pulseKeys.has("ui.defaultMaxAgeDays") ? " pulse-ok" : ""}`}>
            <div className="indexer-row indexer-row--settings">
              <span className="indexer-title">Date</span>
              <div className="indexer-actions">
                <select
                  value={ui.defaultMaxAgeDays ?? ""}
                  onChange={(e) => setUi((u) => ({ ...u, defaultMaxAgeDays: e.target.value }))}
                >
                  <option value="">Tous</option>
                  <option value="1">1 jour</option>
                  <option value="2">2 jours</option>
                  <option value="3">3 jours</option>
                  <option value="7">7 jours</option>
                  <option value="15">15 jours</option>
                  <option value="30">30 jours</option>
                </select>
              </div>
            </div>
          </div>

          <div className={`indexer-card${pulseKeys.has("ui.defaultFilterSourceId") ? " pulse-ok" : ""}`}>
            <div className="indexer-row indexer-row--settings">
              <span className="indexer-title">Source</span>
              <div className="indexer-actions">
                <select
                  value={ui.defaultFilterSourceId ?? ""}
                  onChange={(e) => setUi((u) => ({ ...u, defaultFilterSourceId: e.target.value }))}
                >
                  <option value="">Toutes sources</option>
                  {sourceOptions.map((s) => (
                    <option key={s.value} value={s.value}>
                      {s.label}
                    </option>
                  ))}
                </select>
              </div>
            </div>
          </div>

          <div className={`indexer-card${pulseKeys.has("ui.defaultFilterCategoryId") ? " pulse-ok" : ""}`}>
            <div className="indexer-row indexer-row--settings">
              <span className="indexer-title">Catégorie</span>
              <div className="indexer-actions">
                <select
                  value={ui.defaultFilterCategoryId ?? ""}
                  onChange={(e) => setUi((u) => ({ ...u, defaultFilterCategoryId: e.target.value }))}
                >
                  <option value="">Toutes catégories</option>
                  {categoryOptions.map((c) => (
                    <option key={c.value} value={c.value}>
                      {c.label}
                    </option>
                  ))}
                </select>
              </div>
            </div>
          </div>

          <div className={`indexer-card${pulseKeys.has("ui.defaultFilterApplication") ? " pulse-ok" : ""}`}>
            <div className="indexer-row indexer-row--settings">
              <span className="indexer-title">Application</span>
              <div className="indexer-actions">
                <select
                  value={ui.defaultFilterApplication ?? ""}
                  onChange={(e) => setUi((u) => ({ ...u, defaultFilterApplication: e.target.value }))}
                >
                  <option value="">Toutes apps</option>
                  <option value="__hide_apps__">Masquer apps</option>
                  {appOptions.map((app) => (
                    <option key={app.value} value={app.value}>
                      {app.label}
                    </option>
                  ))}
                </select>
              </div>
            </div>
          </div>

          <div className={`indexer-card${pulseKeys.has("ui.defaultFilterSeen") ? " pulse-ok" : ""}`}>
            <div className="indexer-row indexer-row--settings">
              <span className="indexer-title">Vu</span>
              <div className="indexer-actions">
                <select
                  value={ui.defaultFilterSeen ?? ""}
                  onChange={(e) => setUi((u) => ({ ...u, defaultFilterSeen: e.target.value }))}
                >
                  <option value="">Tous</option>
                  <option value="1">Vu</option>
                  <option value="0">Pas vu</option>
                </select>
              </div>
            </div>
          </div>

          <div className={`indexer-card${pulseKeys.has("ui.defaultFilterQuality") ? " pulse-ok" : ""}`}>
            <div className="indexer-row indexer-row--settings">
              <span className="indexer-title">Qualité</span>
              <div className="indexer-actions">
                <select
                  value={ui.defaultFilterQuality ?? ""}
                  onChange={(e) => setUi((u) => ({ ...u, defaultFilterQuality: e.target.value }))}
                >
                  <option value="">Toutes qualités</option>
                  {FILTER_QUALITY_OPTIONS.map((q) => (
                    <option key={q} value={q}>
                      {q}
                    </option>
                  ))}
                </select>
              </div>
            </div>
          </div>
        </div>
      </div>

    </>
  );
}
