import { useCallback, useState } from "react";
import { apiGet, apiPut } from "../../../api/client.js";

export default function useSecuritySettings() {
  const [security, setSecurity] = useState({
    authentication: "none",
    authenticationRequired: "local",
    username: "",
    password: "",
    passwordConfirmation: "",
    hasPassword: false,
  });
  const [initialSecurity, setInitialSecurity] = useState({
    authentication: "none",
    authenticationRequired: "local",
    username: "",
  });
  const [securityErrors, setSecurityErrors] = useState([]);

  const isDirty =
    JSON.stringify({
      authentication: security.authentication,
      authenticationRequired: security.authenticationRequired,
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
          authentication: sec.authentication || "none",
          authenticationRequired: sec.authenticationRequired || "local",
          username: sec.username || "",
          hasPassword: !!sec.hasPassword,
          password: "",
          passwordConfirmation: "",
        }));
        setInitialSecurity({
          authentication: sec.authentication || "none",
          authenticationRequired: sec.authenticationRequired || "local",
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
      authentication: security.authentication,
      authenticationRequired: security.authenticationRequired,
      username: security.username,
    };

    if (security.password || security.passwordConfirmation) {
      payload.password = security.password;
      payload.passwordConfirmation = security.passwordConfirmation;
    }

    try {
      await apiPut("/api/settings/security", payload);
      setInitialSecurity({
        authentication: security.authentication,
        authenticationRequired: security.authenticationRequired,
        username: security.username,
      });
      setSecurity((prev) => ({
        ...prev,
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
