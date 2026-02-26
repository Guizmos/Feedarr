import React, { useEffect, useRef } from "react";

export default function SettingsUsers({
  security,
  setSecurity,
  securityErrors,
  securityMessage,
  credentialsRequiredForMode,
}) {
  const usernameInputRef = useRef(null);
  const passwordInputRef = useRef(null);

  useEffect(() => {
    if (securityErrors.includes("username")) {
      usernameInputRef.current?.focus();
      return;
    }
    if (securityErrors.includes("password")) {
      passwordInputRef.current?.focus();
    }
  }, [securityErrors]);

  return (
    <div className="settings-card" id="security">
      <div className="settings-card__title">Authentification</div>
      {credentialsRequiredForMode && (
        <div className="onboarding__error">
          Credentials required: in Smart/Strict mode when auth is required (public URL or proxy), set username and password.
        </div>
      )}
      {!!securityMessage && <div className="onboarding__error">{securityMessage}</div>}
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
                    ref={usernameInputRef}
                    type="text"
                    value={security.username}
                    onChange={(e) => setSecurity((s) => ({ ...s, username: e.target.value }))}
                    className={securityErrors.includes("username") ? "is-error" : ""}
                    required={credentialsRequiredForMode}
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
                    ref={passwordInputRef}
                    type="password"
                    value={security.password}
                    onChange={(e) => setSecurity((s) => ({ ...s, password: e.target.value }))}
                    className={securityErrors.includes("password") ? "is-error" : ""}
                    required={credentialsRequiredForMode && !security.hasPassword}
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
                    required={credentialsRequiredForMode && !security.hasPassword}
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
