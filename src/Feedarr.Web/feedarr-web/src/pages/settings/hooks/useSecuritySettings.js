import { useCallback, useEffect, useRef, useState } from "react";
import { apiGet, apiPut } from "../../../api/client.js";
import { getSecurityText } from "../securityI18n.js";

export const defaultSecurityState = {
  authMode: "smart",
  publicBaseUrl: "",
  username: "",
  password: "",
  passwordConfirmation: "",
  hasPassword: false,
  authConfigured: false,
  authRequired: false,
};

export const defaultInitialSecurityState = {
  authMode: "smart",
  publicBaseUrl: "",
  username: "",
};

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

export function buildSecurityPayload(security, overrides = {}) {
  const merged = { ...security, ...overrides };
  const payload = {
    authMode: String(merged.authMode || "smart"),
    publicBaseUrl: String(merged.publicBaseUrl || ""),
    username: String(merged.username || ""),
  };

  if (merged.allowDowngradeToOpen === true) {
    payload.allowDowngradeToOpen = true;
  }

  if (payload.authMode !== "open" && (merged.password || merged.passwordConfirmation)) {
    payload.password = String(merged.password || "");
    payload.passwordConfirmation = String(merged.passwordConfirmation || "");
  }

  return payload;
}

export function normalizeSecurityResponse(source) {
  return {
    authMode: source?.authMode || "smart",
    publicBaseUrl: source?.publicBaseUrl || "",
    username: source?.username || "",
    password: "",
    passwordConfirmation: "",
    hasPassword: !!source?.hasPassword,
    authConfigured: !!source?.authConfigured,
    authRequired: !!source?.authRequired,
  };
}

export function collectChangedSecurityKeys(security, initialSecurity) {
  const changed = new Set();
  if (String(security.authMode || "") !== String(initialSecurity.authMode || "")) changed.add("security.authMode");
  if (String(security.publicBaseUrl || "") !== String(initialSecurity.publicBaseUrl || "")) changed.add("security.publicBaseUrl");
  if (String(security.username || "") !== String(initialSecurity.username || "")) changed.add("security.username");
  if (String(security.password || "").trim()) changed.add("security.password");
  if (String(security.passwordConfirmation || "").trim()) changed.add("security.passwordConfirmation");
  return changed;
}

function buildFieldErrors(keys, message) {
  const next = {};
  keys.forEach((key) => {
    next[key] = message;
  });
  return next;
}

function extractSecurityErrorsFromMessage(message, t) {
  const text = String(message || "");
  const lower = text.toLowerCase();
  if (!text) {
    return { securityErrors: [], securityFieldErrors: {}, securityMessage: "", passwordMessage: "" };
  }

  if (lower.includes("credentials_required") || lower.includes("credentials are required") || lower.includes("identifiants requis")) {
    const translated = t("settings.security.notice.credsRequired");
    const keys = ["username", "password", "passwordConfirmation"];
    return {
      securityErrors: keys,
      securityFieldErrors: buildFieldErrors(keys, translated),
      securityMessage: translated,
      passwordMessage: "",
    };
  }

  if (lower.includes("password and confirmation required")) {
    const translated = t("settings.security.error.passwordAndConfirmationRequired");
    const keys = ["password", "passwordConfirmation"];
    return {
      securityErrors: keys,
      securityFieldErrors: buildFieldErrors(keys, translated),
      securityMessage: translated,
      passwordMessage: translated,
    };
  }

  if (lower.includes("password confirmation mismatch")) {
    const translated = t("settings.security.error.passwordConfirmationMismatch");
    return {
      securityErrors: ["password", "passwordConfirmation"],
      securityFieldErrors: buildFieldErrors(["passwordConfirmation"], translated),
      securityMessage: translated,
      passwordMessage: translated,
    };
  }

  if (lower.includes("downgrade_confirmation_required")) {
    const translated = t("settings.security.warning.downgradeOpen");
    return {
      securityErrors: [],
      securityFieldErrors: {},
      securityMessage: translated,
      passwordMessage: "",
    };
  }

  const keys = [];
  if (lower.includes("username")) keys.push("username");
  if (lower.includes("password")) keys.push("password", "passwordConfirmation");
  return {
    securityErrors: keys,
    securityFieldErrors: keys.length > 0 ? buildFieldErrors(keys, text) : {},
    securityMessage: text,
    passwordMessage: keys.some((key) => key.startsWith("password")) ? text : "",
  };
}

export async function loadSecuritySettingsData(request = apiGet) {
  const response = await request("/api/settings/security");
  return normalizeSecurityResponse(response || defaultSecurityState);
}

export async function saveSecuritySettingsData(payload, request = apiPut) {
  const response = await request("/api/settings/security", payload);
  return normalizeSecurityResponse(response || payload);
}

export default function useSecuritySettings() {
  const [loaded, setLoaded] = useState(false);
  const [loading, setLoading] = useState(false);
  const [loadError, setLoadError] = useState("");
  const [security, setSecurityState] = useState(defaultSecurityState);
  const [initialSecurity, setInitialSecurity] = useState(defaultInitialSecurityState);
  const [securityErrors, setSecurityErrors] = useState([]);
  const [securityFieldErrors, setSecurityFieldErrors] = useState({});
  const [securityMessage, setSecurityMessage] = useState("");
  const [passwordMessage, setPasswordMessage] = useState("");
  const [saveError, setSaveError] = useState("");
  const [pulseKinds, setPulseKinds] = useState({});
  const pulseTimerRef = useRef(null);
  const t = getSecurityText();

  const isDirty =
    JSON.stringify({
      authMode: security.authMode,
      publicBaseUrl: security.publicBaseUrl,
      username: security.username,
    }) !== JSON.stringify(initialSecurity) ||
    !!security.password ||
    !!security.passwordConfirmation;

  const isStrict = security.authMode === "strict";
  const isSmart = security.authMode === "smart";
  const isProtectedMode = isStrict || isSmart;
  const serverHasPassword = security.hasPassword;
  const usernameValue = String(security.username || "").trim();
  const passwordValue = String(security.password || "").trim();
  const confirmValue = String(security.passwordConfirmation || "").trim();
  const isExposedConfig = isExposedPublicBaseUrl(security.publicBaseUrl);
  const effectiveAuthRequired = isStrict ? true : !!security.authRequired;
  const statusRequiresCredentials = isSmart && effectiveAuthRequired && !security.authConfigured;
  const credentialsRequiredForMode = isStrict || (isSmart && (isExposedConfig || statusRequiresCredentials));
  const requiresCredsToSave = loaded && isProtectedMode && credentialsRequiredForMode && !security.authConfigured;
  const userIsEditingCreds = !!passwordValue || !!confirmValue;
  const credsRequired =
    loaded &&
    isProtectedMode &&
    (requiresCredsToSave || userIsEditingCreds || (credentialsRequiredForMode && !serverHasPassword));
  const usernameRequired = loaded && isProtectedMode && (credentialsRequiredForMode || credsRequired);
  const passwordRequired = credsRequired;
  const confirmRequired = credsRequired;
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
  const canSave = loaded && isDirty && !credsMissing;
  const showExistingCredentialsHint =
    loaded &&
    isProtectedMode &&
    security.authConfigured &&
    serverHasPassword &&
    !userIsEditingCreds;

  const applyPulse = useCallback((keys, kind) => {
    if (pulseTimerRef.current) clearTimeout(pulseTimerRef.current);
    const next = {};
    [...keys].forEach((key) => {
      next[key] = kind;
    });
    setPulseKinds(next);
    pulseTimerRef.current = setTimeout(() => {
      setPulseKinds({});
    }, 1200);
  }, []);

  const setSecurity = useCallback((updater) => {
    setSecurityMessage("");
    setSecurityErrors([]);
    setSecurityFieldErrors({});
    setPasswordMessage("");
    setSaveError("");
    setSecurityState(updater);
  }, []);

  const loadSecuritySettings = useCallback(async () => {
    setLoading(true);
    setLoadError("");
    try {
      const next = await loadSecuritySettingsData();
      setSecurityState(next);
      setInitialSecurity({
        authMode: next.authMode,
        publicBaseUrl: next.publicBaseUrl,
        username: next.username,
      });
      setSecurityErrors([]);
      setSecurityFieldErrors({});
      setSecurityMessage("");
      setPasswordMessage("");
      setSaveError("");
      return next;
    } catch (error) {
      setLoadError(error?.message || "Erreur chargement paramètres sécurité");
      throw error;
    } finally {
      setLoaded(true);
      setLoading(false);
    }
  }, []);

  const saveSecuritySettings = useCallback(async (options = {}) => {
    const credsWarning = t("settings.security.notice.credsRequired");
    const downgradeWarning = t("settings.security.warning.downgradeOpen");
    const changed = collectChangedSecurityKeys(security, initialSecurity);

    setSecurityErrors([]);
    setSecurityFieldErrors({});
    setSecurityMessage("");
    setPasswordMessage("");
    setSaveError("");

    if (credsMissing) {
      const nextErrors = [];
      const nextFieldErrors = {};
      if (usernameMissing) {
        nextErrors.push("username");
        nextFieldErrors.username = credsWarning;
      }
      if (passwordMissing) {
        nextErrors.push("password");
        nextFieldErrors.password = credsWarning;
      }
      if (confirmMissing) {
        nextErrors.push("passwordConfirmation");
        nextFieldErrors.passwordConfirmation = credsWarning;
      }
      setSecurityErrors(nextErrors);
      setSecurityFieldErrors(nextFieldErrors);
      setSecurityMessage(credsWarning);
      setSaveError(credsWarning);
      applyPulse(changed.size > 0 ? changed : new Set(["security.username", "security.password", "security.passwordConfirmation"]), "err");
      throw markSecuritySettingsError(new Error(credsWarning));
    }

    const shouldAllowDowngrade = options?.allowDowngradeToOpen === true;
    if (requiresDowngradeConfirmation && !shouldAllowDowngrade) {
      setSecurityMessage(downgradeWarning);
      setSaveError(downgradeWarning);
      applyPulse(new Set(["security.authMode"]), "err");
      throw markSecuritySettingsError(new Error(downgradeWarning));
    }

    try {
      const saved = await saveSecuritySettingsData(
        buildSecurityPayload(security, {
          allowDowngradeToOpen: shouldAllowDowngrade,
        }),
      );

      setInitialSecurity({
        authMode: saved.authMode,
        publicBaseUrl: saved.publicBaseUrl,
        username: saved.username,
      });
      setSecurityState(saved);
      applyPulse(changed, "ok");
      return changed;
    } catch (error) {
      if (isPasswordComplexityError(error)) {
        const message = formatPasswordComplexityMessage(error);
        setSecurityErrors(["password", "passwordConfirmation"]);
        setSecurityFieldErrors({
          password: message,
          passwordConfirmation: message,
        });
        setPasswordMessage(message);
        setSecurityMessage(message);
        setSaveError(message);
        applyPulse(changed.size > 0 ? changed : new Set(["security.password", "security.passwordConfirmation"]), "err");
        if (error && typeof error === "object") error.message = message;
        throw markSecuritySettingsError(error);
      }

      if (String(error?.error || "").toLowerCase() === "downgrade_confirmation_required") {
        setSecurityMessage(downgradeWarning);
        setSaveError(downgradeWarning);
        applyPulse(changed.size > 0 ? changed : new Set(["security.authMode"]), "err");
        throw markSecuritySettingsError(error);
      }

      const mapped = extractSecurityErrorsFromMessage(error?.message, t);
      setSecurityErrors(mapped.securityErrors);
      setSecurityFieldErrors(mapped.securityFieldErrors);
      setSecurityMessage(mapped.securityMessage);
      setPasswordMessage(mapped.passwordMessage);
      setSaveError(mapped.securityMessage || error?.message || "Erreur sauvegarde sécurité");

      const fallbackPulseKeys = new Set(
        mapped.securityErrors.map((key) => `security.${key}`)
      );
      applyPulse(changed.size > 0 ? changed : fallbackPulseKeys, "err");
      throw markSecuritySettingsError(error);
    }
  }, [
    applyPulse,
    confirmMissing,
    credsMissing,
    initialSecurity,
    passwordMissing,
    requiresDowngradeConfirmation,
    security,
    t,
    usernameMissing,
  ]);

  useEffect(() => {
    return () => {
      if (pulseTimerRef.current) clearTimeout(pulseTimerRef.current);
    };
  }, []);

  return {
    security,
    setSecurity,
    initialSecurity,
    loaded,
    loading,
    loadError,
    securityErrors,
    securityFieldErrors,
    securityMessage,
    passwordMessage,
    saveError,
    pulseKinds,
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
