import React, { useEffect, useRef } from "react";

export default function SettingsUsers({
  security,
  setSecurity,
  securityErrors,
  securityMessage,
  passwordMessage,
  showExistingCredentialsHint,
  allowDowngradeToOpen,
  setAllowDowngradeToOpen,
  requiresDowngradeConfirmation,
  credentialsRequiredForMode,
  usernameRequired,
  passwordRequired,
  confirmRequired,
  usernameFieldState,
  passwordFieldState,
  confirmFieldState,
}) {
  const usernameInputRef = useRef(null);
  const passwordInputRef = useRef(null);

  const fieldBorder = (state) => ({
    border:
      state === "error"
        ? "3px solid #ef4444"
        : state === "valid"
        ? "3px solid #22c55e"
        : "3px solid rgba(148, 163, 184, 0.5)",
    borderRadius: 6,
    padding: 2,
    background:
      state === "error"
        ? "rgba(239, 68, 68, 0.08)"
        : state === "valid"
        ? "rgba(34, 197, 94, 0.08)"
        : "rgba(148, 163, 184, 0.06)",
    display: "inline-block",
  });

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
      {credentialsRequiredForMode && !security.authConfigured && (
        <div className="onboarding__error">
          Credentials required: in Smart/Strict mode when auth is required (public URL or proxy), set username and password.
        </div>
      )}
      {requiresDowngradeConfirmation && (
        <div className="onboarding__warn">
          <div>Passer en mode Open desactive l&apos;authentification.</div>
          <label style={{ display: "inline-flex", gap: 8, alignItems: "center", marginTop: 6 }}>
            <input
              type="checkbox"
              checked={allowDowngradeToOpen}
              onChange={(e) => setAllowDowngradeToOpen(e.target.checked)}
            />
            Je confirme desactiver l&apos;authentification
          </label>
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
                  <div
                    style={fieldBorder(securityErrors.includes("username") ? "error" : usernameFieldState)}
                    data-state={usernameFieldState || "neutral"}
                  >
                    <input
                      ref={usernameInputRef}
                      type="text"
                      value={security.username}
                      onChange={(e) => setSecurity((s) => ({ ...s, username: e.target.value }))}
                      required={usernameRequired}
                      placeholder="Enter username"
                    />
                  </div>
                </div>
              </div>
            </div>

            <div className="indexer-card">
              <div className="indexer-row indexer-row--settings">
                <span className="indexer-title">Password</span>
                <div className="indexer-actions">
                  <div
                    style={fieldBorder(securityErrors.includes("password") ? "error" : passwordFieldState)}
                    data-state={passwordFieldState || "neutral"}
                  >
                    <input
                      ref={passwordInputRef}
                      type="password"
                      value={security.password}
                      onChange={(e) => setSecurity((s) => ({ ...s, password: e.target.value }))}
                      required={passwordRequired}
                      placeholder={security.hasPassword ? "Leave blank to keep current" : "Enter password"}
                    />
                  </div>
                </div>
              </div>
              {showExistingCredentialsHint && (
                <div className="onboarding__hint">
                  Identifiants deja configures. Laisse vide pour conserver le mot de passe actuel.
                </div>
              )}
              {!!passwordMessage && <div className="onboarding__error">{passwordMessage}</div>}
            </div>

            <div className="indexer-card">
              <div className="indexer-row indexer-row--settings">
                <span className="indexer-title">Password Confirmation</span>
                <div className="indexer-actions">
                  <div
                    style={fieldBorder(securityErrors.includes("passwordConfirmation") ? "error" : confirmFieldState)}
                    data-state={confirmFieldState || "neutral"}
                  >
                    <input
                      type="password"
                      value={security.passwordConfirmation}
                      onChange={(e) => setSecurity((s) => ({ ...s, passwordConfirmation: e.target.value }))}
                      required={confirmRequired}
                      placeholder="Confirm password"
                    />
                  </div>
                </div>
              </div>
            </div>
          </>
        )}
      </div>
    </div>
  );
}
