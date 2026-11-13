import { useCallback, useState } from "react";
import { apiGet, apiPut } from "../../../api/client.js";
import { securitySaved } from "../../../auth/session.js";

export function normalizeSecurityResponse(source = {}) {
  return { requireHttps: !!source.requireHttps, publicBaseUrl: source.publicBaseUrl || "", username: source.username || "",
    password: "", passwordConfirmation: "", hasPassword: !!source.hasPassword,
    authConfigured: !!source.authConfigured, transportMigration: source.transportMigration || "" };
}
export function buildSecurityPayload(security) {
  return { requireHttps: !!security.requireHttps, publicBaseUrl: security.publicBaseUrl, username: security.username,
    ...(security.password || security.passwordConfirmation ? { password: security.password, passwordConfirmation: security.passwordConfirmation } : {}) };
}
export function collectChangedSecurityKeys(current, initial) {
  return new Set(["requireHttps", "publicBaseUrl", "username", "password", "passwordConfirmation"].filter(key => current[key] !== initial[key]).map(key => `security.${key}`));
}
export async function loadSecuritySettingsData(request = apiGet) {
  return normalizeSecurityResponse(await request("/api/settings/security"));
}
export async function saveSecuritySettingsData(payload, request = apiPut) {
  return normalizeSecurityResponse(await request("/api/settings/security", payload));
}
export default function useSecuritySettings() {
  const [security, setSecurity] = useState(() => normalizeSecurityResponse());
  const [initial, setInitial] = useState(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState("");
  const load = useCallback(async () => {
    setLoading(true); setError("");
    try { const next = await loadSecuritySettingsData(); setSecurity(next); setInitial(next); }
    catch (e) { setError(e.message); }
    finally { setLoading(false); }
  }, []);
  const save = useCallback(async () => {
    setLoading(true); setError("");
    try {
      const next = await saveSecuritySettingsData(buildSecurityPayload(security));
      setSecurity(next); setInitial(next);
      // The PUT is committed. The gate owns reauthentication, without nested requests.
      securitySaved({ ...next, authRequired: true });
    } catch (e) { setError(e.detail || e.message); }
    finally { setLoading(false); }
  }, [security]);
  return { security, setSecurity, loading, error, load, save,
    canSave: !!initial && !loading && collectChangedSecurityKeys(security, initial).size > 0 };
}
