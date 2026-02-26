import { useCallback, useState } from "react";
import { apiGet, apiPut } from "../../../api/client.js";

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

export default function useSecuritySettings() {
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

  const isDirty =
    JSON.stringify({
      authMode: security.authMode,
      publicBaseUrl: security.publicBaseUrl,
      username: security.username,
    }) !== JSON.stringify(initialSecurity) ||
    !!security.password ||
    !!security.passwordConfirmation;
  const isProtectedMode = security.authMode === "smart" || security.authMode === "strict";
  const isExposedConfig = isExposedPublicBaseUrl(security.publicBaseUrl);
  const credentialsRequiredForMode = isProtectedMode && isExposedConfig;
  const usernameMissing = credentialsRequiredForMode && !String(security.username || "").trim();
  const hasPasswordPresent = security.hasPassword || !!String(security.password || "").trim();
  const passwordMissing = credentialsRequiredForMode && !hasPasswordPresent;
  const passwordUpdateStarted = !!security.password || !!security.passwordConfirmation;
  const passwordUpdateInvalid =
    passwordUpdateStarted &&
    (!String(security.password || "").trim() ||
      !String(security.passwordConfirmation || "").trim() ||
      security.password !== security.passwordConfirmation);
  const canSave = !(usernameMissing || passwordMissing || passwordUpdateInvalid);
  const credentialsWarning =
    "Credentials are required when AuthMode is smart/strict and instance is exposed. Set username/password or switch to open.";

  const setSecurity = useCallback((updater) => {
    setSecurityMessage("");
    setSecurityErrors([]);
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
      }
    } catch {
      // Ignore load errors
    }
  }, []);

  const saveSecuritySettings = useCallback(async () => {
    setSecurityErrors([]);
    setSecurityMessage("");
    if (usernameMissing || passwordMissing) {
      const next = [];
      if (usernameMissing) next.push("username");
      if (passwordMissing) next.push("password", "passwordConfirmation");
      setSecurityErrors(next);
      setSecurityMessage(credentialsWarning);
      throw new Error(credentialsWarning);
    }
    if (passwordUpdateInvalid) {
      const message = "Password and confirmation are required and must match.";
      setSecurityErrors(["password", "passwordConfirmation"]);
      setSecurityMessage(message);
      throw new Error(message);
    }

    const payload = {
      authMode: security.authMode,
      publicBaseUrl: security.publicBaseUrl,
      username: security.username,
    };

    if (security.password || security.passwordConfirmation) {
      payload.password = security.password;
      payload.passwordConfirmation = security.passwordConfirmation;
    }

    try {
      const saved = await apiPut("/api/settings/security", payload);
      setInitialSecurity({
        authMode: saved?.authMode || security.authMode,
        publicBaseUrl: saved?.publicBaseUrl || security.publicBaseUrl,
        username: saved?.username || security.username,
      });
      setSecurityState((prev) => ({
        ...prev,
        authMode: saved?.authMode || prev.authMode,
        publicBaseUrl: saved?.publicBaseUrl || prev.publicBaseUrl,
        username: saved?.username || prev.username,
        hasPassword: !!saved?.hasPassword,
        authConfigured: !!saved?.authConfigured,
        authRequired: !!saved?.authRequired,
        password: "",
        passwordConfirmation: "",
      }));
      setSecurityMessage("");
    } catch (e) {
      if (typeof e?.message === "string") {
        const msgLower = e.message.toLowerCase();
        const next = [];
        if (msgLower.includes("credentials_required") || msgLower.includes("credentials are required")) {
          if (!String(security.username || "").trim()) next.push("username");
          if (!(security.hasPassword || !!String(security.password || "").trim())) {
            next.push("password", "passwordConfirmation");
          }
          setSecurityMessage(e.message);
        } else {
          if (msgLower.includes("username")) next.push("username");
          if (msgLower.includes("password")) next.push("password", "passwordConfirmation");
          if (next.length > 0) setSecurityMessage(e.message);
        }
        if (next.length > 0) setSecurityErrors(next);
      }
      throw e;
    }
  }, [
    credentialsWarning,
    passwordMissing,
    passwordUpdateInvalid,
    security,
    usernameMissing,
  ]);

  return {
    security,
    setSecurity,
    initialSecurity,
    securityErrors,
    securityMessage,
    credentialsRequiredForMode,
    canSave,
    isDirty,
    loadSecuritySettings,
    saveSecuritySettings,
  };
}
