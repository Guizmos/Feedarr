import React, { useCallback, useEffect, useMemo, useState } from "react";
import ItemRow from "../../ui/ItemRow.jsx";
import Modal from "../../ui/Modal.jsx";
import { apiDelete, apiGet, apiPatch, apiPost, apiPut } from "../../api/client.js";
import { tr } from "../../app/uiText.js";
import CategoryMappingBoard from "../shared/CategoryMappingBoard.jsx";
import {
  CATEGORY_GROUP_LABELS,
  buildCategoryMappingsPatchDto,
  buildMappingsPayload,
  mapFromCapsAssignments,
  normalizeCategoryGroupKey,
} from "../../domain/categories/index.js";

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
  const [categoryMappings, setCategoryMappings] = useState(() => new Map());
  const [saving, setSaving] = useState(false);
  const [reclassifying, setReclassifying] = useState(false);
  const [busySourceId, setBusySourceId] = useState(null);
  const [loadErr, setLoadErr] = useState("");

  const loadProviderConfigs = useCallback(async () => {
    setLoadErr("");
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
          } catch (e) {
            console.error(`[Step3 Indexers] Chargement indexeurs ${provider.label} échoué.`, e);
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
    } catch (e) {
      console.error("[Step3 Indexers] Impossible de charger les fournisseurs depuis l'API.", e);
      setLoadErr("Impossible de charger les fournisseurs. La configuration locale est utilisée.");
    }

    setProviderConfigs(nextConfigs);
    setProviderIndexerWarnings(nextWarnings);
  }, []);

  const loadSources = useCallback(async () => {
    try {
      const data = await apiGet("/api/sources");
      setSources(Array.isArray(data) ? data : []);
    } catch (e) {
      console.error("[Step3 Indexers] Impossible de charger les sources.", e);
      setSources([]);
      setLoadErr("Erreur lors du chargement des sources.");
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
    setCategoryMappings(new Map());
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
    setCategoryMappings(new Map());

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
        includeStandardCatalog: true,
        includeSpecific: true,
      });

      const cats = Array.isArray(res?.categories) ? res.categories : [];
      const warnings = Array.isArray(res?.warnings) ? res.warnings : [];
      if (warnings.length > 0) {
      setCapsWarning(warnings.join(" "));
      }

      setCapsCategories(cats);
      setCategoryMappings(mapFromCapsAssignments(cats));

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

    if (!indexer?.torznabUrl || !normalizeUrl(indexer?.torznabUrl)) {
      setCapsError(manualMode ? tr("URL Torznab requise.", "Torznab URL required.") : tr("Indexeur invalide.", "Invalid indexer."));
      return;
    }

    const existing = sources.find(
      (s) => normalizeUrl(s?.torznabUrl) === normalizeUrl(indexer.torznabUrl)
    );
    if (existing) {
      setCapsError(tr("Cet indexeur est déjà ajouté.", "This indexer is already added."));
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
      });
      if (res?.id) {
        await apiPut(`/api/sources/${res.id}/enabled`, { enabled: true });
        const mappingsPayload = buildMappingsPayload(categoryMappings);
        const selectedCategoryIds = [...categoryMappings.keys()]
          .map((catId) => Number(catId))
          .filter((catId) => Number.isFinite(catId) && catId > 0)
          .sort((a, b) => a - b);
        await apiPatch(
          `/api/sources/${res.id}/category-mappings`,
          buildCategoryMappingsPatchDto({
            mappings: mappingsPayload,
            selectedCategoryIds,
          })
        );
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
    setCategoryMappings(new Map());
    setCapsLoading(true);
    try {
      const res = await apiGet(buildCapsQuery({
        sourceId: source.id,
        indexerName: source?.name,
        includeStandardCatalog: true,
        includeSpecific: true,
      }));

      let cats = Array.isArray(res?.categories) ? res.categories : [];
      const warnings = Array.isArray(res?.warnings) ? res.warnings : [];
      if (warnings.length > 0) {
        setCapsWarning(warnings.join(" "));
      }

      setCapsCategories(cats);
      setCategoryMappings(mapFromCapsAssignments(cats));

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
    const mappingsPayload = (capsCategories || [])
      .map((category) => {
        const catId = Number(category?.id);
        if (!Number.isFinite(catId) || catId <= 0) return null;
        const unifiedKey = normalizeCategoryGroupKey(categoryMappings.get(catId)) || null;
        const unifiedLabel = unifiedKey ? CATEGORY_GROUP_LABELS[unifiedKey] || unifiedKey : null;
        return {
          categoryId: catId,
          unifiedKey,
          unifiedLabel,
          catId,
          groupKey: unifiedKey,
          groupLabel: unifiedLabel,
        };
      })
      .filter(Boolean);
    const selectedCategoryIds = [...categoryMappings.keys()]
      .map((catId) => Number(catId))
      .filter((catId) => Number.isFinite(catId) && catId > 0)
      .sort((a, b) => a - b);
    if (mappingsPayload.length === 0) {
      setCapsError(tr("Aucune categorie a mettre a jour.", "No category to update."));
      return;
    }
    setSaving(true);
    try {
      await apiPatch(
        `/api/sources/${editingSource.id}/category-mappings`,
        buildCategoryMappingsPatchDto({
          mappings: mappingsPayload,
          selectedCategoryIds,
        })
      );
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
    } catch (e) {
      console.error("[Step3 Indexers] Impossible de supprimer la source.", e);
      setLoadErr("Impossible de supprimer la source.");
    }
    setBusySourceId(null);
  }

  async function reclassifyExisting() {
    if (!editingSource?.id || reclassifying) return;
    const confirmed =
      typeof window === "undefined"
        ? true
        : window.confirm("Reclasser l'existant pour cette source ?");
    if (!confirmed) return;

    setCapsError("");
    setReclassifying(true);
    try {
      await apiPost(`/api/sources/${editingSource.id}/reclassify`);
      setCapsOk(tr("Reclassement termine.", "Reclassification done."));
      await loadSources();
    } catch (e) {
      setCapsError(e?.message || tr("Erreur reclassification", "Reclassification error"));
    } finally {
      setReclassifying(false);
    }
  }

  const allCategories = useMemo(() => capsCategories || [], [capsCategories]);
  const selectedCount = categoryMappings.size;
  const canSubmitAdd = useMemo(() => {
    if (manualMode && !normalizeUrl(manualTorznabUrl)) return false;
    return true;
  }, [manualMode, manualTorznabUrl]);
  const canSubmitEdit = true;

  const hasSelectedProvider = !!selectedProviderKey;
  const modalProviderLabel = hasSelectedProvider ? getProviderLabel(selectedProviderKey) : "";
  const modalTitle = editingSource
    ? `${tr("Modifier", "Edit")} : ${editingSource.name}${hasSelectedProvider ? ` (${modalProviderLabel})` : ""}`
    : manualMode
      ? `${tr("Ajouter manuellement", "Add manually")}${hasSelectedProvider ? ` (${modalProviderLabel})` : ""}`
      : `${tr("Ajouter", "Add")} : ${selectedIndexer?.name || tr("Indexeur", "Indexer")}${hasSelectedProvider ? ` (${modalProviderLabel})` : ""}`;

  return (
    <div className="setup-step setup-jackett">
      <h2>{tr("Indexeurs", "Indexers")}</h2>

      {loadErr && <div className="onboarding__error">{loadErr}</div>}

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
        width={840}
      >
        {manualMode && !editingSource && (
          <div className="formgrid formgrid--edit" style={{ marginBottom: 12 }}>
            <div className="field">
              <label>{tr("Nom (optionnel)", "Name (optional)")}</label>
              <input
                value={manualName}
                onChange={(e) => setManualName(e.target.value)}
                placeholder={tr(`Nom ${modalProviderLabel || "indexeur"}`, `Name ${modalProviderLabel || "indexer"}`)}
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
                    setCategoryMappings(new Map());
                  }}
                placeholder={tr("Colle l'URL Copy Torznab Feed", "Paste the Copy Torznab Feed URL")}
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
            <CategoryMappingBoard
              variant="wizard"
              categories={capsCategories}
              mappings={categoryMappings}
              sourceId={editingSource?.id}
              onChangeMapping={(catId, groupKey) => {
                const normalized = normalizeCategoryGroupKey(groupKey);
                setCategoryMappings((prev) => {
                  const next = new Map(prev);
                  if (!normalized) next.delete(catId);
                  else next.set(catId, normalized);
                  return next;
                });
              }}
            />
            <div className="muted" style={{ marginTop: 8 }}>
              {tr(
                `${selectedCount} categorie${selectedCount > 1 ? "s" : ""} selectionnee${selectedCount > 1 ? "s" : ""}`,
                `${selectedCount} selected categor${selectedCount > 1 ? "ies" : "y"}`
              )}
            </div>
          </div>
        )}

        <div className="setup-jackett__actions setup-jackett__footer">
          <div className="setup-jackett__footer-note" role="note">
            <span className="setup-jackett__footer-note-icon" aria-hidden="true">i</span>
            <span>
              {tr(
                "Choisissez de preference les categories specifiques, plus pertinentes. Les categories \"parents\" incluent souvent des sous-categories qui peuvent provoquer un mauvais tri.",
                "Prefer specific categories when possible. Parent categories often include subcategories that can cause incorrect sorting."
              )}
            </span>
          </div>
          {editingSource ? (
            <div style={{ display: "flex", gap: 8 }}>
              <button
                className="btn"
                type="button"
                onClick={reclassifyExisting}
                disabled={saving || reclassifying}
              >
                {reclassifying ? tr("Reclassement...", "Reclassifying...") : tr("Reclasser l'existant", "Reclassify existing")}
              </button>
              <button
                className="btn btn-accent"
                type="button"
                onClick={saveCategories}
                disabled={saving || !canSubmitEdit}
              >
                {saving ? tr("Enregistrement...", "Saving...") : tr("Mettre a jour", "Update")}
              </button>
            </div>
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
                  : tr("Ajouter l'indexeur", "Add Indexer")}
            </button>
          )}
        </div>
      </Modal>
    </div>
  );
}
