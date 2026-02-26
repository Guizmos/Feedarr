import React, { useEffect, useMemo, useRef, useState } from "react";
import { apiPost, apiPut } from "../../api/client.js";
import ItemRow from "../../ui/ItemRow.jsx";
import Modal from "../../ui/Modal.jsx";
import { tr } from "../../app/uiText.js";

const STORAGE_PROVIDER = "feedarr:jackettProvider";
const PROVIDERS = [
  {
    key: "jackett",
    label: "Jackett",
    placeholder: "http://192.168.1.x:9117 ou https://domaine.tld/jackett",
    endpoint: "/api/jackett/indexers",
  },
  {
    key: "prowlarr",
    label: "Prowlarr",
    placeholder: "http://192.168.1.x:9696 ou https://domaine.tld/prowlarr",
    endpoint: "/api/prowlarr/indexers",
  },
];
const STORAGE_KEYS = {
  jackett: {
    baseUrl: "feedarr:jackettBaseUrl",
    apiKey: "feedarr:jackettApiKey",
    indexers: "feedarr:jackettIndexersCache",
    configured: "feedarr:jackettConfigured",
    manualOnly: "feedarr:jackettManualOnly",
  },
  prowlarr: {
    baseUrl: "feedarr:prowlarrBaseUrl",
    apiKey: "feedarr:prowlarrApiKey",
    indexers: "feedarr:prowlarrIndexersCache",
    configured: "feedarr:prowlarrConfigured",
    manualOnly: "feedarr:prowlarrManualOnly",
  },
};
const EMPTY_CONFIG = { baseUrl: "", indexers: [], configured: false, manualOnly: false };

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
  const configured = window.localStorage.getItem(keys.configured) === "true";
  const cached = window.localStorage.getItem(keys.indexers) || "";
  const cachedList = cached ? (JSON.parse(cached) || []) : [];
  const manualOnly = window.localStorage.getItem(keys.manualOnly) === "true";
  return {
    baseUrl,
    configured: configured && !!baseUrl,
    indexers: Array.isArray(cachedList) ? cachedList : [],
    manualOnly,
  };
}

export default function Step3JackettConn({ onStatusChange, resetToken, initialStatus }) {
  const [provider, setProvider] = useState("");
  const [selectValue, setSelectValue] = useState("");
  const [baseUrl, setBaseUrl] = useState("");
  const [apiKey, setApiKey] = useState("");
  const [showKey, setShowKey] = useState(false);
  const [modalOpen, setModalOpen] = useState(false);
  const [testState, setTestState] = useState("idle"); // idle | testing | ok | manual | error
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
    Object.values(STORAGE_KEYS).forEach((keys) => {
      window.localStorage.removeItem(keys.apiKey);
    });

    const legacyProvider = window.localStorage.getItem(STORAGE_PROVIDER) || "";
    if (legacyProvider === "prowlarr") {
      const legacyKeys = STORAGE_KEYS.jackett;
      const proKeys = STORAGE_KEYS.prowlarr;
      const hasNew = window.localStorage.getItem(proKeys.configured) === "true";
      const legacyBase = window.localStorage.getItem(legacyKeys.baseUrl) || "";
      const legacyConfigured = window.localStorage.getItem(legacyKeys.configured) === "true";
      const legacyIndexers = window.localStorage.getItem(legacyKeys.indexers) || "";
      if (!hasNew && (legacyConfigured || legacyBase)) {
        window.localStorage.setItem(proKeys.baseUrl, legacyBase);
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
      setApiKey("");
      setIndexers(Array.isArray(activeConfig.indexers) ? activeConfig.indexers : []);
      setTestCount(Array.isArray(activeConfig.indexers) ? activeConfig.indexers.length : 0);
      setTestState(activeConfig.manualOnly ? "manual" : "ok");
      setTestMsg(
        activeConfig.manualOnly
          ? tr("Configuration enregistree. Ajoute les indexeurs manuellement a l'etape suivante.", "Configuration saved. Add indexers manually at the next step.")
          : tr("Cles deja enregistrees.", "Keys already saved.")
      );
      setSaved(true);
      onStatusChange?.({
        provider: activeProvider,
        ready: true,
        baseUrl: activeConfig.baseUrl,
        indexers: Array.isArray(activeConfig.indexers) ? activeConfig.indexers : [],
        manualOnly: !!activeConfig.manualOnly,
      });
    }
    setDidLoad(true);
  }, [onStatusChange]);

  useEffect(() => {
    if (!initialStatus?.ready) return;
    const nextBase = String(initialStatus.baseUrl || "");
    if (!nextBase) return;
    const nextProvider = initialStatus.provider || "jackett";

    // Rehydrate only when wizard state changes, not on local form edits.
    skipResetRef.current = true;
    setProvider(nextProvider);
    setSelectValue("");
    setBaseUrl(nextBase);
    setApiKey("");
    setIndexers(Array.isArray(initialStatus.indexers) ? initialStatus.indexers : []);
    setTestState(initialStatus?.manualOnly ? "manual" : "ok");
    setTestMsg(
      initialStatus?.manualOnly
        ? tr("Configuration enregistree. Ajoute les indexeurs manuellement a l'etape suivante.", "Configuration saved. Add indexers manually at the next step.")
        : tr("Cles deja enregistrees.", "Keys already saved.")
    );
    setTestCount(Array.isArray(initialStatus.indexers) ? initialStatus.indexers.length : 0);
    setSaved(true);
    setConfigs((prev) => ({
      ...prev,
      [nextProvider]: {
        baseUrl: nextBase,
        indexers: Array.isArray(initialStatus.indexers) ? initialStatus.indexers : [],
        configured: true,
        manualOnly: !!initialStatus?.manualOnly,
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
        window.localStorage.removeItem(keys.manualOnly);
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
    onStatusChange?.({ provider: "", ready: false, baseUrl: "", indexers: [], manualOnly: false });
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
    (testState === "ok" || testState === "manual")
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
    setApiKey("");
    setIndexers(Array.isArray(cfg.indexers) ? cfg.indexers : []);
    setTestCount(Array.isArray(cfg.indexers) ? cfg.indexers.length : 0);
    if (cfg.configured) {
      setTestState(cfg.manualOnly ? "manual" : "ok");
      setTestMsg(
        cfg.manualOnly
          ? tr("Configuration enregistree. Ajoute les indexeurs manuellement a l'etape suivante.", "Configuration saved. Add indexers manually at the next step.")
          : tr("Cles deja enregistrees.", "Keys already saved.")
      );
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
          nextMsg = tr(
            `Connexion OK (${count} indexeur${count > 1 ? "s" : ""})`,
            `Connection OK (${count} indexer${count > 1 ? "s" : ""})`
          );
          nextCount = count;
          nextIndexers = Array.isArray(list) ? list : [];
        } else {
          nextState = "manual";
          nextMsg = tr(
            "Connexion OK, mais aucun indexeur recupere automatiquement. Tu pourras les ajouter manuellement a l'etape suivante (Copy Torznab Feed + cle API).",
            "Connection OK, but no indexer was auto-fetched. You can add them manually at the next step (Copy Torznab Feed + API key)."
          );
        }
        break;
      } catch (e) {
        const rawMsg = e?.message || `Erreur de connexion ${providerName}.`;
        if (attempt === 0 && /invalid start of a value|unexpected token\s*</i.test(rawMsg)) {
          await new Promise((r) => setTimeout(r, 350));
          continue;
        }
        nextState = "manual";
        nextMsg =
          /invalid start of a value|unexpected token\s*</i.test(rawMsg)
            ? tr(
              `Recuperation auto des indexeurs ${providerName} impossible. Enregistre la config puis ajoute les indexeurs manuellement a l'etape suivante (Copy Torznab Feed + cle API).`,
              `Unable to auto-fetch ${providerName} indexers. Save the config then add indexers manually at the next step (Copy Torznab Feed + API key).`
            )
            : `${rawMsg} ${tr(
              "Tu peux quand meme enregistrer et ajouter les indexeurs manuellement a l'etape suivante.",
              "You can still save and add indexers manually at the next step."
            )}`;
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
    if (nextState === "ok" || nextState === "manual") {
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
    triggerPulse(nextState === "error" ? "error" : "ok");
  }

  async function saveConfig() {
    if (testState !== "ok" && testState !== "manual") return;
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
      setTestMsg(
        e?.message
        || tr(`Impossible d'enregistrer ${providerName}.`, `Unable to save ${providerName}.`)
      );
      return;
    }

    window.localStorage.setItem(keys.baseUrl, normalized);
    window.localStorage.removeItem(keys.apiKey);
    window.localStorage.setItem(keys.configured, "true");
    window.localStorage.setItem(keys.manualOnly, testState === "manual" ? "true" : "false");
    window.localStorage.setItem(STORAGE_PROVIDER, provider);
    window.localStorage.setItem(keys.indexers, JSON.stringify(indexers || []));
    skipResetRef.current = true;
    setBaseUrl(normalized);
    setSaved(true);
    setModalOpen(false);
    setConfigs((prev) => ({
      ...prev,
      [provider]: {
        baseUrl: normalized,
        indexers: Array.isArray(indexers) ? indexers : [],
        configured: true,
        manualOnly: testState === "manual",
      },
    }));
    onStatusChange?.({
      provider,
      ready: true,
      baseUrl: normalized,
      indexers: Array.isArray(indexers) ? indexers : [],
      manualOnly: testState === "manual",
    });
  }

  function clearProviderConfig(providerKey) {
    const keys = STORAGE_KEYS[providerKey];
    if (typeof window !== "undefined" && keys) {
      window.localStorage.removeItem(keys.baseUrl);
      window.localStorage.removeItem(keys.apiKey);
      window.localStorage.removeItem(keys.indexers);
      window.localStorage.removeItem(keys.configured);
      window.localStorage.removeItem(keys.manualOnly);
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
        indexers: Array.isArray(cfg.indexers) ? cfg.indexers : [],
        manualOnly: !!cfg.manualOnly,
      });
    } else {
      if (typeof window !== "undefined") {
        window.localStorage.removeItem(STORAGE_PROVIDER);
      }
      onStatusChange?.({ provider: "", ready: false, baseUrl: "", indexers: [], manualOnly: false });
    }
  }

  return (
    <div className="setup-step setup-jackett-conn">
      <h2>{tr("Fournisseurs", "Providers")}</h2>
      <p>{tr("Choisis ton fournisseur d'indexeurs.", "Choose your indexer provider.")}</p>

      {availableProviders.length > 0 && (
        <div className="setup-providers__add settings-row settings-row--ui-select">
          <label>{tr("Fournisseur", "Provider")}</label>
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
              {tr("Selectionner...", "Select...")}
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
        <h4>{tr("Fournisseurs configures", "Configured providers")}</h4>
        {configuredProviders.length === 0 && (
          <div className="muted">{tr("Aucun fournisseur configure.", "No provider configured.")}</div>
        )}
        {configuredProviders.length > 0 && (
          <div className="indexer-list">
            {configuredProviders.map((cfg, idx) => {
              const meta = configs[cfg.key]?.baseUrl || "";
              const badges = [{ label: tr("Configure", "Configured"), className: "pill-ok" }];
              if (configs[cfg.key]?.manualOnly) {
                badges.push({ label: tr("Ajout manuel", "Manual add"), className: "pill-warn" });
              }
              return (
                <ItemRow
                  key={cfg.key}
                  id={idx + 1}
                  title={cfg.label}
                  meta={meta}
                  enabled
                  badges={badges}
                  actions={[
                    {
                      icon: "edit",
                      title: tr("Modifier", "Edit"),
                      onClick: () => openProviderModal(cfg.key),
                      disabled: testState === "testing",
                    },
                    {
                      icon: "delete",
                      title: tr("Supprimer", "Delete"),
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
        title={`${tr("Configurer", "Configure")} ${providerMeta.label}`}
        onClose={() => setModalOpen(false)}
        width={520}
      >
        <div className="formgrid formgrid--edit">
          <div className="field">
            <label>{tr("Base URL", "Base URL")}</label>
            <input
              value={baseUrl}
              onChange={(e) => setBaseUrl(e.target.value)}
              placeholder={providerMeta.placeholder}
              disabled={testState === "testing"}
            />
            <span className="field-hint">{tr("IP, hostname ou URL reverse proxy (http/https)", "IP, hostname or reverse proxy URL (http/https)")}</span>
          </div>
          <div className="field">
            <label>{tr("API key", "API key")}</label>
            <div className="setup-jackett-key">
              <input
                type={showKey ? "text" : "password"}
                value={apiKey}
                onChange={(e) => setApiKey(e.target.value)}
                placeholder={`${tr("Cle API", "API key")} ${providerMeta.label}`}
                disabled={testState === "testing"}
              />
              <button
                className="btn"
                type="button"
                onClick={() => setShowKey((v) => !v)}
                disabled={testState === "testing"}
              >
                {showKey ? tr("Masquer", "Hide") : tr("Afficher", "Show")}
              </button>
            </div>
          </div>
        </div>

        <div className="setup-jackett-hint">
          {tr("Indexeurs detectes", "Detected indexers")}: {testCount || 0}
        </div>

        {testState === "error" && <div className="onboarding__error">{testMsg}</div>}
        {testState === "ok" && (
          <div className="onboarding__ok">
            {testMsg} {saved ? ` - ${tr("enregistre", "saved")}` : ""}
          </div>
        )}
        {testState === "manual" && (
          <div className="onboarding__warn">
            {testMsg} {saved ? ` - ${tr("enregistre", "saved")}` : ""}
          </div>
        )}

        <div className="setup-jackett-footer">
          <button
            className={`btn btn-accent btn-test${testPulse === "ok" ? " btn-pulse-ok" : ""}${testPulse === "error" ? " btn-pulse-err" : ""}`}
            type="button"
            onClick={testState === "ok" || testState === "manual" ? saveConfig : () => testConnection()}
            disabled={!canPrimary}
          >
            {isTesting ? (
              <>
                <span className="btn-spinner" />
                {tr("Test en cours...", "Test in progress...")}
              </>
            ) : testPulse === "ok" ? (
              tr("Valide", "Valid")
            ) : testPulse === "error" ? (
              tr("Invalide", "Invalid")
            ) : testState === "ok" ? (
              tr("Sauvegarder", "Save")
            ) : testState === "manual" ? (
              tr("Sauvegarder (manuel)", "Save (manual)")
            ) : (
              tr("Tester", "Test")
            )}
          </button>
        </div>
      </Modal>
    </div>
  );
}
