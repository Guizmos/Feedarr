import React, { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { apiGet, apiPost, apiPut } from "../../api/client.js";
import { tr } from "../../app/uiText.js";
import { getSessionSnapshot, login, refreshSession } from "../../auth/session.js";
import InlineNotice from "../../pages/settings/InlineNotice.jsx";

export default function Step2Security({ required = false, onStatusChange, saveRef }) {
  const [bootstrap, setBootstrap] = useState("");
  const [loadFailed, setLoadFailed] = useState(false);
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [savingPhase, setSavingPhase] = useState("");
  const [recheckRequired, setRecheckRequired] = useState(false);
  const [saved, setSaved] = useState(false);
  // saveAttempted becomes true on first click of "Enregistrer", then stays true.
  // This is what enables error coloring on empty fields.
  const [saveAttempted, setSaveAttempted] = useState(false);
  const [apiError, setApiError] = useState("");
  const usernameInputRef = useRef(null);
  const passwordInputRef = useRef(null);
  const [form, setForm] = useState({
    authMode: "smart",
    publicBaseUrl: "",
    username: "",
    password: "",
    passwordConfirmation: "",
    hasPassword: false,
    authConfigured: false,
    authRequired: required,
  });

  // --- Pulse animation (same pattern as SettingsUsers) ---
  const [pulseKeys, setPulseKeys] = useState(() => new Set());
  const prevFieldStatesRef = useRef({ username: "", password: "", confirm: "" });
  const pulseTimerRef = useRef(null);
  useEffect(() => {
    return () => { if (pulseTimerRef.current) clearTimeout(pulseTimerRef.current); };
  }, []);

  const load = useCallback(async () => {
    setLoading(true);
    setLoadFailed(false);
    setSaved(false);
    setApiError("");
    try {
      const sec = await apiGet("/api/settings/security");
      setSaved(!!sec?.authConfigured || sec?.authMode === "open");
      setForm((prev) => ({
        ...prev,
        authMode: sec?.authMode || "smart",
        publicBaseUrl: sec?.publicBaseUrl || "",
        username: sec?.username || "",
        password: "",
        passwordConfirmation: "",
        hasPassword: !!sec?.hasPassword,
        authConfigured: !!sec?.authConfigured,
        authRequired: !!sec?.authRequired,
      }));
    } catch (e) {
      setLoadFailed(true);
      setApiError(e?.message || tr("Erreur chargement securite", "Security loading error"));
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => { load(); }, [load]);

  // --- Derived booleans (recomputed on every render, no useMemo needed) ---
  const requiresCreds = form.authMode !== "open";
  const usernameEmpty = !String(form.username || "").trim();
  const hasPasswordPresent = form.hasPassword || !!String(form.password || "").trim();
  const passwordUpdateStarted = !!form.password || !!form.passwordConfirmation;
  const passwordUpdateInvalid =
    requiresCreds && passwordUpdateStarted &&
    (!String(form.password || "").trim() ||
      !String(form.passwordConfirmation || "").trim() ||
      form.password !== form.passwordConfirmation);

  // --- Field states ---
  // Error only shows after saveAttempted (first click of "Enregistrer").
  // Valid shows as soon as the field has a correct value.
  const usernameState = useMemo(() => {
    if (saveAttempted && requiresCreds && usernameEmpty) return "error";
    if (!usernameEmpty) return "valid";
    return "";
  }, [saveAttempted, requiresCreds, usernameEmpty]);

  const passwordState = useMemo(() => {
    if (saveAttempted && requiresCreds && !hasPasswordPresent) return "error";
    if (passwordUpdateInvalid) return "error";
    if (form.hasPassword && !passwordUpdateStarted) return "valid";
    if (!passwordUpdateInvalid && !!form.password) return "valid";
    return "";
  }, [saveAttempted, requiresCreds, hasPasswordPresent, form.hasPassword, form.password, passwordUpdateStarted, passwordUpdateInvalid]);

  const confirmState = useMemo(() => {
    if (passwordUpdateInvalid) return "error";
    if (!!form.passwordConfirmation && form.password === form.passwordConfirmation) return "valid";
    return "";
  }, [passwordUpdateInvalid, form.passwordConfirmation, form.password]);

  // --- Pulse on state change ---
  useEffect(() => {
    const nextStates = { username: usernameState, password: passwordState, confirm: confirmState };
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
      pulseTimerRef.current = setTimeout(() => { setPulseKeys(new Set()); }, 850);
    }
  }, [usernameState, passwordState, confirmState]);

  const fieldCls = (key, state) => {
    const resolved = state === "error" || state === "valid" ? state : "";
    let cls = "fieldWrap";
    if (resolved === "error") cls += " fieldWrap--error";
    if (resolved === "valid") cls += " fieldWrap--valid";
    if (pulseKeys.has(key)) cls += " fieldWrap--pulse";
    return cls;
  };

  // --- Save ---
  const save = useCallback(async () => {
    if (loading || saving) return;
    setSaveAttempted(true);

    // Client-side validation — order matters for focus
    const reqCreds = form.authMode !== "open";
    const uEmpty = !String(form.username || "").trim();
    const noPass = !(form.hasPassword || !!String(form.password || "").trim());
    const pwdInvalid =
      reqCreds && (!!form.password || !!form.passwordConfirmation) &&
      (!String(form.password || "").trim() ||
        !String(form.passwordConfirmation || "").trim() ||
        form.password !== form.passwordConfirmation);

    if (reqCreds && uEmpty) { usernameInputRef.current?.focus(); return; }
    if (reqCreds && noPass) { passwordInputRef.current?.focus(); return; }
    if (pwdInvalid) { passwordInputRef.current?.focus(); return; }

    setSaving(true);
    setSaved(false);
    setRecheckRequired(false);
    setApiError("");
    let configurationSaved = false;
    let writeAttempted = false;
    try {
      const payload = {
        authMode: form.authMode,
        publicBaseUrl: form.publicBaseUrl,
        ...(reqCreds ? { username: form.username } : {}),
      };
      if (reqCreds && (form.password || form.passwordConfirmation)) {
        payload.password = form.password;
        payload.passwordConfirmation = form.passwordConfirmation;
      }
      const session = getSessionSnapshot().state;
      let headers = {};
      if (session?.authRequired && !session.authConfigured) {
        setSavingPhase(tr("Autorisation bootstrap…", "Authorizing bootstrap…"));
        const issued = await apiPost("/api/setup/bootstrap-token", {}, {
          headers: bootstrap ? { "X-Bootstrap-Secret": bootstrap } : {},
        });
        headers = { "X-Feedarr-Bootstrap-Token": issued.token };
      }
      setSavingPhase(tr("Enregistrement…", "Saving…"));
      writeAttempted = true;
      await apiPut("/api/settings/security", payload, { headers });
      configurationSaved = true;
      setBootstrap("");
      setSavingPhase(tr("Vérification de l’accès…", "Verifying access…"));
      if (form.authMode !== "open" && form.password && window.location.protocol === "https:") {
        await login(form.username, form.password, false);
      } else await refreshSession({ force: true });
      // Verify the persisted settings, not merely the PUT echo or a pre-write GET.
      setSavingPhase(tr("Relecture des paramètres…", "Reading saved settings…"));
      const next = await apiGet("/api/settings/security");
      if (next?.authMode !== payload.authMode || (payload.authMode === "open" && (next.authRequired || next.authConfigured || next.hasPassword || next.username)) || (reqCreds && (!next?.authConfigured || !next?.hasPassword))) {
        throw new Error(tr("L’état relu ne correspond pas aux paramètres demandés. Vérifiez l’accès puis réessayez.", "Persisted settings do not match the requested state. Check access and retry."));
      }
      setForm((prev) => ({
        ...prev,
        authMode: next.authMode,
        publicBaseUrl: next.publicBaseUrl ?? "",
        username: next.username ?? "",
        password: "",
        passwordConfirmation: "",
        hasPassword: !!next?.hasPassword,
        authConfigured: !!next?.authConfigured,
        authRequired: !!next?.authRequired,
      }));
      setSaved(true);
    } catch (e) {
      const uncertainWrite = writeAttempted && (!e?.status || e.status >= 500);
      setRecheckRequired(configurationSaved || uncertainWrite);
      const status = e?.status ? `HTTP ${e.status} — ` : e?.name === "TimeoutError" ? `${tr("Délai dépassé", "Timed out")} — ` : "";
      const cause = e?.detail || e?.message || tr("Erreur de connexion", "Connection error");
      setApiError(`${configurationSaved ? `${tr("Paramètres enregistrés.", "Settings saved.")} ` : uncertainWrite ? `${tr("Enregistrement non confirmé.", "Save not confirmed.")} ` : ""}${status}${cause}${configurationSaved || uncertainWrite ? ` ${tr("Vérifiez l’accès pour reprendre sans réenregistrer les identifiants.", "Check access to continue without saving credentials again.")}` : ""}`);
    } finally {
      setSavingPhase("");
      setSaving(false);
    }
  }, [form, bootstrap, loading, saving]);

  const verifyAccess = useCallback(async () => {
    if (saving) return;
    setSaving(true); setSavingPhase(tr("Vérification de l’accès…", "Verifying access…")); setApiError("");
    try {
      const state = await refreshSession({ force: true });
      if (!state.authRequired || state.authenticated) await load();
      setRecheckRequired(false);
    }
    catch (e) { setApiError(e?.message || "Vérification impossible. Réessayez."); }
    finally { setSavingPhase(""); setSaving(false); }
  }, [saving, load]);

  useEffect(() => {
    if (saveRef) { saveRef.current = recheckRequired ? verifyAccess : save; }
  }, [save, verifyAccess, recheckRequired, saveRef]);

  // --- Status for parent ---
  const status = useMemo(() => ({
    ready: !loading && !saving && !loadFailed && !apiError,
    canSave: !loading && !saving && !loadFailed,
    saving: loading || saving,
    progress: savingPhase,
    saved,
    recheckRequired,
    error: apiError,
    authRequired: !!form.authRequired,
    authConfigured: !!form.authConfigured,
    authMode: form.authMode,
  }), [form.authConfigured, form.authMode, form.authRequired, loading, saving, saved, apiError, loadFailed, recheckRequired, savingPhase]);

  useEffect(() => { onStatusChange?.(status); }, [onStatusChange, status]);

  // --- Notices: specific, reactive, progressive ---
  const notices = useMemo(() => {
    const list = [];
    const reqCreds = form.authMode !== "open";
    const uEmpty = !String(form.username || "").trim();
    const noPass = !(form.hasPassword || !!String(form.password || "").trim());
    const pwdInvalid =
      reqCreds && (!!form.password || !!form.passwordConfirmation) &&
      (!String(form.password || "").trim() ||
        !String(form.passwordConfirmation || "").trim() ||
        form.password !== form.passwordConfirmation);

    const hasValidationIssue = (saveAttempted && reqCreds && (uEmpty || noPass)) || pwdInvalid;

    // Progressive validation messages (update as user fills in the form)
    if (saveAttempted && reqCreds && uEmpty && noPass) {
      list.push({ key: "creds", variant: "error",
        message: tr("Username et password sont requis.", "Username and password are required.") });
    } else if (saveAttempted && reqCreds && uEmpty) {
      list.push({ key: "username", variant: "error",
        message: tr("Username requis.", "Username is required.") });
    } else if (saveAttempted && reqCreds && noPass) {
      list.push({ key: "password", variant: "error",
        message: tr("Password requis.", "Password is required.") });
    } else if (pwdInvalid) {
      list.push({ key: "confirm", variant: "error",
        message: tr(
          "Les mots de passe ne correspondent pas ou sont incomplets.",
          "Passwords don't match or are incomplete."
        ) });
    }

    // API error (from backend)
    if (apiError) {
      list.push({ key: "api", variant: "error", message: apiError });
    }

    // Existing credentials hint (shown before any save attempt)
    if (!saveAttempted && !saved && form.authMode !== "open" && form.hasPassword) {
      list.push({ key: "hint", variant: "info",
        message: tr(
          "Identifiants déjà configurés. Laisse vide pour conserver le mot de passe actuel.",
          "Credentials already configured. Leave blank to keep current password."
        ) });
    }

    // Success (only if no current validation issues)
    if (saved && !apiError && !hasValidationIssue) {
      list.push({ key: "ok", variant: "success",
        message: tr("Paramètres de sécurité sauvegardés.", "Security settings saved.") });
    }

    return list;
  }, [saveAttempted, form.authMode, form.username, form.hasPassword, form.password, form.passwordConfirmation, apiError, saved]);

  return (
    <div className="setup-step setup-security" aria-busy={loading || saving}>
      <h2>{tr("Securite", "Security")}</h2>
      <p>{tr("Configure le mode d'acces de Feedarr.", "Configure Feedarr access mode.")}</p>

      {loadFailed && <div className="security-notices"><InlineNotice variant="error" message={apiError} /><div className="settings-card__actions"><button className="btn" type="button" onClick={load}>Réessayer le chargement</button></div></div>}
      {loading && <div className="muted" role="status">{tr("Chargement...", "Loading...")}</div>}

      {!loading && !loadFailed && (
        <>
          <div className="indexer-list">
            {getSessionSnapshot().state?.authRequired && !getSessionSnapshot().state?.authConfigured && <div className="indexer-card">
              <div className="indexer-row indexer-row--settings">
                <label className="indexer-title" htmlFor="setup-bootstrap">Secret bootstrap de l’instance cible</label>
                <div className="indexer-actions"><div className="fieldWrap">
                  <input id="setup-bootstrap" type="password" autoComplete="off" aria-describedby="setup-bootstrap-help" value={bootstrap} onChange={e => setBootstrap(e.target.value)} disabled={saving} />
                </div></div>
              </div>
              <div className="settings-help" id="setup-bootstrap-help">Autorise la création des identifiants à distance. Ce secret appartient à cette instance, pas à une sauvegarde. Une session web nécessite HTTPS.</div>
            </div>}
            <div className="indexer-card">
              <div className="indexer-row indexer-row--settings">
                <span className="indexer-title">Auth Mode</span>
                <div className="indexer-actions">
                  <div className="fieldWrap">
                    <select
                      value={form.authMode}
                      onChange={(e) => { setSaved(false); setForm((s) => ({ ...s, authMode: e.target.value })); }}
                      disabled={saving}
                    >
                      <option value="smart">Smart (default)</option>
                      <option value="strict">Strict</option>
                      <option value="open">Open</option>
                    </select>
                  </div>
                </div>
              </div>
              <div className="settings-help">
                {tr(
                  "Smart protege automatiquement si instance exposee.",
                  "Smart mode protects automatically if the instance is exposed."
                )}
              </div>
            </div>

            <div className="indexer-card">
              <div className="indexer-row indexer-row--settings">
                <span className="indexer-title">Public Base URL</span>
                <div className="indexer-actions">
                  <div className="fieldWrap">
                    <input
                      type="text"
                      value={form.publicBaseUrl}
                      onChange={(e) => { setSaved(false); setForm((s) => ({ ...s, publicBaseUrl: e.target.value })); }}
                      placeholder="https://example.com/feedarr"
                      disabled={saving}
                    />
                  </div>
                </div>
              </div>
            </div>

            {form.authMode !== "open" && (
              <>
                <div className="indexer-card">
                  <div className="indexer-row indexer-row--settings">
                    <span className="indexer-title">Username</span>
                    <div className="indexer-actions">
                      <div className={fieldCls("username", usernameState)}>
                        <input
                          ref={usernameInputRef}
                          type="text"
                          value={form.username}
                          onChange={(e) => { setSaved(false); setForm((s) => ({ ...s, username: e.target.value })); }}
                          placeholder={tr("Entrez username", "Enter username")}
                          disabled={saving}
                        />
                      </div>
                    </div>
                  </div>
                </div>

                <div className="indexer-card">
                  <div className="indexer-row indexer-row--settings">
                    <span className="indexer-title">Password</span>
                    <div className="indexer-actions">
                      <div className={fieldCls("password", passwordState)}>
                        <input
                          ref={passwordInputRef}
                          type="password"
                          value={form.password}
                          onChange={(e) => { setSaved(false); setForm((s) => ({ ...s, password: e.target.value })); }}
                          placeholder={
                            form.hasPassword
                              ? tr("Laisser vide pour conserver", "Leave blank to keep current")
                              : tr("Entrez password", "Enter password")
                          }
                          disabled={saving}
                        />
                      </div>
                    </div>
                  </div>
                </div>

                <div className="indexer-card">
                  <div className="indexer-row indexer-row--settings">
                    <span className="indexer-title">Password Confirmation</span>
                    <div className="indexer-actions">
                      <div className={fieldCls("confirm", confirmState)}>
                        <input
                          type="password"
                          value={form.passwordConfirmation}
                          onChange={(e) => { setSaved(false); setForm((s) => ({ ...s, passwordConfirmation: e.target.value })); }}
                          placeholder={tr("Confirmez password", "Confirm password")}
                          disabled={saving}
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
              {notices.map((n) => (
                <InlineNotice key={n.key} variant={n.variant} message={n.message} />
              ))}
            </div>
          )}
        </>
      )}
    </div>
  );
}
