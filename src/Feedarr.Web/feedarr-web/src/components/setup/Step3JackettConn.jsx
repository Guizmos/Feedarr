import React, { useEffect, useMemo, useRef, useState } from "react";
import { apiPost, apiPut } from "../../api/client.js";
import ItemRow from "../../ui/ItemRow.jsx";
import Modal from "../../ui/Modal.jsx";

const STORAGE_PROVIDER = "feedarr:jackettProvider";
const PROVIDERS = [
  {
    key: "jackett",
    label: "Jackett",
    placeholder: "http://localhost:9117",
    endpoint: "/api/jackett/indexers",
  },
  {
    key: "prowlarr",
    label: "Prowlarr",
    placeholder: "http://localhost:9696",
    endpoint: "/api/prowlarr/indexers",
  },
];
const STORAGE_KEYS = {
  jackett: {
    baseUrl: "feedarr:jackettBaseUrl",
    apiKey: "feedarr:jackettApiKey",
    indexers: "feedarr:jackettIndexersCache",
    configured: "feedarr:jackettConfigured",
  },
  prowlarr: {
    baseUrl: "feedarr:prowlarrBaseUrl",
    apiKey: "feedarr:prowlarrApiKey",
    indexers: "feedarr:prowlarrIndexersCache",
    configured: "feedarr:prowlarrConfigured",
  },
};
const EMPTY_CONFIG = { baseUrl: "", apiKey: "", indexers: [], configured: false };

function normalizeBaseUrl(value) {
  return String(value || "").trim().replace(/\/+$/, "");
}

function getProviderMeta(key) {
  return PROVIDERS.find((p) => p.key === key) || PROVIDERS[0];
}

function readConfigFromStorage(providerKey) {
  const keys = STORAGE_KEYS[providerKey];
  if (!keys || typeof window === "undefined") return { ...EMPTY_CONFIG };
  const baseUrl = window.localStorage.getItem(keys.baseUrl) || "";
  const apiKey = window.localStorage.getItem(keys.apiKey) || "";
  const configured = window.localStorage.getItem(keys.configured) === "true";
  const cached = window.localStorage.getItem(keys.indexers) || "";
  const cachedList = cached ? (JSON.parse(cached) || []) : [];
  return {
    baseUrl,
    apiKey,
    configured: configured && !!baseUrl && !!apiKey,
    indexers: Array.isArray(cachedList) ? cachedList : [],
  };
}

export default function Step3JackettConn({ onStatusChange, resetToken, initialStatus }) {
  const [provider, setProvider] = useState("");
  const [selectValue, setSelectValue] = useState("");
  const [baseUrl, setBaseUrl] = useState("");
  const [apiKey, setApiKey] = useState("");
  const [showKey, setShowKey] = useState(false);
  const [modalOpen, setModalOpen] = useState(false);
  const [testState, setTestState] = useState("idle"); // idle | testing | ok | error
  const [testMsg, setTestMsg] = useState("");
  const [testCount, setTestCount] = useState(0);
  const [testPulse, setTestPulse] = useState("");
  const [saved, setSaved] = useState(false);
  const [indexers, setIndexers] = useState([]);
  const [configs, setConfigs] = useState({
    jackett: { ...EMPTY_CONFIG },
    prowlarr: { ...EMPTY_CONFIG },
  });
  const [didLoad, setDidLoad] = useState(false);
  const skipResetRef = useRef(false);
  const pulseTimerRef = useRef(null);

  useEffect(() => {
    if (typeof window === "undefined") return;
    const legacyProvider = window.localStorage.getItem(STORAGE_PROVIDER) || "";
    if (legacyProvider === "prowlarr") {
      const legacyKeys = STORAGE_KEYS.jackett;
      const proKeys = STORAGE_KEYS.prowlarr;
      const hasNew = window.localStorage.getItem(proKeys.configured) === "true";
      const legacyBase = window.localStorage.getItem(legacyKeys.baseUrl) || "";
      const legacyKey = window.localStorage.getItem(legacyKeys.apiKey) || "";
      const legacyConfigured = window.localStorage.getItem(legacyKeys.configured) === "true";
      const legacyIndexers = window.localStorage.getItem(legacyKeys.indexers) || "";
      if (!hasNew && (legacyConfigured || legacyBase || legacyKey)) {
        window.localStorage.setItem(proKeys.baseUrl, legacyBase);
        window.localStorage.setItem(proKeys.apiKey, legacyKey);
        if (legacyIndexers) window.localStorage.setItem(proKeys.indexers, legacyIndexers);
        window.localStorage.setItem(proKeys.configured, legacyConfigured ? "true" : "false");
        window.localStorage.removeItem(legacyKeys.baseUrl);
        window.localStorage.removeItem(legacyKeys.apiKey);
        window.localStorage.removeItem(legacyKeys.indexers);
        window.localStorage.removeItem(legacyKeys.configured);
      }
    }

    const nextConfigs = {
      jackett: readConfigFromStorage("jackett"),
      prowlarr: readConfigFromStorage("prowlarr"),
    };
    setConfigs(nextConfigs);

    const preferred = legacyProvider === "prowlarr" ? "prowlarr" : "jackett";
    const preferredConfig = nextConfigs[preferred];
    const fallback = PROVIDERS.find((p) => nextConfigs[p.key]?.configured);
    const activeProvider = preferredConfig?.configured ? preferred : fallback?.key || "";
    if (activeProvider) {
      const activeConfig = nextConfigs[activeProvider] || { ...EMPTY_CONFIG };
      skipResetRef.current = true;
      setProvider(activeProvider);
      setSelectValue("");
      setBaseUrl(activeConfig.baseUrl);
      setApiKey(activeConfig.apiKey);
      setIndexers(Array.isArray(activeConfig.indexers) ? activeConfig.indexers : []);
      setTestCount(Array.isArray(activeConfig.indexers) ? activeConfig.indexers.length : 0);
      setTestState("ok");
      setTestMsg("Clés déjà enregistrées.");
      setSaved(true);
      onStatusChange?.({
        provider: activeProvider,
        ready: true,
        baseUrl: activeConfig.baseUrl,
        apiKey: activeConfig.apiKey,
        indexers: Array.isArray(activeConfig.indexers) ? activeConfig.indexers : [],
      });
    }
    setDidLoad(true);
  }, [onStatusChange]);

  useEffect(() => {
    if (!initialStatus?.ready) return;
    const nextBase = String(initialStatus.baseUrl || "");
    const nextKey = String(initialStatus.apiKey || "");
    if (!nextBase || !nextKey) return;
    const nextProvider = initialStatus.provider || "jackett";

    // Rehydrate only when wizard state changes, not on local form edits.
    skipResetRef.current = true;
    setProvider(nextProvider);
    setSelectValue("");
    setBaseUrl(nextBase);
    setApiKey(nextKey);
    setIndexers(Array.isArray(initialStatus.indexers) ? initialStatus.indexers : []);
    setTestState("ok");
    setTestMsg("Clés déjà enregistrées.");
    setTestCount(Array.isArray(initialStatus.indexers) ? initialStatus.indexers.length : 0);
    setSaved(true);
    setConfigs((prev) => ({
      ...prev,
      [nextProvider]: {
        baseUrl: nextBase,
        apiKey: nextKey,
        indexers: Array.isArray(initialStatus.indexers) ? initialStatus.indexers : [],
        configured: true,
      },
    }));
  }, [initialStatus]);

  useEffect(() => {
    if (!resetToken) return;
    if (typeof window !== "undefined") {
      Object.values(STORAGE_KEYS).forEach((keys) => {
        window.localStorage.removeItem(keys.baseUrl);
        window.localStorage.removeItem(keys.apiKey);
        window.localStorage.removeItem(keys.indexers);
        window.localStorage.removeItem(keys.configured);
      });
      window.localStorage.removeItem(STORAGE_PROVIDER);
    }
    setConfigs({
      jackett: { ...EMPTY_CONFIG },
      prowlarr: { ...EMPTY_CONFIG },
    });
    setProvider("");
    setSelectValue("");
    setBaseUrl("");
    setApiKey("");
    setSaved(false);
    setTestState("idle");
    setTestMsg("");
    setTestCount(0);
    setTestPulse("");
    setIndexers([]);
    onStatusChange?.({ provider: "", ready: false, baseUrl: "", apiKey: "", indexers: [] });
    setModalOpen(false);
  }, [resetToken, onStatusChange]);

  useEffect(() => {
    if (!didLoad) return;
    if (skipResetRef.current) {
      skipResetRef.current = false;
      return;
    }
    setTestState("idle");
    setTestMsg("");
    setTestCount(0);
    setTestPulse("");
    setSaved(false);
  }, [baseUrl, apiKey, provider, didLoad]);

  const canTest = useMemo(
    () => (provider === "jackett" || provider === "prowlarr") && !!baseUrl.trim() && !!apiKey.trim() && testState !== "testing",
    [provider, baseUrl, apiKey, testState]
  );
  const isTesting = testState === "testing";
  const canPrimary =
    testState === "ok"
      ? !!baseUrl.trim() && !!apiKey.trim() && !isTesting
      : canTest;
  const configuredProviders = useMemo(
    () => PROVIDERS.filter((p) => configs[p.key]?.configured),
    [configs]
  );
  const availableProviders = useMemo(
    () => PROVIDERS.filter((p) => !configs[p.key]?.configured),
    [configs]
  );
  const providerMeta = getProviderMeta(provider || "jackett");

  function openProviderModal(providerKey) {
    const cfg = configs[providerKey] || { ...EMPTY_CONFIG };
    skipResetRef.current = true;
    setProvider(providerKey);
    setSelectValue("");
    setBaseUrl(cfg.baseUrl || "");
    setApiKey(cfg.apiKey || "");
    setIndexers(Array.isArray(cfg.indexers) ? cfg.indexers : []);
    setTestCount(Array.isArray(cfg.indexers) ? cfg.indexers.length : 0);
    if (cfg.configured) {
      setTestState("ok");
      setTestMsg("Clés déjà enregistrées.");
      setSaved(true);
    } else {
      setTestState("idle");
      setTestMsg("");
      setSaved(false);
    }
    setTestPulse("");
    setShowKey(false);
    setModalOpen(true);
  }

  function triggerPulse(status) {
    if (pulseTimerRef.current) {
      clearTimeout(pulseTimerRef.current);
    }
    setTestPulse(status);
    pulseTimerRef.current = setTimeout(() => {
      setTestPulse("");
    }, 1200);
  }

  async function testConnection() {
    if (!canTest) return;
    setTestState("testing");
    setTestMsg("");
    setTestCount(0);

    const start = Date.now();
    let nextState = "error";
    let nextMsg = "";
    let nextCount = 0;
    let nextIndexers = [];
    const providerMeta = getProviderMeta(provider);
    const providerName = providerMeta.label;

    for (let attempt = 0; attempt < 2; attempt += 1) {
      try {
        const normalized = normalizeBaseUrl(baseUrl);
        const endpoint = providerMeta.endpoint;
        const res = await apiPost(endpoint, {
          baseUrl: normalized,
          apiKey: apiKey.trim(),
        });
        const list = Array.isArray(res) ? res : res?.indexers;
        const count = Array.isArray(list) ? list.length : 0;
        if (count > 0) {
          nextState = "ok";
          nextMsg = `Connexion OK (${count} indexeur${count > 1 ? "s" : ""})`;
          nextCount = count;
          nextIndexers = Array.isArray(list) ? list : [];
        } else {
          nextState = "error";
          nextMsg = "Connexion OK mais aucun indexeur détecté.";
        }
        break;
      } catch (e) {
        const rawMsg = e?.message || `Erreur de connexion ${providerName}.`;
        if (attempt === 0 && /invalid start of a value|unexpected token\s*</i.test(rawMsg)) {
          await new Promise((r) => setTimeout(r, 350));
          continue;
        }
        nextState = "error";
        nextMsg =
          /invalid start of a value|unexpected token\s*</i.test(rawMsg)
            ? `Réponse ${providerName} invalide. Vérifie l'URL et la clé API.`
            : rawMsg;
        break;
      }
    }

    const elapsed = Date.now() - start;
    if (elapsed < 2000) {
      await new Promise((r) => setTimeout(r, 2000 - elapsed));
    }

    setTestState(nextState);
    setTestMsg(nextMsg);
    setTestCount(nextCount);
    if (nextState === "ok") {
      setIndexers(nextIndexers);
      if (typeof window !== "undefined") {
        const keys = STORAGE_KEYS[provider];
        if (keys) {
          window.localStorage.setItem(keys.indexers, JSON.stringify(nextIndexers));
        }
      }
      if (nextCount > 200) {
        const sample = nextIndexers.slice(0, 3).map((x) => ({ id: x?.id, name: x?.name }));
        console.log(`[${providerName}] indexers count suspicious`, {
          url: normalizeBaseUrl(baseUrl),
          status: "ok",
          size: nextCount,
          shape: Array.isArray(nextIndexers) ? "array" : typeof nextIndexers,
          sample,
        });
      }
    }
    triggerPulse(nextState === "ok" ? "ok" : "error");
  }

  async function saveConfig() {
    if (testState !== "ok") return;
    const normalized = normalizeBaseUrl(baseUrl);
    const key = apiKey.trim();
    if (!normalized || !key) return;
    const keys = STORAGE_KEYS[provider];
    if (!keys || typeof window === "undefined") return;

    try {
      await apiPut(`/api/setup/indexer-providers/${provider}`, {
        baseUrl: normalized,
        apiKey: key,
        enabled: true,
      });
      console.info("[setup/providers] persisted", { provider, baseUrl: normalized });
    } catch (e) {
      const providerName = getProviderMeta(provider).label;
      setTestState("error");
      setSaved(false);
      setTestMsg(e?.message || `Impossible d'enregistrer ${providerName}.`);
      return;
    }

    window.localStorage.setItem(keys.baseUrl, normalized);
    window.localStorage.setItem(keys.apiKey, key);
    window.localStorage.setItem(keys.configured, "true");
    window.localStorage.setItem(STORAGE_PROVIDER, provider);
    if (indexers?.length) {
      window.localStorage.setItem(keys.indexers, JSON.stringify(indexers));
    }
    skipResetRef.current = true;
    setBaseUrl(normalized);
    setSaved(true);
    setModalOpen(false);
    setConfigs((prev) => ({
      ...prev,
      [provider]: {
        baseUrl: normalized,
        apiKey: key,
        indexers: Array.isArray(indexers) ? indexers : [],
        configured: true,
      },
    }));
    onStatusChange?.({
      provider,
      ready: true,
      baseUrl: normalized,
      apiKey: key,
      indexers: Array.isArray(indexers) ? indexers : [],
    });
  }

  function clearProviderConfig(providerKey) {
    const keys = STORAGE_KEYS[providerKey];
    if (typeof window !== "undefined" && keys) {
      window.localStorage.removeItem(keys.baseUrl);
      window.localStorage.removeItem(keys.apiKey);
      window.localStorage.removeItem(keys.indexers);
      window.localStorage.removeItem(keys.configured);
    }
    const nextConfigs = {
      ...configs,
      [providerKey]: { ...EMPTY_CONFIG },
    };
    setConfigs(nextConfigs);

    if (providerKey === provider) {
      const nextProvider = "";
      setProvider(nextProvider);
      setSelectValue("");
      setBaseUrl("");
      setApiKey("");
      setSaved(false);
      setTestState("idle");
      setTestMsg("");
      setTestCount(0);
      setTestPulse("");
      setIndexers([]);
      setModalOpen(false);
    }

    const remaining = PROVIDERS.find((p) => (p.key !== providerKey) && nextConfigs[p.key]?.configured);
    if (remaining) {
      const cfg = nextConfigs[remaining.key] || { ...EMPTY_CONFIG };
      if (typeof window !== "undefined") {
        window.localStorage.setItem(STORAGE_PROVIDER, remaining.key);
      }
      onStatusChange?.({
        provider: remaining.key,
        ready: cfg.configured,
        baseUrl: cfg.baseUrl,
        apiKey: cfg.apiKey,
        indexers: Array.isArray(cfg.indexers) ? cfg.indexers : [],
      });
    } else {
      if (typeof window !== "undefined") {
        window.localStorage.removeItem(STORAGE_PROVIDER);
      }
      onStatusChange?.({ provider: "", ready: false, baseUrl: "", apiKey: "", indexers: [] });
    }
  }

  return (
    <div className="setup-step setup-jackett-conn">
      <h2>Fournisseurs</h2>
      <p>Choisis ton fournisseur d’indexeurs.</p>

      {availableProviders.length > 0 && (
        <div className="setup-providers__add settings-row settings-row--ui-select">
          <label>Fournisseur</label>
          <select
            className="settings-field"
            value={selectValue}
            onChange={(e) => {
              const next = e.target.value;
              setSelectValue("");
              if (!next) return;
              openProviderModal(next);
            }}
          >
            <option value="" disabled>
              Sélectionner...
            </option>
            {availableProviders.map((opt) => (
              <option key={opt.key} value={opt.key}>
                {opt.label}
              </option>
            ))}
          </select>
        </div>
      )}

      <div className="setup-providers__list">
        <h4>Fournisseurs configurés</h4>
        {configuredProviders.length === 0 && (
          <div className="muted">Aucun fournisseur configuré.</div>
        )}
        {configuredProviders.length > 0 && (
          <div className="indexer-list">
            {configuredProviders.map((cfg, idx) => {
              const meta = configs[cfg.key]?.baseUrl || "";
              return (
                <ItemRow
                  key={cfg.key}
                  id={idx + 1}
                  title={cfg.label}
                  meta={meta}
                  enabled
                  badges={[{ label: "Configuré", className: "pill-ok" }]}
                  actions={[
                    {
                      icon: "edit",
                      title: "Modifier",
                      onClick: () => openProviderModal(cfg.key),
                      disabled: testState === "testing",
                    },
                    {
                      icon: "delete",
                      title: "Supprimer",
                      onClick: () => clearProviderConfig(cfg.key),
                      disabled: testState === "testing",
                      className: "iconbtn--danger",
                    },
                  ]}
                  showToggle={false}
                />
              );
            })}
          </div>
        )}
      </div>

      <Modal
        open={modalOpen}
        title={`Configurer ${providerMeta.label}`}
        onClose={() => setModalOpen(false)}
        width={520}
      >
        <div className="formgrid formgrid--edit">
          <div className="field">
            <label>Base URL</label>
            <input
              value={baseUrl}
              onChange={(e) => setBaseUrl(e.target.value)}
              placeholder={providerMeta.placeholder}
              disabled={testState === "testing"}
            />
          </div>
          <div className="field">
            <label>API key</label>
            <div className="setup-jackett-key">
              <input
                type={showKey ? "text" : "password"}
                value={apiKey}
                onChange={(e) => setApiKey(e.target.value)}
                placeholder={`Clé API ${providerMeta.label}`}
                disabled={testState === "testing"}
              />
              <button
                className="btn"
                type="button"
                onClick={() => setShowKey((v) => !v)}
                disabled={testState === "testing"}
              >
                {showKey ? "Masquer" : "Afficher"}
              </button>
            </div>
          </div>
        </div>

        <div className="setup-jackett-hint">
          Indexeurs détectés : {testCount || 0}
        </div>

        {testState === "error" && <div className="onboarding__error">{testMsg}</div>}
        {testState === "ok" && (
          <div className="onboarding__ok">
            {testMsg} {saved ? "— enregistré" : ""}
          </div>
        )}

        <div className="setup-jackett-footer">
          <button
            className={`btn btn-accent btn-test${testPulse === "ok" ? " btn-pulse-ok" : ""}${testPulse === "error" ? " btn-pulse-err" : ""}`}
            type="button"
            onClick={testState === "ok" ? saveConfig : () => testConnection()}
            disabled={!canPrimary}
          >
            {isTesting ? (
              <>
                <span className="btn-spinner" />
                Test en cours...
              </>
            ) : testPulse === "ok" ? (
              "Valide"
            ) : testPulse === "error" ? (
              "Invalide"
            ) : testState === "ok" ? (
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
