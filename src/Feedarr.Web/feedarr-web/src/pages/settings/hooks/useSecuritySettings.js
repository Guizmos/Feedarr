import { useCallback, useState } from "react";
import { apiGet, apiPut } from "../../../api/client.js";
import { getSecurityText } from "../securityI18n.js";

function isLocalHost(host) {
  if (!host) return false;
  const normalized = String(host).trim().replace(/^\[(.*)\]$/, "$1").toLowerCase();
  return normalized === "localhost" || normalized === "127.0.0.1" || normalized === "::1";
}

function isExposedPublicBaseUrl(publicBaseUrl) {
  const raw = String(publicBaseUrl || "").trim();
  if (!raw) return false;
  try {
    const parsed = new URL(raw);
    return !isLocalHost(parsed.hostname);
  } catch {
    return false;
  }
}

function isPasswordComplexityError(error) {
  const title = String(error?.title || "").toLowerCase();
  const message = String(error?.message || "").toLowerCase();
  return title === "password_complexity_required" || message.includes("password_complexity_required");
}

function formatPasswordComplexityMessage(error) {
  const t = getSecurityText();
  const req = error?.requirements;
  if (req && typeof req === "object") {
    const minLength = Number(req.minLength);
    const clauses = [];
    if (req.requireUpper) clauses.push(t("settings.security.error.passwordClause.upper"));
    if (req.requireLower) clauses.push(t("settings.security.error.passwordClause.lower"));
    if (req.requireDigit) clauses.push(t("settings.security.error.passwordClause.digit"));
    if (req.requireSymbol) clauses.push(t("settings.security.error.passwordClause.symbol"));
    if (minLength > 0 && clauses.length > 0) {
      return `${t("settings.security.error.passwordComplexityPrefix")} ${minLength} ${t("settings.security.error.passwordComplexityWithAtLeast")} ${clauses.join(", ")}.`;
    }
  }
  return t("settings.security.error.passwordComplexityFallback");
}

function markSecuritySettingsError(error) {
  if (error && typeof error === "object") {
    error.isSecuritySettingsError = true;
    return error;
  }
  const wrapped = new Error(String(error || "Security settings error"));
  wrapped.isSecuritySettingsError = true;
  return wrapped;
}

export default function useSecuritySettings() {
  const [loaded, setLoaded] = useState(false);
  const [security, setSecurityState] = useState({
    authMode: "smart",
    publicBaseUrl: "",
    username: "",
    password: "",
    passwordConfirmation: "",
    hasPassword: false,
    authConfigured: false,
    authRequired: false,
  });
  const [initialSecurity, setInitialSecurity] = useState({
    authMode: "smart",
    publicBaseUrl: "",
    username: "",
  });
  const [securityErrors, setSecurityErrors] = useState([]);
  const [securityMessage, setSecurityMessage] = useState("");
  const [passwordMessage, setPasswordMessage] = useState("");

  // ── Derived state ────────────────────────────────────────────────────────

  const isDirty =
    JSON.stringify({
      authMode: security.authMode,
      publicBaseUrl: security.publicBaseUrl,
      username: security.username,
    }) !== JSON.stringify(initialSecurity) ||
    !!security.password ||
    !!security.passwordConfirmation;

  // Mode
  const isStrict = security.authMode === "strict";
  const isSmart = security.authMode === "smart";
  const isProtectedMode = isStrict || isSmart;
  const serverHasPassword = security.hasPassword;

  // Trimmed values for comparison (avoids repeated calls)
  const usernameValue = String(security.username || "").trim();
  const passwordValue = String(security.password || "").trim();
  const confirmValue = String(security.passwordConfirmation || "").trim();

  // Banner: existing logic for public-URL / proxy exposure warning (unrelated to field states)
  const isExposedConfig = isExposedPublicBaseUrl(security.publicBaseUrl);
  const effectiveAuthRequired = isStrict ? true : !!security.authRequired;
  const statusRequiresCredentials = isSmart && effectiveAuthRequired && !security.authConfigured;
  const credentialsRequiredForMode = isStrict || (isSmart && (isExposedConfig || statusRequiresCredentials));

  // ── Validation ───────────────────────────────────────────────────────────
  //
  // strict: credentials always required.
  // smart: credentials required only when exposed / authRequired.
  // In non-required smart mode, editing password still activates full credential validation.

  const requiresCredsToSave = loaded && isProtectedMode && credentialsRequiredForMode && !security.authConfigured;
  const userIsEditingCreds = !!passwordValue || !!confirmValue;
  const credsRequired =
    loaded &&
    isProtectedMode &&
    (requiresCredsToSave || userIsEditingCreds || (credentialsRequiredForMode && !serverHasPassword));

  const usernameRequired = loaded && isProtectedMode && (credentialsRequiredForMode || credsRequired);
  const passwordRequired = credsRequired;
  const confirmRequired = credsRequired;

  // ── Field visual states: "error" | "valid" | "" ──────────────────────────
  //
  // Gate: !loaded || open mode → always "" (no flash, nothing required).
  //
  // When already configured and idle → green on password fields.
  // Username keeps live validation in protected modes.

  const isConfiguredIdle =
    loaded &&
    isProtectedMode &&
    credentialsRequiredForMode &&
    security.authConfigured &&
    !!usernameValue &&
    serverHasPassword &&
    !userIsEditingCreds;

  const usernameFieldState = usernameRequired
    ? (usernameValue ? "valid" : "error")
    : "";

  const passwordFieldState = !loaded || !isProtectedMode
    ? ""
    : isConfiguredIdle
    ? "valid"
    : passwordRequired
    ? (passwordValue ? "valid" : "error")
    : "";

  const confirmFieldState = !loaded || !isProtectedMode
    ? ""
    : isConfiguredIdle
    ? "valid"
    : confirmRequired
    ? (!confirmValue || security.password !== security.passwordConfirmation ? "error" : "valid")
    : "";

  // ── Save blocking ─────────────────────────────────────────────────────────
  //
  const usernameMissing = usernameRequired && !usernameValue;
  const passwordMissing = passwordRequired && !passwordValue;
  const confirmMissing =
    confirmRequired &&
    (!confirmValue || security.password !== security.passwordConfirmation);

  const credsMissing = usernameMissing || passwordMissing || confirmMissing;

  const requiresDowngradeConfirmation =
    loaded &&
    security.authMode === "open" &&
    (initialSecurity.authMode !== "open" || security.authConfigured);

  // Save requires: data loaded + something changed + no validation errors
  const canSave = loaded && isDirty && !credsMissing;

  const showExistingCredentialsHint =
    loaded &&
    isProtectedMode &&
    security.authConfigured &&
    serverHasPassword &&
    !userIsEditingCreds;

  // ── Actions ──────────────────────────────────────────────────────────────

  const setSecurity = useCallback((updater) => {
    setSecurityMessage("");
    setSecurityErrors([]);
    setPasswordMessage("");
    setSecurityState(updater);
  }, []);

  const loadSecuritySettings = useCallback(async () => {
    try {
      const sec = await apiGet("/api/settings/security");
      if (sec) {
        setSecurityState((prev) => ({
          ...prev,
          authMode: sec.authMode || "smart",
          publicBaseUrl: sec.publicBaseUrl || "",
          username: sec.username || "",
          hasPassword: !!sec.hasPassword,
          authConfigured: !!sec.authConfigured,
          authRequired: !!sec.authRequired,
          password: "",
          passwordConfirmation: "",
        }));
        setInitialSecurity({
          authMode: sec.authMode || "smart",
          publicBaseUrl: sec.publicBaseUrl || "",
          username: sec.username || "",
        });
        setSecurityErrors([]);
        setSecurityMessage("");
        setPasswordMessage("");
      }
    } catch {
      // Ignore load errors — keep default state, show no red
    } finally {
      // Always mark loaded so validation can activate
      setLoaded(true);
    }
  }, []);

  const saveSecuritySettings = useCallback(async (options = {}) => {
    const t = getSecurityText();
    const credsWarning = t("settings.security.notice.credsRequired");
    const downgradeWarning = t("settings.security.warning.downgradeOpen");

    setSecurityErrors([]);
    setSecurityMessage("");
    setPasswordMessage("");

    // Defensive guard (mirrors canSave — should never fire when save button is properly disabled)
    if (credsMissing) {
      const next = [];
      if (usernameMissing) next.push("username");
      if (passwordMissing || confirmMissing) next.push("password", "passwordConfirmation");
      setSecurityErrors(next);
      setSecurityMessage(credsWarning);
      throw markSecuritySettingsError(new Error(credsWarning));
    }
    const shouldAllowDowngrade = options?.allowDowngradeToOpen === true;
    if (requiresDowngradeConfirmation && !shouldAllowDowngrade) {
      setSecurityMessage(downgradeWarning);
      throw markSecuritySettingsError(new Error(downgradeWarning));
    }

    const payload = {
      authMode: security.authMode,
      publicBaseUrl: security.publicBaseUrl,
      username: security.username,
    };
    if (requiresDowngradeConfirmation && shouldAllowDowngrade) {
      payload.allowDowngradeToOpen = true;
    }

    if (security.authMode !== "open" && (security.password || security.passwordConfirmation)) {
      payload.password = security.password;
      payload.passwordConfirmation = security.passwordConfirmation;
    }

    try {
      const saved = await apiPut("/api/settings/security", payload);
      setInitialSecurity({
        authMode: saved?.authMode ?? security.authMode,
        publicBaseUrl: saved?.publicBaseUrl ?? security.publicBaseUrl,
        username: saved?.username ?? security.username,
      });
      setSecurityState((prev) => ({
        ...prev,
        authMode: saved?.authMode ?? prev.authMode,
        publicBaseUrl: saved?.publicBaseUrl ?? prev.publicBaseUrl,
        username: saved?.username ?? prev.username,
        hasPassword: !!saved?.hasPassword,
        authConfigured: !!saved?.authConfigured,
        authRequired: !!saved?.authRequired,
        password: "",
        passwordConfirmation: "",
      }));
      setSecurityMessage("");
    } catch (e) {
      if (isPasswordComplexityError(e)) {
        const message = formatPasswordComplexityMessage(e);
        setSecurityErrors(["password", "passwordConfirmation"]);
        setPasswordMessage(message);
        setSecurityMessage(message);
        if (e && typeof e === "object") e.message = message;
        throw markSecuritySettingsError(e);
      }
      if (String(e?.error || "").toLowerCase() === "downgrade_confirmation_required") {
        setSecurityMessage(downgradeWarning);
        throw markSecuritySettingsError(e);
      }
      if (typeof e?.message === "string") {
        const msgLower = e.message.toLowerCase();
        const next = [];
        if (
          msgLower.includes("credentials_required") ||
          msgLower.includes("credentials are required") ||
          msgLower.includes("identifiants requis")
        ) {
          if (!usernameValue) next.push("username");
          if (!passwordValue) next.push("password", "passwordConfirmation");
          setSecurityMessage(credsWarning);
        } else if (msgLower.includes("password and confirmation required")) {
          setSecurityMessage(t("settings.security.error.passwordAndConfirmationRequired"));
        } else if (msgLower.includes("password confirmation mismatch")) {
          setSecurityMessage(t("settings.security.error.passwordConfirmationMismatch"));
        } else {
          if (msgLower.includes("username")) next.push("username");
          if (msgLower.includes("password")) next.push("password", "passwordConfirmation");
          if (next.length > 0) setSecurityMessage(e.message);
        }
        if (next.length > 0) setSecurityErrors(next);
      }
      throw markSecuritySettingsError(e);
    }
  }, [
    credsMissing,
    usernameMissing,
    passwordMissing,
    confirmMissing,
    requiresDowngradeConfirmation,
    security,
    usernameValue,
    passwordValue,
  ]);

  return {
    security,
    setSecurity,
    initialSecurity,
    loaded,
    securityErrors,
    securityMessage,
    passwordMessage,
    requiresDowngradeConfirmation,
    credentialsRequiredForMode,
    effectiveAuthRequired,
    usernameRequired,
    passwordRequired,
    confirmRequired,
    credsRequired,
    credsMissing,
    canSave,
    isDirty,
    showExistingCredentialsHint,
    usernameFieldState,
    passwordFieldState,
    confirmFieldState,
    loadSecuritySettings,
    saveSecuritySettings,
  };
}
