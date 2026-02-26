import React, { useCallback, useEffect, useMemo, useState } from "react";
import { apiGet, apiPut } from "../../api/client.js";
import { tr } from "../../app/uiText.js";

export default function Step2Security({ required = false, onStatusChange }) {
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState("");
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

  const load = useCallback(async () => {
    setLoading(true);
    setError("");
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
      setError(e?.message || tr("Erreur chargement securite", "Security loading error"));
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    load();
  }, [load]);

  const save = useCallback(async () => {
    setSaving(true);
    setError("");
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
    } catch (e) {
      setError(e?.message || tr("Erreur sauvegarde securite", "Security save error"));
    } finally {
      setSaving(false);
    }
  }, [form]);

  const status = useMemo(() => {
    const effectiveRequired = !!form.authRequired || !!required;
    const ready = !effectiveRequired || form.authConfigured || form.authMode === "open";
    return {
      ready: ready && !saving,
      saving,
      error,
      authRequired: effectiveRequired,
      authConfigured: !!form.authConfigured,
      authMode: form.authMode,
    };
  }, [error, form.authConfigured, form.authMode, form.authRequired, required, saving]);

  useEffect(() => {
    onStatusChange?.(status);
  }, [onStatusChange, status]);

  return (
    <div className="setup-step setup-security">
      <h2>{tr("Securite", "Security")}</h2>
      <p>{tr("Configure le mode d'acces de Feedarr.", "Configure Feedarr access mode.")}</p>

      {loading && <div className="muted">{tr("Chargement...", "Loading...")}</div>}
      {error && <div className="onboarding__error">{error}</div>}

      {!loading && (
        <>
          {status.authRequired && !status.authConfigured && (
            <div className="onboarding__error">
              {tr(
                "Configuration securite requise avant de continuer.",
                "Security setup is required before continuing."
              )}
            </div>
          )}

          <div className="indexer-list">
            <div className="indexer-card">
              <div className="indexer-row indexer-row--settings">
                <span className="indexer-title">Auth Mode</span>
                <div className="indexer-actions">
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

            {form.authMode !== "open" && (
              <>
                <div className="indexer-card">
                  <div className="indexer-row indexer-row--settings">
                    <span className="indexer-title">Username</span>
                    <div className="indexer-actions">
                      <input
                        type="text"
                        value={form.username}
                        onChange={(e) => setForm((s) => ({ ...s, username: e.target.value }))}
                        placeholder={tr("Entrez username", "Enter username")}
                        disabled={saving}
                      />
                    </div>
                  </div>
                </div>

                <div className="indexer-card">
                  <div className="indexer-row indexer-row--settings">
                    <span className="indexer-title">Password</span>
                    <div className="indexer-actions">
                      <input
                        type="password"
                        value={form.password}
                        onChange={(e) => setForm((s) => ({ ...s, password: e.target.value }))}
                        placeholder={form.hasPassword ? tr("Laisser vide pour conserver", "Leave blank to keep current") : tr("Entrez password", "Enter password")}
                        disabled={saving}
                      />
                    </div>
                  </div>
                </div>

                <div className="indexer-card">
                  <div className="indexer-row indexer-row--settings">
                    <span className="indexer-title">Password Confirmation</span>
                    <div className="indexer-actions">
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
              </>
            )}
          </div>

          <div className="setup-jackett__actions" style={{ marginTop: 16 }}>
            <button className="btn btn-accent" type="button" onClick={save} disabled={saving}>
              {saving ? tr("Enregistrement...", "Saving...") : tr("Enregistrer", "Save")}
            </button>
          </div>
        </>
      )}
    </div>
  );
}
