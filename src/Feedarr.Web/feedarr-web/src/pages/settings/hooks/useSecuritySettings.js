import { useCallback, useState } from "react";
import { apiGet, apiPut } from "../../../api/client.js";

export default function useSecuritySettings() {
  const [security, setSecurity] = useState({
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

  const isDirty =
    JSON.stringify({
      authMode: security.authMode,
      publicBaseUrl: security.publicBaseUrl,
      username: security.username,
    }) !== JSON.stringify(initialSecurity) ||
    !!security.password ||
    !!security.passwordConfirmation;

  const loadSecuritySettings = useCallback(async () => {
    try {
      const sec = await apiGet("/api/settings/security");
      if (sec) {
        setSecurity((prev) => ({
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
      }
    } catch {
      // Ignore load errors
    }
  }, []);

  const saveSecuritySettings = useCallback(async () => {
    setSecurityErrors([]);
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
      setSecurity((prev) => ({
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
    } catch (e) {
      if (typeof e?.message === "string") {
        const msgLower = e.message.toLowerCase();
        const next = [];
        if (msgLower.includes("username")) next.push("username");
        if (msgLower.includes("password")) next.push("password", "passwordConfirmation");
        if (next.length > 0) setSecurityErrors(next);
      }
      throw e;
    }
  }, [security]);

  return {
    security,
    setSecurity,
    initialSecurity,
    securityErrors,
    isDirty,
    loadSecuritySettings,
    saveSecuritySettings,
  };
}
