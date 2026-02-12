import React, { useCallback, useEffect, useMemo, useState } from "react";
import { IonToggle, setupIonicReact } from "@ionic/react";
import "@ionic/react/css/core.css";
import ItemRow from "../../ui/ItemRow.jsx";
import Modal from "../../ui/Modal.jsx";
import { apiDelete, apiGet, apiPost, apiPut } from "../../api/client.js";

setupIonicReact({ mode: "md" });

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
const PROVIDERS = [
  { key: "jackett", label: "Jackett" },
  { key: "prowlarr", label: "Prowlarr" },
];
const EMPTY_CONFIG = { baseUrl: "", apiKey: "", indexers: [], configured: false };

function normalizeUrl(value) {
  return String(value || "").trim().replace(/\/+$/, "");
}

function readProviderConfig(providerKey) {
  const keys = STORAGE_KEYS[providerKey];
  if (!keys || typeof window === "undefined") return { ...EMPTY_CONFIG };
  const baseUrl = window.localStorage.getItem(keys.baseUrl) || "";
  const apiKey = window.localStorage.getItem(keys.apiKey) || "";
  const configured = window.localStorage.getItem(keys.configured) === "true";
  const cached = window.localStorage.getItem(keys.indexers) || "";
  const list = cached ? (JSON.parse(cached) || []) : [];
  return {
    baseUrl,
    apiKey,
    configured: configured && !!baseUrl && !!apiKey,
    indexers: Array.isArray(list) ? list : [],
  };
}

function getProviderLabel(providerKey) {
  return PROVIDERS.find((p) => p.key === providerKey)?.label || "Fournisseur";
}

export default function Step31JackettIndexers({ onHasSourcesChange, onBack, jackettConfig }) {
  const [providerConfigs, setProviderConfigs] = useState({
    jackett: { ...EMPTY_CONFIG },
    prowlarr: { ...EMPTY_CONFIG },
  });
  const [sources, setSources] = useState([]);
  const [addIds, setAddIds] = useState({ jackett: "", prowlarr: "" });
  const [modalOpen, setModalOpen] = useState(false);
  const [selectedIndexer, setSelectedIndexer] = useState(null);
  const [selectedProviderKey, setSelectedProviderKey] = useState("");
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

  const loadProviderConfigs = useCallback(() => {
    setProviderConfigs({
      jackett: readProviderConfig("jackett"),
      prowlarr: readProviderConfig("prowlarr"),
    });
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
  }

  function closeModal() {
    if (saving) return;
    setModalOpen(false);
    setSelectedProviderKey("");
    resetModalState();
  }

  function openAddModal(providerKey, indexer) {
    resetModalState();
    setSelectedProviderKey(providerKey);
    setSelectedIndexer(indexer);
    setModalOpen(true);
    testCapsForIndexer(providerKey, indexer);
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
      setCapsError("Indexeur invalide.");
      return;
    }

    const providerLabel = getProviderLabel(providerKey);
    const apiKey = providerConfigs[providerKey]?.apiKey || "";
    if (!apiKey) {
      setCapsError(`Clé API ${providerLabel} manquante.`);
      return;
    }

    setCapsLoading(true);
    try {
      const res = await apiPost("/api/categories/caps", {
        torznabUrl: indexer.torznabUrl,
        apiKey,
        authMode: "query",
        indexerName: indexer?.name,
        indexerId: indexer?.id,
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
        setCapsOk("Catégories chargées.");
      } else if (warnings.length === 0) {
        setCapsWarning("Aucune catégorie disponible.");
      }
    } catch (e) {
      setCapsWarning(e?.message || "Caps indisponible. Aucune catégorie disponible.");
    } finally {
      setCapsLoading(false);
    }
  }

  async function addSource() {
    setCapsError("");
    setCapsOk("");
    const indexer = selectedIndexer;
    const selected = allCategories.filter((c) => selectedCategoryIds.has(c.id));
    if (!indexer?.torznabUrl) {
      setCapsError("Indexeur invalide.");
      return;
    }
    if (allCategories.length > 0 && selected.length === 0) {
      setCapsError("Sélectionne au moins une catégorie.");
      return;
    }

    const existing = sources.find(
      (s) => normalizeUrl(s?.torznabUrl) === normalizeUrl(indexer.torznabUrl)
    );
    if (existing) {
      setCapsError("Cet indexeur est déjà ajouté.");
      return;
    }

    const providerKey = selectedProviderKey;
    const providerLabel = getProviderLabel(providerKey);
    const apiKey = providerConfigs[providerKey]?.apiKey || "";
    if (!apiKey) {
      setCapsError(`Clé API ${providerLabel} manquante.`);
      return;
    }

    setSaving(true);
    try {
      const res = await apiPost("/api/sources", {
        name: indexer.name || providerLabel,
        torznabUrl: indexer.torznabUrl,
        authMode: "query",
        apiKey,
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
      setCapsOk("Indexeur ajouté.");
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
        setCapsOk("Catégories chargées.");
      } else if (warnings.length === 0) {
        setCapsWarning("Aucune catégorie disponible.");
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
      setCapsError("Sélectionne au moins une catégorie.");
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
      setCapsOk("Catégories mises à jour.");
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
  const canSubmitAdd = allCategories.length === 0 ? true : selectedCount > 0;
  const canSubmitEdit = selectedCount > 0;

  useEffect(() => {
    if (!useRecommendedFilter) return;
    if (allCategories.length > 0 && recommendedCategories.length === 0) {
      setUseRecommendedFilter(false);
      setCapsWarning((prev) =>
        prev || "Aucune catégorie recommandée. Affichage complet activé."
      );
    }
  }, [useRecommendedFilter, recommendedCategories.length, allCategories.length]);

  const hasSelectedProvider = !!selectedProviderKey;
  const modalProviderLabel = hasSelectedProvider ? getProviderLabel(selectedProviderKey) : "";
  const modalTitle = editingSource
    ? `Modifier : ${editingSource.name}${hasSelectedProvider ? ` (${modalProviderLabel})` : ""}`
    : `Ajouter : ${selectedIndexer?.name || "Indexeur"}${hasSelectedProvider ? ` (${modalProviderLabel})` : ""}`;

  return (
    <div className="setup-step setup-jackett">
      <h2>Indexeurs</h2>

      {!hasConfiguredProviders && (
        <div className="setup-jackett__guard">
          <div className="onboarding__error">
            Aucun fournisseur configuré, reviens à l’étape Fournisseurs.
          </div>
          <button className="btn btn-accent" type="button" onClick={onBack} disabled={!onBack}>
            Retour étape 3
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
                  <label>Ajouter un indexeur</label>
                  <select
                    className="settings-field"
                    value={addIds[provider.key] || ""}
                    onChange={(e) => {
                      const id = e.target.value;
                      setAddIds((prev) => ({ ...prev, [provider.key]: id }));
                      const idx = availableIndexers.find((i) => String(i.id) === String(id));
                      if (idx) openAddModal(provider.key, idx);
                      setAddIds((prev) => ({ ...prev, [provider.key]: "" }));
                    }}
                  >
                    <option value="" disabled>
                      Sélectionner...
                    </option>
                    {availableIndexers.map((idx) => (
                      <option key={idx.id} value={idx.id}>
                        {idx.name}
                      </option>
                    ))}
                  </select>
                  {availableIndexers.length === 0 && (
                    <div className="muted">Aucun indexeur disponible.</div>
                  )}
                </div>

                <div className="setup-jackett__list">
                  <h4>Indexeurs ajoutés</h4>
                  {providerList.length === 0 ? (
                    <div className="muted">Aucun indexeur ajouté.</div>
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
                              title: "Modifier",
                              onClick: () => editSource(src),
                              disabled: busySourceId === src.id,
                            },
                            {
                              icon: "delete",
                              title: "Supprimer",
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
        {capsError && <div className="onboarding__error">{capsError}</div>}
        {capsWarning && <div className="onboarding__error">{capsWarning}</div>}
        {capsOk && <div className="onboarding__ok">{capsOk}</div>}
        {capsLoading && <div className="muted">Chargement des catégories...</div>}

        {allCategories.length > 0 && (
          <div className="setup-jackett__categories">
            <div style={{ display: "flex", alignItems: "center", gap: 10, marginBottom: 8 }}>
              <span className="muted">Afficher seulement les catégories recommandées</span>
              <IonToggle
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
                <div className="muted">Aucune catégorie recommandée. Désactive le filtre pour tout voir.</div>
              )}
            </div>
            <div className="muted" style={{ marginTop: 8 }}>
              {selectedCount} catégorie{selectedCount > 1 ? "s" : ""} sélectionnée{selectedCount > 1 ? "s" : ""}
              {useRecommendedFilter && hiddenSelectedCount > 0 ? ` (dont ${hiddenSelectedCount} hors filtre)` : ""}
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
              {saving ? "Enregistrement..." : "Mettre à jour"}
            </button>
          ) : (
            <button
              className="btn btn-accent"
              type="button"
              onClick={addSource}
              disabled={saving || !canSubmitAdd}
            >
              {saving ? "Enregistrement..." : "Ajouter l'indexeur"}
            </button>
          )}
        </div>
      </Modal>
    </div>
  );
}
