import React from "react";

export default function SettingsGeneral({
  hostPort,
  urlBase,
}) {
  return (
    <div className="settings-card" id="host">
      <div className="settings-card__title">Host</div>
      <div className="indexer-list">
        <div className="indexer-card">
          <div className="indexer-row indexer-row--settings">
            <span className="indexer-title">Port Number</span>
            <div className="indexer-actions">
              <input
                type="number"
                value={hostPort}
                readOnly
                disabled
                className="input--readonly"
              />
            </div>
          </div>
          <div className="settings-help">
            Configured via server settings (environment variable or appsettings.json)
          </div>
        </div>
        <div className="indexer-card">
          <div className="indexer-row indexer-row--settings">
            <span className="indexer-title">URL Base</span>
            <div className="indexer-actions">
              <input
                type="text"
                value={urlBase || "(none)"}
                readOnly
                disabled
                className="input--readonly"
              />
            </div>
          </div>
          <div className="settings-help">
            For reverse proxy support. Configured via server settings.
          </div>
        </div>
      </div>
    </div>
  );
}
