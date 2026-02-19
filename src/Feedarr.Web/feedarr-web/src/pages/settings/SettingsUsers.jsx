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
            <span className="indexer-title">Authentication</span>
            <div className="indexer-actions">
              <select
                value={security.authentication}
                onChange={(e) => setSecurity((s) => ({ ...s, authentication: e.target.value }))}
              >
                <option value="none">None</option>
                <option value="basic">Basic (Browser Popup)</option>
              </select>
            </div>
          </div>
        </div>

        {security.authentication === "basic" && (
          <>
            <div className="indexer-card">
              <div className="indexer-row indexer-row--settings">
                <span className="indexer-title">Authentication Required</span>
                <div className="indexer-actions">
                  <select
                    value={security.authenticationRequired}
                    onChange={(e) => setSecurity((s) => ({ ...s, authenticationRequired: e.target.value }))}
                  >
                    <option value="local">Disabled for Local Addresses</option>
                    <option value="all">Enabled for all Addresses</option>
                  </select>
                </div>
              </div>
              <div className="settings-help">
                Require Username and Password to access Feedarr
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
