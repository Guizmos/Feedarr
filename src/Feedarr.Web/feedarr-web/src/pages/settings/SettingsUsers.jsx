import React, { useEffect, useRef, useState } from "react";
import InlineNotice from "./InlineNotice.jsx";
import { getSecurityText } from "./securityI18n.js";

export default function SettingsUsers({
  security,
  setSecurity,
  securityErrors,
  securityMessage,
  passwordMessage,
  showExistingCredentialsHint,
  credentialsRequiredForMode,
  effectiveAuthRequired,
  usernameRequired,
  passwordRequired,
  confirmRequired,
  usernameFieldState,
  passwordFieldState,
  confirmFieldState,
}) {
  const usernameInputRef = useRef(null);
  const passwordInputRef = useRef(null);
  const t = getSecurityText();
  const CREDENTIALS_REQUIRED_MESSAGE = t("settings.security.notice.credsRequired");
  const EXISTING_CREDENTIALS_MESSAGE = t("settings.security.notice.credsExisting");
  const prevFieldStatesRef = useRef({
    username: "",
    password: "",
    confirm: "",
  });
  const pulseTimerRef = useRef(null);
  const [pulseKeys, setPulseKeys] = useState(() => new Set());

  useEffect(() => {
    return () => {
      if (pulseTimerRef.current) {
        clearTimeout(pulseTimerRef.current);
      }
    };
  }, []);

  useEffect(() => {
    if (securityErrors.includes("username")) {
      usernameInputRef.current?.focus();
      return;
    }
    if (securityErrors.includes("password")) {
      passwordInputRef.current?.focus();
    }
  }, [securityErrors]);

  useEffect(() => {
    const nextStates = {
      username: securityErrors.includes("username") ? "error" : usernameFieldState,
      password: securityErrors.includes("password") ? "error" : passwordFieldState,
      confirm: securityErrors.includes("passwordConfirmation") ? "error" : confirmFieldState,
    };

    const changed = [];
    for (const key of Object.keys(nextStates)) {
      const prev = prevFieldStatesRef.current[key];
      const next = nextStates[key];
      if (next && next !== prev) changed.push(key);
    }

    prevFieldStatesRef.current = nextStates;

    if (changed.length > 0) {
      if (pulseTimerRef.current) clearTimeout(pulseTimerRef.current);
      setPulseKeys(new Set(changed));
      pulseTimerRef.current = setTimeout(() => {
        setPulseKeys(new Set());
      }, 850);
    }
  }, [
    securityErrors,
    usernameFieldState,
    passwordFieldState,
    confirmFieldState,
  ]);

  const fieldClassName = (key, state) => {
    const resolvedState = state === "error" || state === "valid" ? state : "";
    let classes = "fieldWrap";
    if (resolvedState === "error") classes += " fieldWrap--error";
    if (resolvedState === "valid") classes += " fieldWrap--valid";
    if (pulseKeys.has(key)) classes += " fieldWrap--pulse";
    return classes;
  };

  const normalizedSecurityMessage = String(passwordMessage || securityMessage || "").trim();
  const normalizedSecurityMessageLower = normalizedSecurityMessage.toLowerCase();
  const credentialsWarningInline = credentialsRequiredForMode && !security.authConfigured;
  const credentialsWarningLower = CREDENTIALS_REQUIRED_MESSAGE.toLowerCase();
  const credentialsWarningAltLower = "credentials required";
  const credentialsWarningAltFrLower = "identifiants requis";
  const messageLooksLikeCredentialsWarning =
    normalizedSecurityMessageLower.includes(credentialsWarningLower) ||
    normalizedSecurityMessageLower.includes(credentialsWarningAltLower) ||
    normalizedSecurityMessageLower.includes(credentialsWarningAltFrLower);

  const notices = [];
  if (credentialsWarningInline) {
    notices.push({ key: "warn-required", variant: "warning", message: CREDENTIALS_REQUIRED_MESSAGE });
  }
  if (normalizedSecurityMessage) {
    const duplicateCredentialsWarning =
      credentialsWarningInline &&
      (messageLooksLikeCredentialsWarning || normalizedSecurityMessage === CREDENTIALS_REQUIRED_MESSAGE);
    if (!duplicateCredentialsWarning) {
      notices.push({
        key: "security-message",
        variant: messageLooksLikeCredentialsWarning ? "warning" : "error",
        message: normalizedSecurityMessage,
      });
    }
  }
  if (showExistingCredentialsHint) {
    notices.push({ key: "info-existing", variant: "info", message: EXISTING_CREDENTIALS_MESSAGE });
  }

  return (
    <div className="settings-card" id="security">
      <div className="settings-card__title">{t("settings.security.title")}</div>
      <div className="indexer-list">
        <div className="indexer-card">
          <div className="indexer-row indexer-row--settings">
            <span className="indexer-title">{t("settings.security.authMode")}</span>
            <div className="indexer-actions">
              <select
                value={security.authMode}
                onChange={(e) => setSecurity((s) => ({ ...s, authMode: e.target.value }))}
              >
                <option value="smart">{t("settings.security.authMode.smart")}</option>
                <option value="strict">{t("settings.security.authMode.strict")}</option>
                <option value="open">{t("settings.security.authMode.none")}</option>
              </select>
            </div>
          </div>
          <div className="settings-help">
            {t("settings.security.help.smartExposure")}
          </div>
        </div>

        <div className="indexer-card">
          <div className="indexer-row indexer-row--settings">
            <span className="indexer-title">{t("settings.security.publicBaseUrl")}</span>
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
                <span className="indexer-title">{t("settings.security.status")}</span>
                <div className="indexer-actions">
                  <span className={`pill ${security.authConfigured ? "pill-ok" : "pill-warn"}`}>
                    {security.authConfigured
                      ? t("settings.security.status.configured")
                      : t("settings.security.status.notConfigured")}
                  </span>
                  <span className={`pill ${effectiveAuthRequired ? "pill-warn" : "pill-blue"}`}>
                    {effectiveAuthRequired
                      ? t("settings.security.status.authRequired")
                      : t("settings.security.status.authNotRequired")}
                  </span>
                </div>
              </div>
            </div>

            <div className="indexer-card">
              <div className="indexer-row indexer-row--settings">
                <span className="indexer-title">{t("settings.security.username")}</span>
                <div className="indexer-actions">
                  <div className={fieldClassName("username", securityErrors.includes("username") ? "error" : usernameFieldState)}>
                    <input
                      ref={usernameInputRef}
                      type="text"
                      value={security.username}
                      onChange={(e) => setSecurity((s) => ({ ...s, username: e.target.value }))}
                      required={usernameRequired}
                      placeholder={t("settings.security.username.placeholder")}
                    />
                  </div>
                </div>
              </div>
            </div>

            <div className="indexer-card">
              <div className="indexer-row indexer-row--settings">
                <span className="indexer-title">{t("settings.security.password")}</span>
                <div className="indexer-actions">
                  <div className={fieldClassName("password", securityErrors.includes("password") ? "error" : passwordFieldState)}>
                    <input
                      ref={passwordInputRef}
                      type="password"
                      value={security.password}
                      onChange={(e) => setSecurity((s) => ({ ...s, password: e.target.value }))}
                      required={passwordRequired}
                      placeholder={security.hasPassword
                        ? t("settings.security.password.placeholderKeepCurrent")
                        : t("settings.security.password.placeholderEnter")}
                    />
                  </div>
                </div>
              </div>
            </div>

            <div className="indexer-card">
              <div className="indexer-row indexer-row--settings">
                <span className="indexer-title">{t("settings.security.confirm")}</span>
                <div className="indexer-actions">
                  <div className={fieldClassName("confirm", securityErrors.includes("passwordConfirmation") ? "error" : confirmFieldState)}>
                    <input
                      type="password"
                      value={security.passwordConfirmation}
                      onChange={(e) => setSecurity((s) => ({ ...s, passwordConfirmation: e.target.value }))}
                      required={confirmRequired}
                      placeholder={t("settings.security.confirm.placeholder")}
                    />
                  </div>
                </div>
              </div>
            </div>
          </>
        )}
      </div>
      {notices.length > 0 && (
        <div className="security-notices">
          {notices.slice(0, 3).map((notice) => (
            <InlineNotice
              key={notice.key}
              variant={notice.variant}
              message={notice.message}
            />
          ))}
        </div>
      )}
    </div>
  );
}
