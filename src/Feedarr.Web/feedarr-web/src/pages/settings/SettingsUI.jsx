import React from "react";
import ToggleSwitch from "../../ui/ToggleSwitch.jsx";
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

function SettingsRowCard({
  pulseClass,
  title,
  help,
  warning,
  error,
  status,
  control,
}) {
  return (
    <div className={`indexer-card${pulseClass}`}>
      <div className="indexer-row indexer-row--settings">
        <span className="indexer-title">{title}</span>
        <div className="indexer-actions">
          {status && <span className="indexer-status">{status}</span>}
          {control}
        </div>
      </div>
      {help && <div className="settings-help">{help}</div>}
      {warning && <div className="settings-help" style={{ color: "#f59e0b" }}>{warning}</div>}
      {error && <div className="settings-help" style={{ color: "var(--danger, #ef4444)" }}>{error}</div>}
    </div>
  );
}

export default function SettingsUI({
  ui,
  setUiField,
  pulseKinds,
  fieldErrors,
  saveError,
  handleThemeChange,
  sourceOptions,
  appOptions,
  categoryOptions,
}) {
  const pulseClass = (key) => {
    const kind = pulseKinds?.[key];
    return kind === "err" ? " pulse-err" : kind === "ok" ? " pulse-ok" : "";
  };

  const toggleStatus = (value) => (value ? "Actif" : "Désactivé");

  return (
    <>
      {saveError && (
        <div className="errorbox" style={{ marginBottom: 12 }}>
          <div className="errorbox__title">Erreur</div>
          <div className="muted">{saveError}</div>
        </div>
      )}

      <div className="settings-card" id="language">
        <div className="settings-card__title">Language</div>
        <div className="indexer-list">
          <SettingsRowCard
            pulseClass={pulseClass("ui.mediaInfoLanguage")}
            title="Media Info Language"
            help="Langue utilisee pour les infos media (TMDB: synopsis, titres, cast)."
            warning="Rechargement du navigateur requis"
            error={fieldErrors?.mediaInfoLanguage}
            control={(
              <select
                value={ui.mediaInfoLanguage || "fr-FR"}
                onChange={(e) => setUiField("mediaInfoLanguage", e.target.value)}
              >
                {LANGUAGE_OPTIONS.map((language) => (
                  <option key={language.value} value={language.value}>
                    {language.label}
                  </option>
                ))}
              </select>
            )}
          />

          <SettingsRowCard
            pulseClass={pulseClass("ui.uiLanguage")}
            title="UI Language"
            help="Langue de l'interface (formatage dates/heures et preference UI)."
            warning="Rechargement du navigateur requis"
            error={fieldErrors?.uiLanguage}
            control={(
              <select
                value={ui.uiLanguage || "fr-FR"}
                onChange={(e) => setUiField("uiLanguage", e.target.value)}
              >
                {LANGUAGE_OPTIONS.map((language) => (
                  <option key={language.value} value={language.value}>
                    {language.label}
                  </option>
                ))}
              </select>
            )}
          />
        </div>
      </div>

      <div className="settings-card" id="theme">
        <div className="settings-card__title">Theme</div>
        <SettingsRowCard
          pulseClass={pulseClass("ui.theme")}
          title="Apparence"
          error={fieldErrors?.theme}
          control={(
            <select
              value={ui.theme || "light"}
              onChange={(e) => handleThemeChange(e.target.value)}
            >
              <option value="system">Système</option>
              <option value="light">Clair</option>
              <option value="dark">Sombre</option>
            </select>
          )}
        />
      </div>

      <div className="settings-card" id="ui">
        <div className="settings-card__title">UI</div>
        <div className="indexer-list">
          <SettingsRowCard
            pulseClass={pulseClass("ui.hideSeen")}
            title='Marquer "vu" par défaut'
            status={toggleStatus(ui.hideSeenByDefault)}
            control={(
              <ToggleSwitch
                checked={ui.hideSeenByDefault}
                onIonChange={(e) => setUiField("hideSeenByDefault", e.detail.checked)}
                className="settings-toggle"
              />
            )}
          />

          <SettingsRowCard
            pulseClass={pulseClass("ui.showCategories")}
            title="Afficher les catégories"
            status={toggleStatus(ui.showCategories)}
            control={(
              <ToggleSwitch
                checked={ui.showCategories}
                onIonChange={(e) => setUiField("showCategories", e.detail.checked)}
                className="settings-toggle"
              />
            )}
          />

          <SettingsRowCard
            pulseClass={pulseClass("ui.top24DedupeControl")}
            title="Afficher le filtre dedup Top 24h"
            status={toggleStatus(ui.showTop24DedupeControl)}
            control={(
              <ToggleSwitch
                checked={ui.showTop24DedupeControl}
                onIonChange={(e) => setUiField("showTop24DedupeControl", e.detail.checked)}
                className="settings-toggle"
              />
            )}
          />

          <SettingsRowCard
            pulseClass={pulseClass("ui.missingPosterView")}
            title="Activer Vue Sans poster"
            status={toggleStatus(ui.enableMissingPosterView)}
            control={(
              <ToggleSwitch
                checked={ui.enableMissingPosterView}
                onIonChange={(e) => setUiField("enableMissingPosterView", e.detail.checked)}
                className="settings-toggle"
              />
            )}
          />

          <SettingsRowCard
            pulseClass={pulseClass("ui.animations")}
            title="Animations de l'interface"
            status={toggleStatus(ui.animationsEnabled)}
            control={(
              <ToggleSwitch
                checked={ui.animationsEnabled}
                onIonChange={(e) => setUiField("animationsEnabled", e.detail.checked)}
                className="settings-toggle"
              />
            )}
          />
        </div>
      </div>

      <div className="settings-card" id="logs">
        <div className="settings-card__title">Logs</div>
        <div className="indexer-list">
          <SettingsRowCard
            pulseClass={pulseClass("ui.badgeInfo")}
            title="Badge pour Info"
            status={toggleStatus(ui.badgeInfo)}
            control={(
              <ToggleSwitch
                checked={ui.badgeInfo}
                onIonChange={(e) => setUiField("badgeInfo", e.detail.checked)}
                className="settings-toggle"
              />
            )}
          />

          <SettingsRowCard
            pulseClass={pulseClass("ui.badgeWarn")}
            title="Badge pour Warn"
            status={toggleStatus(ui.badgeWarn)}
            control={(
              <ToggleSwitch
                checked={ui.badgeWarn}
                onIonChange={(e) => setUiField("badgeWarn", e.detail.checked)}
                className="settings-toggle"
              />
            )}
          />

          <SettingsRowCard
            pulseClass={pulseClass("ui.badgeError")}
            title="Badge pour Error"
            status={toggleStatus(ui.badgeError)}
            control={(
              <ToggleSwitch
                checked={ui.badgeError}
                onIonChange={(e) => setUiField("badgeError", e.detail.checked)}
                className="settings-toggle"
              />
            )}
          />
        </div>
      </div>

      <div className="settings-card" id="defaults">
        <div className="settings-card__title">Bibliothèque</div>
        <div className="indexer-list">
          <SettingsRowCard
            pulseClass={pulseClass("ui.defaultSort")}
            title="Tri"
            error={fieldErrors?.defaultSort}
            control={(
              <select
                value={ui.defaultSort || "date"}
                onChange={(e) => setUiField("defaultSort", e.target.value)}
              >
                <option value="date">Date</option>
                <option value="seeders">Seeders</option>
                <option value="downloads">Téléchargé</option>
              </select>
            )}
          />

          <SettingsRowCard
            pulseClass={pulseClass("ui.defaultView")}
            title="Vue"
            error={fieldErrors?.defaultView}
            control={(
              <select
                value={ui.defaultView || "grid"}
                onChange={(e) => setUiField("defaultView", e.target.value)}
              >
                <option value="grid">Cartes</option>
                <option value="poster">Poster</option>
                <option value="banner">Banner</option>
                <option value="list">Liste</option>
              </select>
            )}
          />

          <SettingsRowCard
            pulseClass={pulseClass("ui.defaultLimit")}
            title="Limite"
            error={fieldErrors?.defaultLimit}
            control={(
              <select
                value={ui.defaultLimit ?? 100}
                onChange={(e) => {
                  const value = e.target.value;
                  setUiField("defaultLimit", value === "0" ? 0 : Number(value));
                }}
              >
                <option value="50">50</option>
                <option value="100">100</option>
                <option value="200">200</option>
                <option value="500">500</option>
                <option value="0">Tous</option>
              </select>
            )}
          />
        </div>
      </div>

      <div className="settings-card" id="filter-defaults">
        <div className="settings-card__title">Filtre</div>
        <div className="indexer-list">
          <SettingsRowCard
            pulseClass={pulseClass("ui.defaultMaxAgeDays")}
            title="Date"
            error={fieldErrors?.defaultMaxAgeDays}
            control={(
              <select
                value={ui.defaultMaxAgeDays ?? ""}
                onChange={(e) => setUiField("defaultMaxAgeDays", e.target.value)}
              >
                <option value="">Tous</option>
                <option value="1">1 jour</option>
                <option value="2">2 jours</option>
                <option value="3">3 jours</option>
                <option value="7">7 jours</option>
                <option value="15">15 jours</option>
                <option value="30">30 jours</option>
              </select>
            )}
          />

          <SettingsRowCard
            pulseClass={pulseClass("ui.defaultFilterSourceId")}
            title="Source"
            error={fieldErrors?.defaultFilterSourceId}
            control={(
              <select
                value={ui.defaultFilterSourceId ?? ""}
                onChange={(e) => setUiField("defaultFilterSourceId", e.target.value)}
              >
                <option value="">Toutes sources</option>
                {sourceOptions.map((source) => (
                  <option key={source.value} value={source.value}>
                    {source.label}
                  </option>
                ))}
              </select>
            )}
          />

          <SettingsRowCard
            pulseClass={pulseClass("ui.defaultFilterCategoryId")}
            title="Catégorie"
            error={fieldErrors?.defaultFilterCategoryId}
            control={(
              <select
                value={ui.defaultFilterCategoryId ?? ""}
                onChange={(e) => setUiField("defaultFilterCategoryId", e.target.value)}
              >
                <option value="">Toutes catégories</option>
                {categoryOptions.map((category) => (
                  <option key={category.value} value={category.value}>
                    {category.label}
                  </option>
                ))}
              </select>
            )}
          />

          <SettingsRowCard
            pulseClass={pulseClass("ui.defaultFilterApplication")}
            title="Application"
            error={fieldErrors?.defaultFilterApplication}
            control={(
              <select
                value={ui.defaultFilterApplication ?? ""}
                onChange={(e) => setUiField("defaultFilterApplication", e.target.value)}
              >
                <option value="">Toutes apps</option>
                <option value="__hide_apps__">Masquer apps</option>
                {appOptions.map((app) => (
                  <option key={app.value} value={app.value}>
                    {app.label}
                  </option>
                ))}
              </select>
            )}
          />

          <SettingsRowCard
            pulseClass={pulseClass("ui.defaultFilterSeen")}
            title="Vu"
            error={fieldErrors?.defaultFilterSeen}
            control={(
              <select
                value={ui.defaultFilterSeen ?? ""}
                onChange={(e) => setUiField("defaultFilterSeen", e.target.value)}
              >
                <option value="">Tous</option>
                <option value="1">Vu</option>
                <option value="0">Pas vu</option>
              </select>
            )}
          />

          <SettingsRowCard
            pulseClass={pulseClass("ui.defaultFilterQuality")}
            title="Qualité"
            error={fieldErrors?.defaultFilterQuality}
            control={(
              <select
                value={ui.defaultFilterQuality ?? ""}
                onChange={(e) => setUiField("defaultFilterQuality", e.target.value)}
              >
                <option value="">Toutes qualités</option>
                {FILTER_QUALITY_OPTIONS.map((quality) => (
                  <option key={quality} value={quality}>
                    {quality}
                  </option>
                ))}
              </select>
            )}
          />
        </div>
      </div>
    </>
  );
}
