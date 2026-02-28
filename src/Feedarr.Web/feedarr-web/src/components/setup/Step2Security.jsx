import React, { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { apiGet, apiPut } from "../../api/client.js";
import { tr } from "../../app/uiText.js";
import InlineNotice from "../../pages/settings/InlineNotice.jsx";

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

export default function Step2Security({ required = false, onStatusChange, saveRef }) {
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
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
    setApiError("");
    try {
      const sec = await apiGet("/api/settings/security");
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
    passwordUpdateStarted &&
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
    setSaveAttempted(true);

    // Client-side validation — order matters for focus
    const reqCreds = form.authMode !== "open";
    const uEmpty = !String(form.username || "").trim();
    const noPass = !(form.hasPassword || !!String(form.password || "").trim());
    const pwdInvalid =
      (!!form.password || !!form.passwordConfirmation) &&
      (!String(form.password || "").trim() ||
        !String(form.passwordConfirmation || "").trim() ||
        form.password !== form.passwordConfirmation);

    if (reqCreds && uEmpty) { usernameInputRef.current?.focus(); return; }
    if (reqCreds && noPass) { passwordInputRef.current?.focus(); return; }
    if (pwdInvalid) { passwordInputRef.current?.focus(); return; }

    setSaving(true);
    setApiError("");
    try {
      const payload = {
        authMode: form.authMode,
        publicBaseUrl: form.publicBaseUrl,
        username: form.username,
      };
      if (form.password || form.passwordConfirmation) {
        payload.password = form.password;
        payload.passwordConfirmation = form.passwordConfirmation;
      }
      const next = await apiPut("/api/settings/security", payload);
      setForm((prev) => ({
        ...prev,
        authMode: next?.authMode || prev.authMode,
        publicBaseUrl: next?.publicBaseUrl || prev.publicBaseUrl,
        username: next?.username || prev.username,
        password: "",
        passwordConfirmation: "",
        hasPassword: !!next?.hasPassword,
        authConfigured: !!next?.authConfigured,
        authRequired: !!next?.authRequired,
      }));
      setSaved(true);
    } catch (e) {
      setApiError(e?.message || tr("Erreur sauvegarde securite", "Security save error"));
    } finally {
      setSaving(false);
    }
  }, [form]);

  useEffect(() => {
    if (saveRef) { saveRef.current = save; }
  }, [save, saveRef]);

  // --- Status for parent ---
  const status = useMemo(() => ({
    ready: !saving,
    saving,
    saved,
    error: apiError,
    authRequired: !!form.authRequired || !!required,
    authConfigured: !!form.authConfigured,
    authMode: form.authMode,
  }), [form.authConfigured, form.authMode, form.authRequired, required, saving, saved, apiError]);

  useEffect(() => { onStatusChange?.(status); }, [onStatusChange, status]);

  // --- Notices: specific, reactive, progressive ---
  const notices = useMemo(() => {
    const list = [];
    const reqCreds = form.authMode !== "open";
    const uEmpty = !String(form.username || "").trim();
    const noPass = !(form.hasPassword || !!String(form.password || "").trim());
    const pwdInvalid =
      (!!form.password || !!form.passwordConfirmation) &&
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
    <div className="setup-step setup-security">
      <h2>{tr("Securite", "Security")}</h2>
      <p>{tr("Configure le mode d'acces de Feedarr.", "Configure Feedarr access mode.")}</p>

      {loading && <div className="muted">{tr("Chargement...", "Loading...")}</div>}

      {!loading && (
        <>
          <div className="indexer-list">
            <div className="indexer-card">
              <div className="indexer-row indexer-row--settings">
                <span className="indexer-title">Auth Mode</span>
                <div className="indexer-actions">
                  <div className="fieldWrap">
                    <select
                      value={form.authMode}
                      onChange={(e) => setForm((s) => ({ ...s, authMode: e.target.value }))}
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
                      onChange={(e) => setForm((s) => ({ ...s, publicBaseUrl: e.target.value }))}
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
                          onChange={(e) => setForm((s) => ({ ...s, username: e.target.value }))}
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
                          onChange={(e) => setForm((s) => ({ ...s, password: e.target.value }))}
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
                          onChange={(e) => setForm((s) => ({ ...s, passwordConfirmation: e.target.value }))}
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
