import React, { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { apiGet, apiPut } from "../../api/client.js";
import {
  applyUiLanguage,
  DEFAULT_UI_LANGUAGE,
  LANGUAGE_OPTIONS,
  normalizeUiLanguage,
} from "../../app/locale.js";
import { tr } from "../../app/uiText.js";
import { buildUiPayload } from "../../pages/settings/hooks/useUiSettings.js";

export default function Step0Language({ onStatusChange }) {
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState("");
  const [selectedLanguage, setSelectedLanguage] = useState(DEFAULT_UI_LANGUAGE);
  const [uiSnapshot, setUiSnapshot] = useState(null);
  const saveTokenRef = useRef(0);

  const loadSettings = useCallback(async () => {
    setLoading(true);
    setError("");
    try {
      const current = await apiGet("/api/settings/ui");
      const snapshot = buildUiPayload(current || {});
      const normalized = normalizeUiLanguage(snapshot.uiLanguage || DEFAULT_UI_LANGUAGE);
      setUiSnapshot(snapshot);
      setSelectedLanguage(normalized);
      applyUiLanguage(normalized, true);
    } catch (e) {
      setUiSnapshot(
        buildUiPayload({
          uiLanguage: DEFAULT_UI_LANGUAGE,
          mediaInfoLanguage: DEFAULT_UI_LANGUAGE,
        })
      );
      setSelectedLanguage(DEFAULT_UI_LANGUAGE);
      setError(e?.message || tr("Erreur chargement langue", "Language loading error"));
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    loadSettings();
  }, [loadSettings]);

  const saveLanguage = useCallback(async (nextLanguage) => {
    const snapshot = uiSnapshot || buildUiPayload({});
    const normalized = normalizeUiLanguage(nextLanguage || DEFAULT_UI_LANGUAGE);
    const token = saveTokenRef.current + 1;
    saveTokenRef.current = token;

    setSaving(true);
    setError("");
    try {
      const payload = buildUiPayload(snapshot, {
        uiLanguage: normalized,
        mediaInfoLanguage: normalized,
      });
      const saved = await apiPut("/api/settings/ui", payload);
      if (token !== saveTokenRef.current) return;
      const merged = buildUiPayload(saved || payload);
      const savedLanguage = normalizeUiLanguage(merged.uiLanguage || normalized);
      setUiSnapshot(merged);
      setSelectedLanguage(savedLanguage);
      applyUiLanguage(savedLanguage, true);
    } catch (e) {
      if (token !== saveTokenRef.current) return;
      setError(e?.message || tr("Erreur sauvegarde langue", "Language save error"));
    } finally {
      if (token === saveTokenRef.current) {
        setSaving(false);
      }
    }
  }, [uiSnapshot]);

  function handleLanguageChange(event) {
    const nextLanguage = normalizeUiLanguage(event.target.value);
    setSelectedLanguage(nextLanguage);
    void saveLanguage(nextLanguage);
  }

  const status = useMemo(() => ({
    ready: !loading && !saving && !!selectedLanguage && !error,
    saving,
    error,
  }), [error, loading, saving, selectedLanguage]);

  useEffect(() => {
    onStatusChange?.(status);
  }, [onStatusChange, status]);

  return (
    <div className="setup-step setup-language">
      <h2>{tr("Langue", "Language")}</h2>
      <p>{tr("Choisis la langue par defaut de Feedarr.", "Choose Feedarr default language.")}</p>

      <div className="setup-providers__add settings-row settings-row--ui-select">
        <label>{tr("Langue par defaut", "Default language")}</label>
        <select
          className="settings-field"
          value={selectedLanguage}
          onChange={handleLanguageChange}
          disabled={loading || saving}
        >
          {LANGUAGE_OPTIONS.map((language) => (
            <option key={language.value} value={language.value}>
              {language.label}
            </option>
          ))}
        </select>
      </div>

      <div className="muted">
        {tr(
          "Cette etape applique la langue UI et la langue metadata par defaut.",
          "This step sets both UI and metadata default language."
        )}
      </div>

      {loading && <div className="muted">{tr("Chargement...", "Loading...")}</div>}
      {saving && <div className="muted">{tr("Enregistrement...", "Saving...")}</div>}

      {error && (
        <div className="onboarding__error" style={{ marginTop: 12 }}>
          {error}
        </div>
      )}

      {error && (
        <div className="setup-jackett__actions" style={{ marginTop: 12 }}>
          <button className="btn" type="button" onClick={() => loadSettings()} disabled={loading || saving}>
            {tr("Reessayer", "Retry")}
          </button>
        </div>
      )}
    </div>
  );
}
