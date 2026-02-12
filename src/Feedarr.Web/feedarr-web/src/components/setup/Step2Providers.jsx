import React, { useCallback, useEffect, useMemo, useRef, useState } from "react";
import ItemRow from "../../ui/ItemRow.jsx";
import Modal from "../../ui/Modal.jsx";
import { apiGet, apiPost, apiPut } from "../../api/client.js";

const DEFAULT_VALIDATION = {
  tmdb: null,
  tvmaze: null,
  fanart: null,
  igdb: null,
};

const PROVIDERS = [
  {
    key: "tmdb",
    title: "TMDB",
    kind: "tmdb",
    inputKey: "tmdbApiKey",
    inputLabel: "Clé API",
    enabledKey: "tmdbEnabled",
    hasConfig: (f) => !!f?.hasTmdbApiKey,
    links: [
      { label: "Lien vers TMDB", href: "https://www.themoviedb.org/settings/api", variant: "tmdb" },
    ],
  },
  {
    key: "fanart",
    title: "Fanart TV",
    kind: "fanart",
    inputKey: "fanartApiKey",
    inputLabel: "Clé API",
    enabledKey: "fanartEnabled",
    hasConfig: (f) => !!f?.hasFanartApiKey,
    links: [
      { label: "API key", href: "https://fanart.tv/get-an-api-key/" },
    ],
  },
  {
    key: "igdb",
    title: "IGDB",
    kind: "igdb",
    inputKey: "igdbClientId",
    inputKey2: "igdbClientSecret",
    inputLabel: "Client ID",
    inputLabel2: "Client Secret",
    enabledKey: "igdbEnabled",
    hasConfig: (f) => !!f?.hasIgdbClientId && !!f?.hasIgdbClientSecret,
    links: [
      { label: "API key", href: "https://dev.twitch.tv/console/apps" },
    ],
  },
  {
    key: "tvmaze",
    title: "TVmaze",
    kind: "tvmaze",
    inputKey: "tvmazeApiKey",
    inputLabel: "Clé API (optionnel)",
    enabledKey: "tvmazeEnabled",
    hasConfig: (f) => !!f?.hasTvmazeApiKey || !!f?.tvmazeEnabled,
    note: "Pas besoin de clé sauf abonnement.",
    links: [
      { label: "API info", href: "https://www.tvmaze.com/api" },
    ],
  },
];

export default function Step2Providers({ validation, onValidationChange, onAllProvidersAddedChange }) {
  const [flags, setFlags] = useState(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");
  const [addKey, setAddKey] = useState("");
  const [modalOpen, setModalOpen] = useState(false);
  const [modalProvider, setModalProvider] = useState(null);
  const [modalValue, setModalValue] = useState("");
  const [modalValue2, setModalValue2] = useState("");
  const [modalError, setModalError] = useState("");
  const [modalResult, setModalResult] = useState("");
  const [modalTesting, setModalTesting] = useState(false);
  const [modalTestState, setModalTestState] = useState("idle");
  const [modalPulse, setModalPulse] = useState("");
  const [modalSaving, setModalSaving] = useState(false);
  const [busyKey, setBusyKey] = useState(null);
  const [localValidation, setLocalValidation] = useState(DEFAULT_VALIDATION);
  const [providerInputs, setProviderInputs] = useState({});
  const pulseTimerRef = useRef(null);

  const validationState = validation || localValidation;

  const updateValidation = useCallback(
    (updater) => {
      if (onValidationChange) {
        onValidationChange((prev) => {
          const base = prev || DEFAULT_VALIDATION;
          return typeof updater === "function" ? updater(base) : updater;
        });
      } else {
        setLocalValidation((prev) =>
          typeof updater === "function" ? updater(prev) : updater
        );
      }
    },
    [onValidationChange]
  );

  const loadFlags = useCallback(async () => {
    setLoading(true);
    setError("");
    try {
      const data = await apiGet("/api/settings/external");
      setFlags(data || null);
    } catch (e) {
      setError(e?.message || "Erreur chargement providers");
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    loadFlags();
  }, [loadFlags]);

  const providers = useMemo(() => PROVIDERS, []);

  const isConfigured = useCallback((provider) => {
    if (!flags) return false;
    const enabled = flags?.[provider.enabledKey] !== false;
    if (!enabled) return false;
    return provider.hasConfig(flags);
  }, [flags]);

  const availableProviders = useMemo(
    () => providers.filter((p) => !isConfigured(p)),
    [providers, isConfigured]
  );
  const configuredProviders = useMemo(
    () => providers.filter((p) => isConfigured(p)),
    [providers, isConfigured]
  );
  const allProvidersAdded = useMemo(
    () => !!flags && availableProviders.length === 0,
    [flags, availableProviders.length]
  );

  useEffect(() => {
    onAllProvidersAddedChange?.(allProvidersAdded);
  }, [allProvidersAdded, onAllProvidersAddedChange]);

  function openModal(provider, prefill = {}) {
    setModalProvider(provider);
    setModalValue(prefill.value1 || "");
    setModalValue2(prefill.value2 || "");
    setModalError("");
    setModalResult("");
    setModalTesting(false);
    setModalTestState("idle");
    setModalPulse("");
    setModalOpen(true);
  }

  function closeModal() {
    if (modalSaving || modalTesting) return;
    setModalOpen(false);
    setModalProvider(null);
    setModalValue("");
    setModalValue2("");
    setModalError("");
    setModalResult("");
    setModalTesting(false);
    setModalTestState("idle");
    setModalPulse("");
  }

  function handleSelectChange(e) {
    const key = e.target.value;
    setAddKey(key);
    const provider = providers.find((p) => p.key === key);
    if (!provider) return;
    const cached = providerInputs[key] || {};
    openModal(provider, cached);
    setAddKey("");
  }

  function triggerPulse(status) {
    if (pulseTimerRef.current) {
      clearTimeout(pulseTimerRef.current);
    }
    setModalPulse(status);
    pulseTimerRef.current = setTimeout(() => {
      setModalPulse("");
    }, 1200);
  }

  function buildPayload(v1, v2) {
    const payload = {};
    if (modalProvider?.key === "igdb") {
      payload.igdbClientId = v1;
      payload.igdbClientSecret = v2;
    } else if (modalProvider?.key === "tvmaze") {
      if (v1) payload.tvmazeApiKey = v1;
    } else if (modalProvider) {
      payload[modalProvider.inputKey] = v1;
    }
    if (modalProvider) {
      payload[modalProvider.enabledKey] = true;
    }
    return payload;
  }

  function getValidationError(v1, v2) {
    if (!modalProvider) return "Provider invalide.";
    if (modalProvider.key === "igdb") {
      if (!v1 || !v2) return "Client ID et Client Secret requis.";
      return "";
    }
    if (modalProvider.key !== "tvmaze" && !v1) {
      return "Clé API requise.";
    }
    return "";
  }

  async function testProvider() {
    if (!modalProvider) return;
    setModalError("");
    setModalResult("");

    const v1 = modalValue.trim();
    const v2 = modalValue2.trim();
    const validationError = getValidationError(v1, v2);
    if (validationError) {
      setModalError(validationError);
      setModalTestState("error");
      triggerPulse("error");
      return;
    }

    setModalTesting(true);
    setModalTestState("idle");
    setModalPulse("");

    const start = Date.now();
    let ok = false;
    let resultMsg = "";
    let errorMsg = "";

    try {
      const payload = buildPayload(v1, v2);
      await apiPut("/api/settings/external", payload);
      const res = await apiPost("/api/settings/external/test", { kind: modalProvider.kind });
      ok = !!res?.ok;
      updateValidation((prev) => ({ ...prev, [modalProvider.key]: ok ? "ok" : "error" }));
      if (ok) {
        resultMsg = "Test OK";
      } else {
        errorMsg = res?.error ? `Test KO: ${res.error}` : "Test KO";
      }
      await loadFlags();
    } catch (e) {
      errorMsg = e?.message || "Erreur test provider";
    } finally {
      const elapsed = Date.now() - start;
      if (elapsed < 2000) {
        await new Promise((r) => setTimeout(r, 2000 - elapsed));
      }
      setModalTesting(false);
      setModalTestState(ok ? "ok" : "error");
      if (ok) {
        setModalResult(resultMsg);
        setModalError("");
      } else {
        setModalError(errorMsg || "Test KO");
        setModalResult("");
      }
      triggerPulse(ok ? "ok" : "error");
    }
  }

  async function saveProvider() {
    if (!modalProvider || modalTestState !== "ok") return;
    setModalError("");
    setModalResult("");

    const v1 = modalValue.trim();
    const v2 = modalValue2.trim();

    setModalSaving(true);
    setBusyKey(modalProvider.key);
    try {
      const payload = buildPayload(v1, v2);
      await apiPut("/api/settings/external", payload);
      setProviderInputs((prev) => ({
        ...prev,
        [modalProvider.key]: { value1: v1, value2: v2 },
      }));
      await loadFlags();
      closeModal();
    } catch (e) {
      setModalError(e?.message || "Erreur sauvegarde");
    } finally {
      setModalSaving(false);
      setBusyKey(null);
    }
  }

  useEffect(() => {
    if (!modalOpen) return;
    setModalTestState("idle");
    setModalPulse("");
    setModalError("");
    setModalResult("");
  }, [modalValue, modalValue2, modalProvider, modalOpen]);

  async function deleteProvider(provider) {
    setError("");
    setBusyKey(provider.key);
    const payload = {};
    if (provider.key === "igdb") {
      payload.igdbClientId = "";
      payload.igdbClientSecret = "";
    } else if (provider.key === "tvmaze") {
      payload.tvmazeApiKey = "";
    } else {
      payload[provider.inputKey] = "";
    }
    payload[provider.enabledKey] = false;

    try {
      await apiPut("/api/settings/external", payload);
      updateValidation((prev) => ({ ...prev, [provider.key]: null }));
      setProviderInputs((prev) => ({
        ...prev,
        [provider.key]: { value1: "", value2: "" },
      }));
      await loadFlags();
    } catch (e) {
      setError(e?.message || "Erreur suppression");
    } finally {
      setBusyKey(null);
    }
  }

  return (
    <div className="setup-step setup-providers">
      <h2>Providers</h2>
      <p>Ajoute au moins un provider et valide la connexion.</p>

      {error && <div className="onboarding__error">{error}</div>}

      {availableProviders.length > 0 && (
        <div className="setup-providers__add settings-row settings-row--ui-select">
          <label>Provider</label>
          <select className="settings-field" value={addKey} onChange={handleSelectChange}>
            <option value="" disabled>
              Sélectionner...
            </option>
            {availableProviders.map((p) => (
              <option key={p.key} value={p.key}>
                {p.title}
              </option>
            ))}
          </select>
        </div>
      )}

      <div className="setup-providers__list">
        <h4>Providers configurés</h4>
        {loading && (
          <div className="indexer-card">
            <div className="indexer-row">
              <span className="indexer-url muted">Chargement...</span>
            </div>
          </div>
        )}

        {!loading && configuredProviders.length === 0 && (
          <div className="muted">Aucun provider configuré.</div>
        )}

        {!loading && configuredProviders.length > 0 && (
          <div className="indexer-list">
            {configuredProviders.map((provider, idx) => {
              const testStatus = validationState?.[provider.key];
              const statusClass = [
                testStatus === "ok" && "test-ok",
                testStatus === "error" && "test-err",
              ].filter(Boolean).join(" ");

              const badges = [
                {
                  label: "OK",
                  className: "pill-ok",
                },
              ];
              if (testStatus === "ok") badges.push({ label: "TEST OK", className: "pill-ok" });
              if (testStatus === "error") badges.push({ label: "TEST KO", className: "pill-warn" });

              const cached = providerInputs[provider.key] || {};

              return (
                <div key={provider.key} className="setup-provider-block">
                  <ItemRow
                    id={idx + 1}
                    title={provider.title}
                    meta={provider.note || undefined}
                    enabled
                    statusClass={statusClass}
                    badges={badges}
                    actions={[
                      {
                        icon: "edit",
                        title: "Modifier",
                        onClick: () => openModal(provider, cached),
                        disabled: busyKey === provider.key || modalSaving || modalTesting,
                      },
                      {
                        icon: "delete",
                        title: "Supprimer",
                        onClick: () => deleteProvider(provider),
                        disabled: busyKey === provider.key || modalSaving || modalTesting,
                        className: "iconbtn--danger",
                      },
                    ]}
                    showToggle={false}
                  />
                </div>
              );
            })}
          </div>
        )}
      </div>

      <Modal
        open={modalOpen}
        title={modalProvider ? `Configurer : ${modalProvider.title}` : "Configurer"}
        onClose={closeModal}
        width={520}
      >
        <div className="formgrid formgrid--edit">
          <div className="field">
            <label>{modalProvider?.inputLabel || "Clé API"}</label>
            <input
              value={modalValue}
              onChange={(e) => setModalValue(e.target.value)}
              placeholder={modalProvider?.key === "tvmaze" ? "Optionnel" : "Entrez la clé API"}
              disabled={modalSaving || modalTesting}
            />
          </div>
          {modalProvider?.inputKey2 && (
            <div className="field">
              <label>{modalProvider?.inputLabel2 || "Client Secret"}</label>
              <input
                value={modalValue2}
                onChange={(e) => setModalValue2(e.target.value)}
                placeholder="Entrez le client secret"
                disabled={modalSaving || modalTesting}
              />
            </div>
          )}
        </div>

        {modalProvider?.key === "tvmaze" && (
          <div className="setup-provider-note">
            Pas besoin de clé sauf abonnement.
          </div>
        )}

        {modalError && <div className="onboarding__error">{modalError}</div>}
        {modalResult && <div className="onboarding__ok">{modalResult}</div>}

        <div
          className="setup-actions setup-actions--providers"
          style={{ justifyContent: modalProvider?.links?.length ? "space-between" : "flex-end" }}
        >
          {modalProvider?.links?.length > 0 && (
            <div className="setup-provider-links" style={{ marginTop: 0, padding: 0 }}>
              {modalProvider.links.map((link) => (
                <a
                  key={link.href}
                  className={`setup-provider-link${link.variant ? ` setup-provider-link--${link.variant}` : ""}`}
                  href={link.href}
                >
                  {link.label}
                </a>
              ))}
            </div>
          )}
          <button
            className={`btn btn-accent btn-test${modalPulse === "ok" ? " btn-pulse-ok" : ""}${modalPulse === "error" ? " btn-pulse-err" : ""}`}
            type="button"
            onClick={modalTestState === "ok" ? saveProvider : testProvider}
            disabled={
              modalSaving ||
              modalTesting ||
              (modalTestState === "ok"
                ? false
                : (modalProvider?.key === "igdb"
                  ? !modalValue.trim() || !modalValue2.trim()
                  : modalProvider?.key !== "tvmaze" && !modalValue.trim()))
            }
          >
            {modalTesting ? (
              <>
                <span className="btn-spinner" />
                Test en cours...
              </>
            ) : modalPulse === "ok" ? (
              "Valide"
            ) : modalPulse === "error" ? (
              "Invalide"
            ) : modalTestState === "ok" ? (
              "Sauvegarder"
            ) : (
              "Tester"
            )}
          </button>
        </div>
      </Modal>
    </div>
  );
}
