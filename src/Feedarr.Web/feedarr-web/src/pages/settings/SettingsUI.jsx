import React from "react";
import ToggleSwitch from "../../ui/ToggleSwitch.jsx";

export default function SettingsUI({
  ui,
  setUi,
  pulseKeys,
  handleThemeChange,
}) {
  const cardClass = (pulseKey, enabled) =>
    `indexer-card${pulseKey && pulseKeys.has(pulseKey) ? " pulse-ok" : ""}${enabled ? "" : " is-disabled"}`;

  return (
    <>
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
    </>
  );
}
