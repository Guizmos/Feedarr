import React from "react";

export default function SettingsUsers({
  security,
  setSecurity,
  securityErrors,
}) {
  return (
    <div className="settings-card" id="security">
      <div className="settings-card__title">Authentification</div>
      <div className="indexer-list">
        <div className="indexer-card">
          <div className="indexer-row indexer-row--settings">
            <span className="indexer-title">Auth Mode</span>
            <div className="indexer-actions">
              <select
                value={security.authMode}
                onChange={(e) => setSecurity((s) => ({ ...s, authMode: e.target.value }))}
              >
                <option value="smart">Smart (default)</option>
                <option value="strict">Strict</option>
                <option value="open">Open</option>
              </select>
            </div>
          </div>
          <div className="settings-help">
            Smart protects automatically when the instance is exposed
          </div>
        </div>

        <div className="indexer-card">
          <div className="indexer-row indexer-row--settings">
            <span className="indexer-title">Public Base URL</span>
            <div className="indexer-actions">
              <input
                type="text"
                value={security.publicBaseUrl || ""}
                onChange={(e) => setSecurity((s) => ({ ...s, publicBaseUrl: e.target.value }))}
                placeholder="https://example.com/feedarr"
              />
            </div>
          </div>
        </div>

        {security.authMode !== "open" && (
          <>
            <div className="indexer-card">
              <div className="indexer-row indexer-row--settings">
                <span className="indexer-title">Status</span>
                <div className="indexer-actions">
                  <span className={`pill ${security.authConfigured ? "pill-ok" : "pill-warn"}`}>
                    {security.authConfigured ? "Configured" : "Not configured"}
                  </span>
                  <span className={`pill ${security.authRequired ? "pill-warn" : "pill-blue"}`}>
                    {security.authRequired ? "Auth required" : "Auth not required"}
                  </span>
                </div>
              </div>
            </div>

            <div className="indexer-card">
              <div className="indexer-row indexer-row--settings">
                <span className="indexer-title">Username</span>
                <div className="indexer-actions">
                  <input
                    type="text"
                    value={security.username}
                    onChange={(e) => setSecurity((s) => ({ ...s, username: e.target.value }))}
                    className={securityErrors.includes("username") ? "is-error" : ""}
                    placeholder="Enter username"
                  />
                </div>
              </div>
            </div>

            <div className="indexer-card">
              <div className="indexer-row indexer-row--settings">
                <span className="indexer-title">Password</span>
                <div className="indexer-actions">
                  <input
                    type="password"
                    value={security.password}
                    onChange={(e) => setSecurity((s) => ({ ...s, password: e.target.value }))}
                    className={securityErrors.includes("password") ? "is-error" : ""}
                    placeholder={security.hasPassword ? "Leave blank to keep current" : "Enter password"}
                  />
                </div>
              </div>
            </div>

            <div className="indexer-card">
              <div className="indexer-row indexer-row--settings">
                <span className="indexer-title">Password Confirmation</span>
                <div className="indexer-actions">
                  <input
                    type="password"
                    value={security.passwordConfirmation}
                    onChange={(e) => setSecurity((s) => ({ ...s, passwordConfirmation: e.target.value }))}
                    className={securityErrors.includes("passwordConfirmation") ? "is-error" : ""}
                    placeholder="Confirm password"
                  />
                </div>
              </div>
            </div>
          </>
        )}
      </div>
    </div>
  );
}
