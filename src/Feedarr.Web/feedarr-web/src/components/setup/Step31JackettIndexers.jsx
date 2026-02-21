import React, { useCallback, useEffect, useMemo, useState } from "react";
import ItemRow from "../../ui/ItemRow.jsx";
import Modal from "../../ui/Modal.jsx";
import ToggleSwitch from "../../ui/ToggleSwitch.jsx";
import { apiDelete, apiGet, apiPost, apiPut } from "../../api/client.js";
import { tr } from "../../app/uiText.js";

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
const PROVIDERS = [
  { key: "jackett", label: "Jackett" },
  { key: "prowlarr", label: "Prowlarr" },
];
const EMPTY_CONFIG = { baseUrl: "", providerId: null, indexers: [], configured: false, manualOnly: false };
const MANUAL_INDEXER_VALUE = "__manual__";

function normalizeUrl(value) {
  return String(value || "").trim().replace(/\/+$/, "");
}

function readProviderConfig(providerKey) {
  const keys = STORAGE_KEYS[providerKey];
  if (!keys || typeof window === "undefined") return { ...EMPTY_CONFIG };
  const baseUrl = window.localStorage.getItem(keys.baseUrl) || "";
  const configured = window.localStorage.getItem(keys.configured) === "true";
  const cached = window.localStorage.getItem(keys.indexers) || "";
  const list = cached ? (JSON.parse(cached) || []) : [];
  const manualOnly = window.localStorage.getItem(keys.manualOnly) === "true";
  return {
    baseUrl,
    providerId: null,
    configured: configured && !!baseUrl,
    indexers: Array.isArray(list) ? list : [],
    manualOnly,
  };
}

function getProviderLabel(providerKey) {
  return PROVIDERS.find((p) => p.key === providerKey)?.label || tr("Fournisseur", "Provider");
}

export default function Step31JackettIndexers({ onHasSourcesChange, onBack, jackettConfig }) {
  const [providerConfigs, setProviderConfigs] = useState({
    jackett: { ...EMPTY_CONFIG },
    prowlarr: { ...EMPTY_CONFIG },
  });
  const [providerIndexerWarnings, setProviderIndexerWarnings] = useState({
    jackett: "",
    prowlarr: "",
  });
  const [sources, setSources] = useState([]);
  const [addIds, setAddIds] = useState({ jackett: "", prowlarr: "" });
  const [modalOpen, setModalOpen] = useState(false);
  const [selectedIndexer, setSelectedIndexer] = useState(null);
  const [selectedProviderKey, setSelectedProviderKey] = useState("");
  const [manualMode, setManualMode] = useState(false);
  const [manualName, setManualName] = useState("");
  const [manualTorznabUrl, setManualTorznabUrl] = useState("");
  const [editingSource, setEditingSource] = useState(null);
  const [capsLoading, setCapsLoading] = useState(false);
  const [capsError, setCapsError] = useState("");
  const [capsOk, setCapsOk] = useState("");
  const [capsWarning, setCapsWarning] = useState("");
  const [capsCategories, setCapsCategories] = useState([]);
  const [useRecommendedFilter, setUseRecommendedFilter] = useState(true);
  const [selectedCategoryIds, setSelectedCategoryIds] = useState(() => new Set());
  const [saving, setSaving] = useState(false);
  const [busySourceId, setBusySourceId] = useState(null);

  const loadProviderConfigs = useCallback(async () => {
    const nextConfigs = {
      jackett: readProviderConfig("jackett"),
      prowlarr: readProviderConfig("prowlarr"),
    };
    const nextWarnings = {
      jackett: "",
      prowlarr: "",
    };

    try {
      const providerList = await apiGet("/api/providers");
      const byType = new Map(
        (Array.isArray(providerList) ? providerList : [])
          .map((provider) => [String(provider?.type || "").toLowerCase(), provider])
      );

      for (const provider of PROVIDERS) {
        const row = byType.get(provider.key);
        if (!row) continue;

        const providerId = Number(row?.id);
        const baseUrl = normalizeUrl(row?.baseUrl);
        const configured = !!row?.enabled && !!row?.hasApiKey && !!baseUrl;

        nextConfigs[provider.key] = {
          ...nextConfigs[provider.key],
          providerId: Number.isFinite(providerId) && providerId > 0 ? providerId : null,
          baseUrl,
          configured,
          manualOnly: nextConfigs[provider.key]?.manualOnly || false,
        };

        if (typeof window !== "undefined") {
          const keys = STORAGE_KEYS[provider.key];
          window.localStorage.setItem(keys.baseUrl, baseUrl || "");
          window.localStorage.setItem(keys.configured, configured ? "true" : "false");
          window.localStorage.removeItem(keys.apiKey);
          if (!configured) {
            window.localStorage.setItem(keys.indexers, JSON.stringify([]));
            window.localStorage.setItem(keys.manualOnly, "false");
          }
        }
      }

      await Promise.all(
        PROVIDERS.map(async (provider) => {
          const cfg = nextConfigs[provider.key];
          if (!cfg?.configured || !cfg.providerId) return;

          try {
            const list = await apiGet(`/api/providers/${cfg.providerId}/indexers`);
            const indexers = Array.isArray(list) ? list : [];
            nextConfigs[provider.key] = {
              ...nextConfigs[provider.key],
              indexers,
              manualOnly: indexers.length === 0,
            };

            if (typeof window !== "undefined") {
              window.localStorage.setItem(
                STORAGE_KEYS[provider.key].indexers,
                JSON.stringify(indexers)
              );
              window.localStorage.setItem(
                STORAGE_KEYS[provider.key].manualOnly,
                indexers.length === 0 ? "true" : "false"
              );
            }

            if (indexers.length === 0) {
              nextWarnings[provider.key] =
                `Récupération automatique indisponible pour ${provider.label}. Ajoute les indexeurs manuellement via "Copy Torznab Feed".`;
            }
          } catch {
            const cached = Array.isArray(nextConfigs[provider.key]?.indexers)
              ? nextConfigs[provider.key].indexers
              : [];
            const manualOnly = cached.length === 0 || !!nextConfigs[provider.key]?.manualOnly;
            nextConfigs[provider.key] = {
              ...nextConfigs[provider.key],
              manualOnly,
            };
            if (manualOnly) {
              nextWarnings[provider.key] =
                `Impossible de récupérer la liste auto pour ${provider.label}. Ajoute les indexeurs manuellement via "Copy Torznab Feed".`;
            }
          }
        })
      );
    } catch {
      // Keep cached provider state from storage.
    }

    setProviderConfigs(nextConfigs);
    setProviderIndexerWarnings(nextWarnings);
  }, []);

  const loadSources = useCallback(async () => {
    try {
      const data = await apiGet("/api/sources");
      setSources(Array.isArray(data) ? data : []);
    } catch {
      setSources([]);
    }
  }, []);

  useEffect(() => {
    loadProviderConfigs();
  }, [loadProviderConfigs]);

  useEffect(() => {
    if (!jackettConfig) return;
    loadProviderConfigs();
  }, [jackettConfig, loadProviderConfigs]);

  useEffect(() => {
    loadSources();
  }, [loadSources]);

  const configuredProviders = useMemo(
    () => PROVIDERS.filter((p) => providerConfigs[p.key]?.configured),
    [providerConfigs]
  );
  const hasConfiguredProviders = configuredProviders.length > 0;

  const providerSources = useMemo(() => {
    const result = Object.fromEntries(PROVIDERS.map((p) => [p.key, []]));
    const bases = PROVIDERS.map((p) => ({
      key: p.key,
      base: normalizeUrl(providerConfigs[p.key]?.baseUrl),
    })).filter((row) => row.base);

    sources.forEach((source) => {
      const url = normalizeUrl(source?.torznabUrl);
      const match = bases.find((row) => url.startsWith(row.base));
      if (match && result[match.key]) {
        result[match.key].push(source);
      }
    });

    return result;
  }, [sources, providerConfigs]);

  const totalSources = useMemo(
    () => configuredProviders.reduce((sum, provider) => sum + (providerSources[provider.key]?.length || 0), 0),
    [configuredProviders, providerSources]
  );

  useEffect(() => {
    onHasSourcesChange?.(totalSources > 0);
  }, [totalSources, onHasSourcesChange]);

  const availableIndexersByProvider = useMemo(() => {
    const result = {};
    configuredProviders.forEach((provider) => {
      const existing = new Set(
        (providerSources[provider.key] || []).map((s) => normalizeUrl(s?.torznabUrl))
      );
      const cached = providerConfigs[provider.key]?.indexers || [];
      const list = Array.isArray(cached) ? cached : [];
      result[provider.key] = list.filter(
        (idx) => !existing.has(normalizeUrl(idx?.torznabUrl))
      );
    });
    return result;
  }, [configuredProviders, providerConfigs, providerSources]);

  function resetModalState() {
    setCapsError("");
    setCapsOk("");
    setCapsWarning("");
    setCapsCategories([]);
    setUseRecommendedFilter(true);
    setSelectedCategoryIds(new Set());
    setSelectedIndexer(null);
    setEditingSource(null);
    setManualMode(false);
    setManualName("");
    setManualTorznabUrl("");
  }

  function closeModal() {
    if (saving) return;
    setModalOpen(false);
    setSelectedProviderKey("");
    resetModalState();
  }

  function openAddModal(providerKey, indexer) {
    resetModalState();
    setManualMode(false);
    setSelectedProviderKey(providerKey);
    setSelectedIndexer(indexer);
    setModalOpen(true);
    testCapsForIndexer(providerKey, indexer);
  }

  function openManualModal(providerKey) {
    resetModalState();
    setManualMode(true);
    setSelectedProviderKey(providerKey);
    setModalOpen(true);
  }

  function resolveProviderKey(source) {
    const url = normalizeUrl(source?.torznabUrl);
    const match = PROVIDERS.map((p) => ({
      key: p.key,
      base: normalizeUrl(providerConfigs[p.key]?.baseUrl),
    })).find((row) => row.base && url.startsWith(row.base));
    if (match?.key) return match.key;
    return configuredProviders[0]?.key || "";
  }

  function buildCapsQuery(params) {
    const search = new URLSearchParams();
    Object.entries(params || {}).forEach(([key, value]) => {
      if (value === undefined || value === null) return;
      const raw = String(value).trim();
      if (!raw) return;
      search.set(key, raw);
    });
    const qs = search.toString();
    return qs ? `/api/categories/caps?${qs}` : "/api/categories/caps";
  }

  async function testCapsForIndexer(providerKey, indexer) {
    setCapsError("");
    setCapsOk("");
    setCapsWarning("");
    setCapsCategories([]);
    setSelectedCategoryIds(new Set());

    if (!indexer?.torznabUrl) {
      setCapsError(tr("Indexeur invalide.", "Invalid indexer."));
      return;
    }

    const providerId = Number(providerConfigs[providerKey]?.providerId || 0);
    if (!providerId) {
      setCapsError(tr("Fournisseur non configure cote API.", "Provider not configured in API."));
      return;
    }

    setCapsLoading(true);
    try {
      const res = await apiPost("/api/categories/caps/provider", {
        providerId,
        torznabUrl: indexer.torznabUrl,
        indexerId: indexer?.id,
        indexerName: indexer?.name,
      });

      const cats = Array.isArray(res?.categories) ? res.categories : [];
      const warnings = Array.isArray(res?.warnings) ? res.warnings : [];
      if (warnings.length > 0) {
        setCapsWarning(warnings.join(" "));
      }

      setCapsCategories(cats);

      const recommended = cats.filter((c) => c?.isRecommended);
      const base = recommended.length > 0 ? recommended : cats;
      if (base.length > 0) {
        setSelectedCategoryIds(new Set(base.map((c) => c.id)));
      }

      if (cats.length > 0) {
        setCapsOk(tr("Categories chargees.", "Categories loaded."));
      } else if (warnings.length === 0) {
        setCapsWarning(tr("Aucune categorie disponible.", "No category available."));
      }
    } catch (e) {
      setCapsWarning(e?.message || tr("Caps indisponible. Aucune categorie disponible.", "Caps unavailable. No category available."));
    } finally {
      setCapsLoading(false);
    }
  }

  async function testCapsManual() {
    const providerKey = selectedProviderKey;
    if (!providerKey) {
      setCapsError(tr("Fournisseur non selectionne.", "Provider not selected."));
      return;
    }

    const torznabUrl = normalizeUrl(manualTorznabUrl);
    if (!torznabUrl) {
      setCapsError(tr("URL Torznab requise.", "Torznab URL required."));
      return;
    }

    const providerLabel = getProviderLabel(providerKey);
    await testCapsForIndexer(providerKey, {
      id: "manual",
      name: manualName.trim() || `${providerLabel} manuel`,
      torznabUrl,
    });
  }

  async function addSource() {
    setCapsError("");
    setCapsOk("");
    const providerKey = selectedProviderKey;
    const providerLabel = getProviderLabel(providerKey);
    const indexer = manualMode
      ? {
          id: "manual",
          name: manualName.trim() || `${providerLabel} manuel`,
          torznabUrl: normalizeUrl(manualTorznabUrl),
        }
      : selectedIndexer;

    const selected = allCategories.filter((c) => selectedCategoryIds.has(c.id));
    if (!indexer?.torznabUrl || !normalizeUrl(indexer?.torznabUrl)) {
      setCapsError(manualMode ? tr("URL Torznab requise.", "Torznab URL required.") : tr("Indexeur invalide.", "Invalid indexer."));
      return;
    }
    if (allCategories.length > 0 && selected.length === 0) {
      setCapsError(tr("Selectionne au moins une categorie.", "Select at least one category."));
      return;
    }

    const existing = sources.find(
      (s) => normalizeUrl(s?.torznabUrl) === normalizeUrl(indexer.torznabUrl)
    );
    if (existing) {
      setCapsError("Cet indexeur est déjà ajouté.");
      return;
    }

    const providerId = Number(providerConfigs[providerKey]?.providerId || 0);
    if (!providerId) {
      setCapsError(tr("Fournisseur non configure cote API.", "Provider not configured in API."));
      return;
    }

    setSaving(true);
    try {
      const res = await apiPost("/api/sources", {
        name: indexer.name || providerLabel,
        torznabUrl: normalizeUrl(indexer.torznabUrl),
        authMode: "query",
        providerId,
        categories: selected.length > 0 ? selected.map((c) => ({
          id: c.id,
          name: c.name,
          isSub: c.isSub,
          parentId: c.parentId,
          unifiedKey: c.unifiedKey,
          unifiedLabel: c.unifiedLabel,
        })) : undefined,
      });
      if (res?.id) {
        await apiPut(`/api/sources/${res.id}/enabled`, { enabled: true });
      }
      await loadSources();
      setCapsOk(tr("Indexeur ajoute.", "Indexer added."));
      closeModal();
    } catch (e) {
      setCapsError(e?.message || "Erreur ajout indexeur");
    } finally {
      setSaving(false);
    }
  }

  async function editSource(source) {
    const providerKey = resolveProviderKey(source);
    setSelectedProviderKey(providerKey);
    setEditingSource(source);
    setManualMode(false);
    setManualName("");
    setManualTorznabUrl("");
    setModalOpen(true);
    setCapsError("");
    setCapsOk("");
    setCapsWarning("");
    setCapsCategories([]);
    setSelectedCategoryIds(new Set());
    setCapsLoading(true);
    try {
      const res = await apiGet(buildCapsQuery({
        sourceId: source.id,
        indexerName: source?.name,
      }));

      let cats = Array.isArray(res?.categories) ? res.categories : [];
      let existing = [];

      const warnings = Array.isArray(res?.warnings) ? res.warnings : [];
      if (warnings.length > 0) {
        setCapsWarning(warnings.join(" "));
      }

      setCapsCategories(cats);

      try {
        existing = await apiGet(`/api/categories/${source.id}`);
      } catch {
        existing = [];
      }

      const existingIds = new Set(
        (Array.isArray(existing) ? existing : [])
          .map((row) => Number(row?.id))
          .filter((id) => Number.isFinite(id) && id > 0)
      );

      if (existingIds.size > 0) {
        setSelectedCategoryIds(
          new Set(cats.filter((c) => existingIds.has(c.id)).map((c) => c.id))
        );
      } else {
        const recommended = cats.filter((c) => c?.isRecommended);
        const base = recommended.length > 0 ? recommended : cats;
        setSelectedCategoryIds(new Set(base.map((c) => c.id)));
      }

      if (cats.length > 0) {
        setCapsOk(tr("Categories chargees.", "Categories loaded."));
      } else if (warnings.length === 0) {
        setCapsWarning(tr("Aucune categorie disponible.", "No category available."));
      }
    } catch (e) {
      setCapsError(e?.message || "Erreur chargement categories");
    } finally {
      setCapsLoading(false);
    }
  }

  async function saveCategories() {
    if (!editingSource?.id) return;
    const selected = allCategories.filter((c) => selectedCategoryIds.has(c.id));
    if (selected.length === 0) {
      setCapsError(tr("Selectionne au moins une categorie.", "Select at least one category."));
      return;
    }
    setSaving(true);
    try {
      await apiPut(`/api/sources/${editingSource.id}/categories`, {
        categories: selected.map((c) => ({
          id: c.id,
          name: c.name,
          isSub: c.isSub,
          parentId: c.parentId,
          unifiedKey: c.unifiedKey,
          unifiedLabel: c.unifiedLabel,
        })),
      });
      await loadSources();
      setCapsOk(tr("Categories mises a jour.", "Categories updated."));
      closeModal();
    } catch (e) {
      setCapsError(e?.message || "Erreur sauvegarde categories");
    } finally {
      setSaving(false);
    }
  }

  async function deleteSource(source) {
    if (!source?.id) return;
    setBusySourceId(source.id);
    try {
      await apiDelete(`/api/sources/${source.id}`);
      await loadSources();
    } catch {}
    setBusySourceId(null);
  }

  const allCategories = useMemo(() => capsCategories || [], [capsCategories]);
  const recommendedCategories = useMemo(
    () => (capsCategories || []).filter((c) => c?.isRecommended),
    [capsCategories]
  );
  const visibleCategories = useMemo(
    () => (useRecommendedFilter ? recommendedCategories : allCategories),
    [useRecommendedFilter, recommendedCategories, allCategories]
  );
  const visibleIds = useMemo(
    () => new Set(visibleCategories.map((c) => c.id)),
    [visibleCategories]
  );
  const selectedCount = selectedCategoryIds.size;
  const hiddenSelectedCount = useMemo(() => {
    let hidden = 0;
    selectedCategoryIds.forEach((id) => {
      if (!visibleIds.has(id)) hidden++;
    });
    return hidden;
  }, [selectedCategoryIds, visibleIds]);
  const canSubmitAdd = useMemo(() => {
    if (manualMode && !normalizeUrl(manualTorznabUrl)) return false;
    return allCategories.length === 0 ? true : selectedCount > 0;
  }, [manualMode, manualTorznabUrl, allCategories.length, selectedCount]);
  const canSubmitEdit = selectedCount > 0;

  useEffect(() => {
    if (!useRecommendedFilter) return;
    if (allCategories.length > 0 && recommendedCategories.length === 0) {
      setUseRecommendedFilter(false);
      setCapsWarning((prev) =>
        prev || tr("Aucune categorie recommandee. Affichage complet active.", "No recommended category. Full list enabled.")
      );
    }
  }, [useRecommendedFilter, recommendedCategories.length, allCategories.length]);

  const hasSelectedProvider = !!selectedProviderKey;
  const modalProviderLabel = hasSelectedProvider ? getProviderLabel(selectedProviderKey) : "";
  const modalTitle = editingSource
    ? `Modifier : ${editingSource.name}${hasSelectedProvider ? ` (${modalProviderLabel})` : ""}`
    : manualMode
      ? `Ajouter manuellement${hasSelectedProvider ? ` (${modalProviderLabel})` : ""}`
      : `Ajouter : ${selectedIndexer?.name || "Indexeur"}${hasSelectedProvider ? ` (${modalProviderLabel})` : ""}`;

  return (
    <div className="setup-step setup-jackett">
      <h2>{tr("Indexeurs", "Indexers")}</h2>

      {!hasConfiguredProviders && (
        <div className="setup-jackett__guard">
          <div className="onboarding__error">
            {tr("Aucun fournisseur configure, reviens a l'etape Fournisseurs.", "No configured provider. Go back to Providers step.")}
          </div>
          <button className="btn btn-accent" type="button" onClick={onBack} disabled={!onBack}>
            {tr("Retour etape 3", "Back to step 3")}
          </button>
        </div>
      )}

      {hasConfiguredProviders && (
        <div className="setup-jackett__columns">
          {configuredProviders.map((provider) => {
            const availableIndexers = availableIndexersByProvider[provider.key] || [];
            const providerList = providerSources[provider.key] || [];
            return (
              <div className="setup-jackett__column" key={provider.key}>
                <div className="setup-jackett__provider-title">{provider.label}</div>

                <div className="setup-providers__add settings-row settings-row--ui-select">
                  <label>{tr("Ajouter un indexeur", "Add an indexer")}</label>
                  <select
                    className="settings-field"
                    value={addIds[provider.key] || ""}
                    onChange={(e) => {
                      const id = e.target.value;
                      setAddIds((prev) => ({ ...prev, [provider.key]: id }));
                      if (id === MANUAL_INDEXER_VALUE) {
                        openManualModal(provider.key);
                        setAddIds((prev) => ({ ...prev, [provider.key]: "" }));
                        return;
                      }
                      const idx = availableIndexers.find((i) => String(i.id) === String(id));
                      if (idx) openAddModal(provider.key, idx);
                      setAddIds((prev) => ({ ...prev, [provider.key]: "" }));
                    }}
                  >
                    <option value="" disabled>
                      {tr("Selectionner...", "Select...")}
                    </option>
                    {availableIndexers.map((idx) => (
                      <option key={idx.id} value={idx.id}>
                        {idx.name}
                      </option>
                    ))}
                    <option value={MANUAL_INDEXER_VALUE}>
                      {tr("Ajouter manuellement...", "Add manually...")}
                    </option>
                  </select>
                  {availableIndexers.length === 0 && providerConfigs[provider.key]?.indexers?.length > 0 && (
                    <div className="muted">{tr("Tous les indexeurs detectes sont deja ajoutes.", "All detected indexers are already added.")}</div>
                  )}
                  {availableIndexers.length === 0 && providerConfigs[provider.key]?.indexers?.length === 0 && (
                    <div className="onboarding__warn">
                      {providerIndexerWarnings[provider.key] ||
                        tr(`Aucun indexeur detecte automatiquement pour ${provider.label}.`, `No indexer auto-detected for ${provider.label}.`)}
                    </div>
                  )}
                  {providerConfigs[provider.key]?.manualOnly && (
                    <div className="muted">
                      {tr(
                        `Dans ${provider.label}, utilise "Copy Torznab Feed" puis colle l'URL ici. La cle API du fournisseur configuree a l'etape 3 sera utilisee.`,
                        `In ${provider.label}, use "Copy Torznab Feed" and paste the URL here. The API key configured at step 3 will be used.`
                      )}
                    </div>
                  )}
                </div>

                <div className="setup-jackett__list">
                  <h4>{tr("Indexeurs ajoutes", "Added indexers")}</h4>
                  {providerList.length === 0 ? (
                    <div className="muted">{tr("Aucun indexeur ajoute.", "No indexer added.")}</div>
                  ) : (
                    <div className="indexer-list">
                      {providerList.map((src, idx) => (
                        <ItemRow
                          key={src.id}
                          id={idx + 1}
                          title={src.name}
                          meta={src.torznabUrl}
                          enabled={!!src.enabled}
                          actions={[
                            {
                              icon: "edit",
                              title: tr("Modifier", "Edit"),
                              onClick: () => editSource(src),
                              disabled: busySourceId === src.id,
                            },
                            {
                              icon: "delete",
                              title: tr("Supprimer", "Delete"),
                              onClick: () => deleteSource(src),
                              disabled: busySourceId === src.id,
                              className: "iconbtn--danger",
                            },
                          ]}
                          showToggle={false}
                        />
                      ))}
                    </div>
                  )}
                </div>
              </div>
            );
          })}
        </div>
      )}

      <Modal
        open={modalOpen}
        title={modalTitle}
        onClose={closeModal}
        width={720}
      >
        {manualMode && !editingSource && (
          <div className="formgrid formgrid--edit" style={{ marginBottom: 12 }}>
            <div className="field">
              <label>{tr("Nom (optionnel)", "Name (optional)")}</label>
              <input
                value={manualName}
                onChange={(e) => setManualName(e.target.value)}
                placeholder={`Nom ${modalProviderLabel || "indexeur"}`}
                disabled={saving || capsLoading}
              />
            </div>
            <div className="field">
              <label>{tr("Torznab Feed URL", "Torznab Feed URL")}</label>
              <input
                value={manualTorznabUrl}
                onChange={(e) => {
                  setManualTorznabUrl(e.target.value);
                  setCapsError("");
                  setCapsOk("");
                  setCapsWarning("");
                  setCapsCategories([]);
                  setSelectedCategoryIds(new Set());
                }}
                placeholder="Colle l'URL Copy Torznab Feed"
                disabled={saving || capsLoading}
              />
              <span className="field-hint">
                {tr(
                  `Depuis ${modalProviderLabel || "le fournisseur"}, clique "Copy Torznab Feed", puis colle l'URL complete.`,
                  `From ${modalProviderLabel || "the provider"}, click "Copy Torznab Feed" and paste the full URL.`
                )}
              </span>
            </div>
            <div className="setup-jackett__actions" style={{ gridColumn: "1 / -1" }}>
              <button
                className="btn"
                type="button"
                onClick={testCapsManual}
                disabled={saving || capsLoading || !normalizeUrl(manualTorznabUrl)}
              >
                {capsLoading ? tr("Test...", "Test...") : tr("Tester les categories (caps)", "Test categories (caps)")}
              </button>
            </div>
          </div>
        )}

        {capsError && <div className="onboarding__error">{capsError}</div>}
        {capsWarning && <div className="onboarding__warn">{capsWarning}</div>}
        {capsOk && <div className="onboarding__ok">{capsOk}</div>}
        {capsLoading && <div className="muted">{tr("Chargement des categories...", "Loading categories...")}</div>}

        {allCategories.length > 0 && (
          <div className="setup-jackett__categories">
            <div style={{ display: "flex", alignItems: "center", gap: 10, marginBottom: 8 }}>
              <span className="muted">{tr("Afficher seulement les categories recommandees", "Show only recommended categories")}</span>
              <ToggleSwitch
                checked={useRecommendedFilter}
                onIonChange={(e) => setUseRecommendedFilter(e.detail.checked)}
                className="settings-toggle settings-toggle--sm"
              />
            </div>
            <div className="category-picker" style={{ maxHeight: 260 }}>
              {visibleCategories.map((cat) => (
                <label key={cat.id} className="category-row">
                  <input
                    type="checkbox"
                    checked={selectedCategoryIds.has(cat.id)}
                    onChange={(e) => {
                      const checked = e.target.checked;
                      setSelectedCategoryIds((prev) => {
                        const next = new Set(prev);
                        if (checked) next.add(cat.id);
                        else next.delete(cat.id);
                        return next;
                      });
                    }}
                  />
                  <span className="category-id">{cat.id}</span>
                  <span className="category-name">{cat.name}</span>
                  <span className="category-pill">{cat.unifiedLabel}</span>
                </label>
              ))}
              {visibleCategories.length === 0 && useRecommendedFilter && (
                <div className="muted">{tr("Aucune categorie recommandee. Desactive le filtre pour tout voir.", "No recommended category. Disable the filter to show all.")}</div>
              )}
            </div>
            <div className="muted" style={{ marginTop: 8 }}>
              {tr(
                `${selectedCount} categorie${selectedCount > 1 ? "s" : ""} selectionnee${selectedCount > 1 ? "s" : ""}`,
                `${selectedCount} selected categor${selectedCount > 1 ? "ies" : "y"}`
              )}
              {useRecommendedFilter && hiddenSelectedCount > 0
                ? tr(` (dont ${hiddenSelectedCount} hors filtre)`, ` (${hiddenSelectedCount} hidden by filter)`)
                : ""}
            </div>
          </div>
        )}

        <div className="setup-jackett__actions setup-jackett__footer">
          {editingSource ? (
            <button
              className="btn btn-accent"
              type="button"
              onClick={saveCategories}
              disabled={saving || !canSubmitEdit}
            >
              {saving ? tr("Enregistrement...", "Saving...") : tr("Mettre a jour", "Update")}
            </button>
          ) : (
            <button
              className="btn btn-accent"
              type="button"
              onClick={addSource}
              disabled={saving || !canSubmitAdd}
            >
              {saving
                ? tr("Enregistrement...", "Saving...")
                : manualMode
                  ? tr("Ajouter manuellement", "Add manually")
                  : tr("Ajouter l'indexeur", "Add indexer")}
            </button>
          )}
        </div>
      </Modal>
    </div>
  );
}
