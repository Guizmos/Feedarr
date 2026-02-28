import React, { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { apiDelete, apiGet, apiPatch, apiPost, apiPut } from "../api/client.js";
import Loader from "../ui/Loader.jsx";
import { useSubbarSetter } from "../layout/useSubbar.js";
import SubAction from "../ui/SubAction.jsx";
import Modal from "../ui/Modal.jsx";
import AppIcon from "../ui/AppIcon.jsx";
import { startNewIndexerTest, completeNewIndexerTest } from "../tasks/indexerTasks.js";
import { startCapsRefresh, completeCapsRefresh } from "../tasks/categoriesTasks.js";
import ItemRow, { CategoryBubble } from "../ui/ItemRow.jsx";
import IndexersSyncSettingsCard from "../components/indexers/IndexersSyncSettingsCard.jsx";
import useIndexerModal from "./indexers/hooks/useIndexerModal.js";
import useIndexerSync from "./indexers/hooks/useIndexerSync.js";
import { tr } from "../app/uiText.js";
import {
  SOURCE_COLOR_PALETTE,
  getSourceColor,
  normalizeHexColor,
} from "../utils/sourceColors.js";
import CategoryMappingBoard from "../components/shared/CategoryMappingBoard.jsx";
import {
  CATEGORY_GROUP_LABELS,
  buildCategoryMappingsPatchDto,
  MAPPING_GROUP_PRIORITY,
  buildMappingDiff,
  buildMappingsPayload,
  dedupeBubblesByUnifiedKey,
  dedupeCategoriesById,
  mapFromCapsAssignments,
  normalizeCategoryGroupKey,
} from "../domain/categories/index.js";

const MANUAL_INDEXER_VALUE = "__manual__";

/** Normalise une URL pour comparaison : lowercase + suppression du slash final. */
function normalizeUrl(url) {
  return (url || "").trim().toLowerCase().replace(/\/+$/, "");
}

function areMappingMapsEqual(a, b) {
  if (a === b) return true;
  if (!(a instanceof Map) || !(b instanceof Map)) return false;
  if (a.size !== b.size) return false;
  for (const [key, value] of a.entries()) {
    if (b.get(key) !== value) return false;
  }
  return true;
}

export default function Indexers() {
  const setContent = useSubbarSetter();

  const [loading, setLoading] = useState(true);
  const [err, setErr] = useState("");
  const [items, setItems] = useState([]);
  const [saving, setSaving] = useState(false);
  const [capsLoading, setCapsLoading] = useState(false);
  const [capsError, setCapsError] = useState("");
  const [capsWarning, setCapsWarning] = useState("");
  const [capsCategories, setCapsCategories] = useState([]);
  const [categoryMappings, setCategoryMappings] = useState(() => new Map());
  const [providerOptions, setProviderOptions] = useState([]);
  const [providerOptionsLoading, setProviderOptionsLoading] = useState(false);
  const [providerOptionsWarning, setProviderOptionsWarning] = useState("");
  const [selectedProviderId, setSelectedProviderId] = useState("");
  const [indexerOptions, setIndexerOptions] = useState([]);
  const [indexerOptionsLoading, setIndexerOptionsLoading] = useState(false);
  const [indexerOptionsWarning, setIndexerOptionsWarning] = useState("");
  const [indexerOptionsError, setIndexerOptionsError] = useState("");
  const [selectedIndexerId, setSelectedIndexerId] = useState("");
  const indexerRequestRef = useRef(0);
  const [color, setColor] = useState(SOURCE_COLOR_PALETTE[0]);
  const [colorOpen, setColorOpen] = useState(false);
  const colorPickerRef = useRef(null);
  const [showAdvanced, setShowAdvanced] = useState(false);
  const [testPassed, setTestPassed] = useState(false);
  const [testSourceId, setTestSourceId] = useState(null); // ID of source created during test
  const [syncModalOpen, setSyncModalOpen] = useState(false);
  const initialEditRef = useRef(null);
  const initialCategoryMappingsRef = useRef(null);
  const [reclassifying, setReclassifying] = useState(false);

  // form fields
  const [name, setName] = useState("");
  const [torznabUrl, setTorznabUrl] = useState("");
  const [apiKey, setApiKey] = useState("");
  const [enabled, setEnabled] = useState(true);

  const hasEnabledIndexers = useMemo(
    () => (items || []).some((s) => !!s?.enabled),
    [items]
  );

  const [categoriesById, setCategoriesById] = useState({});

  const load = useCallback(async () => {
    setErr("");
    setLoading(true);
    try {
      const data = await apiGet("/api/sources");
      const sources = Array.isArray(data) ? data : [];
      setItems(sources);

      // Charger les catégories pour chaque source
      const catsMap = {};
      await Promise.all(
        sources.map(async (s) => {
          if (!s?.id) return;
          try {
            const mappings = await apiGet(`/api/sources/${s.id}/category-mappings`);
            catsMap[s.id] = (Array.isArray(mappings) ? mappings : []).map((mapping) => ({
              id: Number(mapping?.catId),
              unifiedKey: normalizeCategoryGroupKey(mapping?.groupKey || mapping?.unifiedKey),
              unifiedLabel:
                mapping?.groupLabel ||
                mapping?.unifiedLabel ||
                CATEGORY_GROUP_LABELS[normalizeCategoryGroupKey(mapping?.groupKey || mapping?.unifiedKey)] ||
                "",
              name:
                mapping?.groupLabel ||
                mapping?.unifiedLabel ||
                CATEGORY_GROUP_LABELS[normalizeCategoryGroupKey(mapping?.groupKey || mapping?.unifiedKey)] ||
                "",
            }));
          } catch (e) {
            console.error(`[Indexers] Impossible de charger les catégories pour la source #${s.id}.`, e);
            catsMap[s.id] = [];
          }
        })
      );
      setCategoriesById(catsMap);
    } catch (e) {
      setErr(e?.message || "Erreur chargement sources");
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    load();
  }, [load]);

  const {
    syncingId,
    syncStatusById,
    syncAllRunning,
    testingId,
    testStatusById,
    hasPendingSync,
    syncAll,
    syncSource,
    testSource,
  } = useIndexerSync({ items, load, setErr });

  const loadProviders = useCallback(async () => {
    setProviderOptions([]);
    setProviderOptionsWarning("");
    setProviderOptionsLoading(true);
    try {
      const res = await apiGet("/api/providers");
      const list = Array.isArray(res) ? res : [];
      const enabled = list.filter((p) => !!p?.enabled);
      setProviderOptions(enabled);
      if (enabled.length === 0) {
        setProviderOptionsWarning("Aucun fournisseur configuré.");
      }
    } catch (e) {
      setProviderOptionsWarning(e?.message || "Impossible de charger les fournisseurs.");
    } finally {
      setProviderOptionsLoading(false);
    }
  }, []);

  const loadProviderIndexers = useCallback(async (providerId) => {
    if (!providerId) return;
    const requestId = ++indexerRequestRef.current;
    setIndexerOptions([]);
    setIndexerOptionsWarning("");
    setIndexerOptionsError("");
    setIndexerOptionsLoading(true);
    try {
      const res = await apiGet(`/api/providers/${providerId}/indexers`);
      if (requestId !== indexerRequestRef.current) return;
      const options = Array.isArray(res) ? res : res?.indexers;
      const normalized = (Array.isArray(options) ? options : [])
        .filter((opt) => opt?.name && opt?.torznabUrl)
        .sort((a, b) => String(a.name).localeCompare(String(b.name)));
      setIndexerOptions(normalized);
      if (normalized.length === 0) {
        setIndexerOptionsWarning("Aucun indexeur disponible (fournisseur non configuré ?)");
      }
    } catch (e) {
      if (requestId !== indexerRequestRef.current) return;
      setIndexerOptionsError(e?.message || "Impossible de charger les indexeurs.");
    } finally {
      if (requestId === indexerRequestRef.current) {
        setIndexerOptionsLoading(false);
      }
    }
  }, []);

  useEffect(() => {
    if (!selectedProviderId) {
      indexerRequestRef.current++;
      setIndexerOptions([]);
      setIndexerOptionsWarning("");
      setIndexerOptionsError("");
      setIndexerOptionsLoading(false);
      return;
    }
    loadProviderIndexers(selectedProviderId);
  }, [selectedProviderId, loadProviderIndexers]);

  useEffect(() => {
    if (!colorOpen) return;
    const handleClick = (event) => {
      if (!colorPickerRef.current) return;
      if (!colorPickerRef.current.contains(event.target)) {
        setColorOpen(false);
      }
    };
    const handleKey = (event) => {
      if (event.key === "Escape") setColorOpen(false);
    };
    document.addEventListener("mousedown", handleClick);
    document.addEventListener("keydown", handleKey);
    return () => {
      document.removeEventListener("mousedown", handleClick);
      document.removeEventListener("keydown", handleKey);
    };
  }, [colorOpen]);

  const prepareAdd = useCallback(() => {
    setName("");
    setTorznabUrl("");
    setApiKey("");
    setEnabled(true);
    setColor(SOURCE_COLOR_PALETTE[0]);
    setColorOpen(false);
    setShowAdvanced(false);
    setTestPassed(false);
    setTestSourceId(null);
    setCapsError("");
    setCapsWarning("");
    setCapsCategories([]);
    setCategoryMappings(new Map());
    setProviderOptions([]);
    setProviderOptionsWarning("");
    setSelectedProviderId("");
    setIndexerOptions([]);
    setIndexerOptionsWarning("");
    setIndexerOptionsError("");
    setSelectedIndexerId("");
    initialEditRef.current = null;
    initialCategoryMappingsRef.current = null;
    loadProviders();
  }, [loadProviders]);

  const prepareEdit = useCallback((s) => {
    const initialColor =
      normalizeHexColor(s?.color) || getSourceColor(s?.id, s?.color) || SOURCE_COLOR_PALETTE[0];
    initialEditRef.current = {
      name: (s?.name ?? "").trim(),
      torznabUrl: (s?.torznabUrl ?? "").trim(),
      color: normalizeHexColor(initialColor) || SOURCE_COLOR_PALETTE[0],
    };
    const existingMappings = (categoriesById[s?.id] || [])
      .map((row) => [Number(row?.id), normalizeCategoryGroupKey(row?.unifiedKey)])
      .filter(([id, key]) => Number.isFinite(id) && id > 0 && !!key);
    initialCategoryMappingsRef.current = new Map(existingMappings);

    setName(s.name ?? "");
    setTorznabUrl(s.torznabUrl ?? "");
    setApiKey(""); // jamais réafficher
    setEnabled(!!s.enabled);
    setColor(initialColor);
    setColorOpen(false);
    setShowAdvanced(false);
    setTestPassed(false);
    setTestSourceId(null);
    setCapsError("");
    setCapsWarning("");
    setCapsCategories([]);
    setCategoryMappings(new Map());
    setSelectedProviderId("");
    setSelectedIndexerId("");
  }, [categoriesById]);

  const resetModalState = useCallback(() => {
    setCapsError("");
    setCapsWarning("");
    setCapsCategories([]);
    setCategoryMappings(new Map());
    setSelectedProviderId("");
    setSelectedIndexerId("");
    setIndexerOptionsError("");
    setColorOpen(false);
  }, []);

  const {
    modalOpen,
    editing,
    confirmOpen,
    confirmTarget,
    modalStep,
    setModalStep,
    openAdd,
    openEdit,
    closeModal,
    openDeleteConfirm,
    closeDeleteConfirm,
  } = useIndexerModal({
    onPrepareAdd: prepareAdd,
    onPrepareEdit: prepareEdit,
    onAfterClose: resetModalState,
  });

  const toggleAdvanced = useCallback(() => {
    setShowAdvanced((v) => !v);
  }, []);

  async function testIndexer() {
    setCapsError("");
    setCapsWarning("");
    setErr("");

    const payload = {
      torznabUrl: torznabUrl.trim(),
      apiKey: apiKey.trim(),
      authMode: "query",
      indexerName: name.trim(),
    };

    if (!payload.torznabUrl) {
      setCapsError("URL requise pour tester.");
      return;
    }
    if (!editing?.id && !selectedProviderId) {
      setCapsError("Choisis un fournisseur avant de tester.");
      return;
    }

    setCapsLoading(true);

    // Ajouter la tâche à la sidebar (pour test caps)
    if (editing?.id) {
      startCapsRefresh(editing.id, editing.name || `Source #${editing.id}`);
    } else {
      startNewIndexerTest();
    }

    try {
      const res = editing?.id
        ? await apiGet(`/api/categories/caps?sourceId=${editing.id}&includeStandardCatalog=true&includeSpecific=true`)
        : await apiPost("/api/categories/caps/provider", {
          providerId:   Number(selectedProviderId),
          torznabUrl:   payload.torznabUrl,
          indexerName:  payload.indexerName,
          indexerId:    isManualIndexer ? null : (selectedIndexerId || null),
          includeStandardCatalog: true,
          includeSpecific: true,
        });

      const cats = Array.isArray(res?.categories) ? res.categories : [];
      const uniqueCats = dedupeCategoriesById(cats);
      const warnings = Array.isArray(res?.warnings) ? res.warnings : [];
      if (warnings.length > 0) {
        setCapsWarning(warnings.join(" "));
      }

      setCapsCategories(uniqueCats);
      const mapped = mapFromCapsAssignments(uniqueCats);
      setCategoryMappings(mapped);
      if (uniqueCats.length === 0 && warnings.length === 0) {
        setCapsWarning("Aucune catégorie disponible.");
      }

      if (editing?.id) {
        initialCategoryMappingsRef.current = new Map(mapped);
        setModalStep(2);
      } else {
        setCategoryMappings(new Map());
        // Capture the source ID if the backend created one during test
        if (res?.sourceId) {
          setTestSourceId(res.sourceId);
        }
        // Don't go to step 2 immediately - show "Suivant" button first
        setTestPassed(true);
      }
    } catch (e) {
      setCapsError(e?.message || "Erreur test indexeur");
    } finally {
      setCapsLoading(false);
      // Retirer la tâche de la sidebar
      if (editing?.id) {
        completeCapsRefresh(editing.id);
      } else {
        completeNewIndexerTest();
      }
    }
  }

  async function save(e) {
    if (e?.preventDefault) e.preventDefault();
    if (e?.stopPropagation) e.stopPropagation();
    if (saving) return;
    setErr("");
    setCapsError("");
    setSaving(true);

    if (isAdding && !selectedProviderId) {
      setCapsError("Choisis un fournisseur avant d’enregistrer.");
      setSaving(false);
      return;
    }

    const normalizedColor = normalizeHexColor(color) || SOURCE_COLOR_PALETTE[0];
    const payload = {
      name: name.trim(),
      torznabUrl: torznabUrl.trim(),
      authMode: "query",
      apiKey: apiKey.trim() || null,
      providerId: selectedProviderId ? Number(selectedProviderId) : null,
      color: normalizedColor,
    };

    try {
      const normalizedMappings = new Map(
        [...categoryMappings.entries()]
          .map(([catId, groupKey]) => [Number(catId), normalizeCategoryGroupKey(groupKey)])
          .filter(([catId, groupKey]) => Number.isFinite(catId) && catId > 0 && !!groupKey)
      );
      const selectedCategoryIds = [...normalizedMappings.keys()]
        .map((catId) => Number(catId))
        .filter((catId) => Number.isFinite(catId) && catId > 0)
        .sort((a, b) => a - b);

      if (editing?.id) {
        if (modalStep === 2) {
          await apiPut(`/api/sources/${editing.id}`, payload);
          await apiPut(`/api/sources/${editing.id}/enabled`, { enabled });
          const patch = buildMappingDiff(initialCategoryMappingsRef.current, normalizedMappings);
          await apiPatch(
            `/api/sources/${editing.id}/category-mappings`,
            buildCategoryMappingsPatchDto({
              mappings: patch,
              selectedCategoryIds,
            })
          );
        } else {
          await apiPut(`/api/sources/${editing.id}`, payload);
          await apiPut(`/api/sources/${editing.id}/enabled`, { enabled });
        }
      } else {
        if (modalStep !== 2) return;
        const mappingsPayload = buildMappingsPayload(normalizedMappings);

        let sourceId = testSourceId;
        // If source was already created during test, update it instead of creating
        if (sourceId) {
          await apiPut(`/api/sources/${sourceId}`, payload);
          await apiPut(`/api/sources/${sourceId}/enabled`, { enabled });
        } else {
          const created = await apiPost("/api/sources", {
            ...payload,
          });
          sourceId = Number(created?.id) || null;
        }

        if (sourceId) {
          await apiPatch(
            `/api/sources/${sourceId}/category-mappings`,
            buildCategoryMappingsPatchDto({
              mappings: mappingsPayload,
              selectedCategoryIds,
            })
          );
        }
      }

      await load();
      if (typeof window !== "undefined") {
        window.dispatchEvent(new Event("onboarding:refresh"));
      }
      closeModal();
    } catch (e2) {
      setErr(e2?.message || "Erreur sauvegarde");
    } finally {
      setSaving(false);
    }
  }

  async function toggleEnabled(s) {
    if (!s?.id) return;
    setErr("");
    try {
      await apiPut(`/api/sources/${s.id}/enabled`, { enabled: !s.enabled });
      await load();
    } catch (e) {
      setErr(e?.message || "Erreur enable/disable");
    }
  }

  async function reclassifyExisting() {
    if (!editing?.id || reclassifying) return;
    const confirmed =
      typeof window === "undefined"
        ? true
        : window.confirm("Reclasser l'existant pour cette source ? Cette action peut prendre quelques instants.");
    if (!confirmed) return;

    setErr("");
    setCapsError("");
    setReclassifying(true);
    try {
      await apiPost(`/api/sources/${editing.id}/reclassify`);
      await load();
      setCapsWarning("Reclassement lancé avec succès pour cette source.");
    } catch (e) {
      setErr(e?.message || "Erreur reclassification");
    } finally {
      setReclassifying(false);
    }
  }

  // Subbar (comme Library)
  useEffect(() => {
    setContent(
      <div className="indexers-subbar-content" subbarClassName="subbar--indexers-sync">
        <SubAction icon="refresh" label="Refresh" onClick={load} />
        <SubAction icon="add_circle" label="Ajouter" onClick={openAdd} />
        <SubAction
          icon="settings"
          label="Options"
          onClick={() => setSyncModalOpen(true)}
          title="Options de synchronisation"
        />
        {hasEnabledIndexers && <div className="subspacer" />}
        {hasEnabledIndexers && (
          <SubAction
            icon={syncAllRunning ? "progress_activity" : "sync"}
            label="Sync all"
            onClick={syncAll}
            disabled={!hasEnabledIndexers || syncAllRunning || hasPendingSync}
            title={!hasEnabledIndexers ? "Aucun indexer actif" : "Sync all"}
            className={syncAllRunning ? "is-loading" : undefined}
          />
        )}
      </div>
    );
    return () => setContent(null);
  }, [
    setContent,
    load,
    openAdd,
    syncAll,
    syncAllRunning,
    hasEnabledIndexers,
    hasPendingSync,
  ]);

  async function removeSource(s) {
    if (!s?.id) return;
    setErr("");
    try {
      await apiDelete(`/api/sources/${s.id}`);
      await load();
    } catch (e) {
      setErr(e?.message || "Erreur suppression");
    }
  }

  const rows = useMemo(() => {
    return (items || [])
      .map((s) => ({
        ...s,
        _name: s.name ?? `Source ${s.id}`,
        _url: s.torznabUrl ?? "",
      }))
      .sort((a, b) => (Number(a.id) || 0) - (Number(b.id) || 0));
  }, [items]);

  const isEditing = !!editing;
  const isAdding = !editing;
  const selectedColor = normalizeHexColor(color) || SOURCE_COLOR_PALETTE[0];
  const colorPickerDisabled = isEditing && modalStep === 2;
  const isManualIndexer = isAdding && selectedIndexerId === MANUAL_INDEXER_VALUE;
  const canTest = isAdding
    ? selectedProviderId && torznabUrl.trim()
    : torznabUrl.trim();
  const editDirty = useMemo(() => {
    if (!isEditing) return false;
    const initial = initialEditRef.current;
    if (!initial) return false;
    const nameChanged = name.trim() !== initial.name;
    const urlChanged = torznabUrl.trim() !== initial.torznabUrl;
    const colorChanged = selectedColor !== initial.color;
    const apiKeyChanged = !!apiKey.trim();
    return nameChanged || urlChanged || colorChanged || apiKeyChanged;
  }, [isEditing, name, torznabUrl, selectedColor, apiKey]);
  const categoriesDirty = useMemo(() => {
    if (!isEditing || modalStep !== 2) return false;
    const initial = initialCategoryMappingsRef.current;
    if (!initial) return false;
    return !areMappingMapsEqual(categoryMappings, initial);
  }, [isEditing, modalStep, categoryMappings]);
  const canSaveEdit = isEditing && (editDirty || categoriesDirty);
  const selectedCount = categoryMappings.size;
  const capsCount = capsCategories.length;
  const indexerModalWidth = modalStep === 2 ? "75vw" : 560;

  // Filter out indexers that are already added (but always keep the currently selected one)
  // Normalisation URL des deux côtés (trailing slash, casse) pour éviter les faux négatifs.
  const existingIndexerUrls = useMemo(
    () => new Set((items || []).map((s) => normalizeUrl(s.torznabUrl)).filter(Boolean)),
    [items]
  );
  const availableIndexerOptions = useMemo(() => {
    const filtered = (indexerOptions || []).filter((opt) => !existingIndexerUrls.has(normalizeUrl(opt.torznabUrl)));
    // Always include the currently selected indexer (in case it was saved during test)
    if (selectedIndexerId) {
      const alreadyIncluded = filtered.some((opt) => String(opt.id) === String(selectedIndexerId));
      if (!alreadyIncluded) {
        const current = (indexerOptions || []).find((opt) => String(opt.id) === String(selectedIndexerId));
        if (current) return [...filtered, current];
      }
    }
    return filtered;
  }, [indexerOptions, existingIndexerUrls, selectedIndexerId]);
  const modalPreviewCredentials = useMemo(() => {
    if (isEditing) return null;

    const trimmedTorznabUrl = torznabUrl.trim();
    if (!trimmedTorznabUrl) return null;

    return {
      providerId: selectedProviderId ? Number(selectedProviderId) : null,
      torznabUrl: trimmedTorznabUrl,
      indexerId: isManualIndexer ? null : (selectedIndexerId || null),
      authMode: "query",
      apiKey: apiKey.trim(),
      sourceName: name.trim(),
    };
  }, [isEditing, torznabUrl, selectedProviderId, isManualIndexer, selectedIndexerId, apiKey, name]);

  return (
    <div className="page page--indexers">
      <div className="pagehead">
        <div>
          <h1>Indexeurs</h1>
          <div className="muted">Sources Torznab / Jackett / Prowlarr</div>
        </div>
      </div>
      <div className="pagehead__divider" />

      {err && (
        <div className="errorbox">
          <div className="errorbox__title">Erreur</div>
          <div className="muted">{err}</div>
        </div>
      )}

      {loading ? (
        <Loader label="Chargement des sources…" />
      ) : (
        <div className="indexer-list itemrow-grid">
          {rows.map((s) => {
            const isSyncing = syncingId === s.id || syncStatusById[s.id] === "pending";
            const isTesting = testingId === s.id;
            const isBusy = isSyncing || isTesting || syncAllRunning;

            // Status class for animations
            const statusClass = [
              testStatusById[s.id] === "ok" && "test-ok",
              testStatusById[s.id] === "error" && "test-err",
              syncStatusById[s.id] === "ok" && "sync-ok",
              syncStatusById[s.id] === "error" && "sync-err",
            ].filter(Boolean).join(" ");

            // Categories as badges — 1 bulle par unifiedKey distinct
            const categoryBadges = dedupeBubblesByUnifiedKey(
              (categoriesById[s.id] || []).slice().sort((a, b) => {
                const orderA = MAPPING_GROUP_PRIORITY.indexOf(a.unifiedKey);
                const orderB = MAPPING_GROUP_PRIORITY.indexOf(b.unifiedKey);
                return (orderA === -1 ? 999 : orderA) - (orderB === -1 ? 999 : orderB);
              })
            ).map((cat) => (
              <CategoryBubble
                key={cat.unifiedKey || cat.id}
                unifiedKey={cat.unifiedKey}
                label={cat.unifiedLabel || cat.name}
                title={`${cat.name} (${cat.id})`}
              />
            ));

            return (
              <ItemRow
                key={s.id}
                id={s.id}
                title={s._name}
                meta={s._url}
                enabled={s.enabled}
                badges={categoryBadges}
                statusClass={statusClass}
                actions={[
                  {
                    icon: "sync",
                    title: isSyncing ? "Sync en cours..." : "Sync",
                    onClick: () => syncSource(s),
                    disabled: isBusy || !s.enabled,
                    spinning: isSyncing,
                  },
                  {
                    icon: "science",
                    title: isTesting ? "Test en cours..." : "Test",
                    onClick: () => testSource(s),
                    disabled: isTesting || isSyncing || !s.enabled,
                    spinning: isTesting,
                  },
                  {
                    icon: "edit",
                    title: "Modifier",
                    onClick: () => openEdit(s),
                    disabled: isBusy || !s.enabled,
                  },
                  {
                    icon: "delete",
                    title: "Supprimer",
                    onClick: () => openDeleteConfirm(s),
                    disabled: isBusy || !s.enabled,
                    className: "iconbtn--danger",
                  },
                ]}
                showToggle
                onToggle={() => toggleEnabled(s)}
                toggleDisabled={isSyncing || isTesting}
              />
            );
          })}
        </div>
      )}

      {/* MODAL SYNC OPTIONS */}
      <Modal
        open={syncModalOpen}
        title="Options de synchronisation"
        onClose={() => setSyncModalOpen(false)}
        width={560}
      >
        <div style={{ padding: 12 }}>
          <IndexersSyncSettingsCard showSaveButton />
        </div>
      </Modal>

      {/* MODAL DELETE CONFIRM */}
      <Modal
        open={confirmOpen}
        title={confirmTarget ? `Supprimer : ${confirmTarget?.name ?? confirmTarget?.id}` : "Supprimer"}
        onClose={closeDeleteConfirm}
        width={520}
      >
        <div style={{ padding: 12 }}>
          <div style={{ fontWeight: 700, marginBottom: 8 }}>
            Confirmer la suppression ?
          </div>
          <div className="muted">
            Cette action est irréversible.
          </div>
          <div className="formactions" style={{ marginTop: 16 }}>
            <button
              className="btn btn-danger"
              type="button"
              onClick={async () => {
                const target = confirmTarget;
                closeDeleteConfirm();
                if (target) await removeSource(target);
              }}
            >
              Supprimer
            </button>
            <button className="btn" type="button" onClick={closeDeleteConfirm}>
              Annuler
            </button>
          </div>
        </div>
      </Modal>

      {/* MODAL ADD / EDIT */}
      <Modal
        open={modalOpen}
        title={editing ? `Modifier : ${editing?.name ?? editing?.id}` : "Ajouter une source"}
        onClose={closeModal}
        width={indexerModalWidth}
      >
        <form onSubmit={(e) => e.preventDefault()} className="formgrid formgrid--edit">
          {/* Step 1 fields - hidden in step 2 */}
          {modalStep === 1 && (
            <>
              {isAdding && (
                <div className="field">
                  <label className="muted">Fournisseur</label>
                  <select
                    value={selectedProviderId}
                    onChange={(e) => {
                      const nextId = e.target.value;
                      setSelectedProviderId(nextId);
                      setSelectedIndexerId("");
                      setIndexerOptions([]);
                      setIndexerOptionsWarning("");
                      setIndexerOptionsError("");
                      setName("");
                      setTorznabUrl("");
                      setCapsError("");
                      setCapsWarning("");
                      setCapsCategories([]);
                      setCategoryMappings(new Map());
                      setTestPassed(false);
                      setTestSourceId(null);
                    }}
                    disabled={providerOptionsLoading || providerOptions.length === 0}
                  >
                    <option value="" disabled>
                      Choisir un fournisseur...
                    </option>
                    {providerOptions.map((p) => (
                      <option key={p.id} value={p.id}>
                        {p.name || (String(p.type || "").toLowerCase() === "prowlarr" ? "Prowlarr" : "Jackett")}
                      </option>
                    ))}
                  </select>
                  {providerOptionsWarning && (
                    <div className="muted" style={{ marginTop: 6 }}>{providerOptionsWarning}</div>
                  )}
                </div>
              )}

              {isAdding && (
                <div className="field">
                  <label className="muted">Indexeur</label>
                  <select
                    value={selectedIndexerId}
                    onChange={(e) => {
                      const nextId = e.target.value;
                      setSelectedIndexerId(nextId);
                      if (nextId === MANUAL_INDEXER_VALUE) {
                        setName("");
                        setTorznabUrl("");
                        setTestPassed(false);
                        setTestSourceId(null);
                        return;
                      }
                      const match = availableIndexerOptions.find(
                        (opt) => String(opt?.id) === String(nextId)
                      );
                      if (match) {
                        setName(match.name || "");
                        setTorznabUrl(match.torznabUrl || "");
                      }
                      setTestPassed(false);
                      setTestSourceId(null);
                    }}
                    disabled={!selectedProviderId || indexerOptionsLoading}
                  >
                    <option value="" disabled>
                      Choisir un indexeur...
                    </option>
                    {availableIndexerOptions.map((opt) => (
                      <option key={opt.id} value={opt.id}>
                        {opt.name}
                      </option>
                    ))}
                    <option value={MANUAL_INDEXER_VALUE}>Ajouter manuellement...</option>
                  </select>
                  {indexerOptionsWarning && (
                    <div className="muted" style={{ marginTop: 6 }}>{indexerOptionsWarning}</div>
                  )}
                  {!indexerOptionsLoading && indexerOptions.length > 0 && availableIndexerOptions.length === 0 && (
                    <div className="muted" style={{ marginTop: 6 }}>Tous les indexeurs sont déjà ajoutés.</div>
                  )}
                  {indexerOptionsLoading && (
                    <div className="muted" style={{ marginTop: 6 }}>Chargement des indexeurs...</div>
                  )}
                  {indexerOptionsError && (
                    <div className="errorbox" style={{ marginTop: 8 }}>
                      <div className="errorbox__title">Erreur</div>
                      <div className="muted" style={{ marginBottom: 6 }}>{indexerOptionsError}</div>
                      <button
                        className="btn"
                        type="button"
                        onClick={() => loadProviderIndexers(selectedProviderId)}
                        disabled={!selectedProviderId || indexerOptionsLoading}
                      >
                        Retry
                      </button>
                    </div>
                  )}
                </div>
              )}

              {isAdding && isManualIndexer && (
                <div className="field">
                  <label className="muted">Torznab URL</label>
                  <input
                    value={torznabUrl}
                    onChange={(e) => setTorznabUrl(e.target.value)}
                    placeholder="Colle l'URL Copy Torznab Feed"
                  />
                  <span className="field-hint">
                    Depuis Jackett/Prowlarr, utilise "Copy Torznab Feed", puis colle l'URL complète.
                  </span>
                </div>
              )}

              {isAdding && isManualIndexer && (
                <div className="field">
                  <label className="muted">Clé API (optionnel)</label>
                  <input
                    value={apiKey}
                    onChange={(e) => setApiKey(e.target.value)}
                    placeholder="Laisse vide pour utiliser la clé du fournisseur"
                  />
                </div>
              )}

              <div className="field">
                <label className="muted">{tr("Nom", "Name")}</label>
                <input
                  value={name}
                  onChange={(e) => setName(e.target.value)}
                  placeholder={tr("Nom de l'indexeur", "Indexer name")}
                  disabled={isAdding && !selectedIndexerId}
                />
              </div>

              <div className="field" style={{ gridColumn: "1 / -1" }}>
                <label className="muted">{tr("Couleur", "Color")}</label>
                <div className={`color-slider${colorOpen ? " is-open" : ""}`} ref={colorPickerRef}>
                  <button
                    className="color-dot color-dot--main"
                    type="button"
                    onClick={() => !colorPickerDisabled && setColorOpen((v) => !v)}
                    disabled={colorPickerDisabled}
                    style={{ background: selectedColor }}
                    aria-label={tr("Choisir une couleur", "Choose a color")}
                    title={tr("Choisir une couleur", "Choose a color")}
                  />
                  <div className="color-slider__track">
                    {SOURCE_COLOR_PALETTE.map((swatch) => {
                      const normalized = normalizeHexColor(swatch);
                      const isActive = normalized === selectedColor;
                      return (
                        <button
                          key={swatch}
                          type="button"
                          className={`color-swatch${isActive ? " is-active" : ""}`}
                          style={{ background: swatch }}
                          onClick={() => {
                            setColor(swatch);
                            setColorOpen(false);
                          }}
                          aria-label={`${tr("Couleur", "Color")} ${swatch}`}
                          title={swatch}
                        />
                      );
                    })}
                  </div>
                </div>
              </div>
            </>
          )}

          {/* Summary header for step 2 in add mode */}
          {isAdding && modalStep === 2 && (
            <div className="field" style={{ gridColumn: "1 / -1", marginBottom: 8 }}>
              <div style={{ display: "flex", alignItems: "center", gap: 12 }}>
                <span
                  className="color-dot"
                  style={{ background: selectedColor, width: 16, height: 16, borderRadius: "50%", flexShrink: 0 }}
                />
                <span style={{ fontWeight: 600 }}>{name || "Nouvel indexeur"}</span>
                <button
                  type="button"
                  className="btn btn-sm"
                  onClick={() => {
                    setModalStep(1);
                    setTestPassed(false);
                  }}
                  style={{ marginLeft: "auto" }}
                >
                  Modifier
                </button>
              </div>
            </div>
          )}

          {isEditing && showAdvanced && modalStep === 1 && (
            <div className="field" style={{ gridColumn: "1 / -1" }}>
              <div className="advanced-fields">
                <div className="field">
                  <label className="muted">Torznab URL</label>
                  <input
                    value={torznabUrl}
                    onChange={(e) => setTorznabUrl(e.target.value)}
                    placeholder="http://ip:port/api..."
                    disabled={modalStep === 2}
                  />
                </div>
                <div className="field">
                  <label className="muted">Clé API</label>
                  <input
                    value={apiKey}
                    onChange={(e) => setApiKey(e.target.value)}
                    placeholder="Laisse vide pour ne pas changer"
                    disabled={modalStep === 2}
                  />
                </div>
              </div>
            </div>
          )}

          {modalStep === 2 && (
            <div className="field" style={{ gridColumn: "1 / -1" }}>
              <label className="muted">Catégories assignées ({selectedCount}/{capsCount})</label>
              <div style={{ marginTop: 8 }}>
                <CategoryMappingBoard
                  categories={capsCategories}
                  mappings={categoryMappings}
                  sourceId={editing?.id}
                  previewCredentials={modalPreviewCredentials}
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
              </div>
            </div>
          )}

          {capsError && (
            <div className="errorbox" style={{ gridColumn: "1 / -1" }}>
              <div className="errorbox__title">Erreur</div>
              <div className="muted">{capsError}</div>
            </div>
          )}
          {capsWarning && (
            <div className="errorbox" style={{ gridColumn: "1 / -1" }}>
              <div className="errorbox__title">Info</div>
              <div className="muted">{capsWarning}</div>
            </div>
          )}

          <div className="formactions">
            {isEditing && modalStep === 1 ? (
              <div className="formactions-row">
                <div className="formactions-left">
                  {showAdvanced && (
                    <button
                      className="btn btn-fixed-info btn-nohover"
                      type="button"
                      onClick={testIndexer}
                      disabled={!canTest || capsLoading || saving}
                    >
                      {capsLoading ? "Test..." : "Tester categories"}
                    </button>
                  )}
                </div>
                <div className="formactions-right">
                  <button
                    type="button"
                    className="btn btn-sm"
                    onClick={toggleAdvanced}
                  >
                    {showAdvanced ? "Masquer options avancées" : "Options avancées"}
                  </button>
                  {canSaveEdit && (
                    <button className="btn btn-hover-ok" type="button" onClick={save} disabled={saving}>
                      Enregistrer
                    </button>
                  )}
                  <button className="btn btn-fixed-danger btn-nohover" type="button" onClick={closeModal} disabled={saving}>
                    Annuler
                  </button>
                </div>
              </div>
            ) : isEditing && modalStep === 2 ? (
              <div className="formactions-row">
                <div className="formactions-left">
                  <button
                    className="btn btn-hover-info"
                    type="button"
                    onClick={reclassifyExisting}
                    disabled={saving || reclassifying}
                  >
                    {reclassifying ? "Reclassement..." : "Reclasser l'existant"}
                  </button>
                </div>
                <div className="formactions-right">
                  {canSaveEdit && (
                    <button className="btn btn-hover-ok" type="button" onClick={save} disabled={saving}>
                      Enregistrer
                    </button>
                  )}
                  <button className="btn btn-fixed-danger btn-nohover" type="button" onClick={closeModal} disabled={saving}>
                    Annuler
                  </button>
                </div>
              </div>
            ) : modalStep === 1 ? (
              <div className="formactions-row">
                <div className="formactions-left">
                  {testPassed && (
                    <span className="muted" style={{ display: "flex", alignItems: "center", gap: 6 }}>
                      <AppIcon name="check_circle" style={{ color: "var(--ok)", fontSize: 18 }} />
                      Test réussi
                    </span>
                  )}
                </div>
                <div className="formactions-right">
                  {testPassed ? (
                    <button
                      className="btn btn-hover-ok"
                      type="button"
                      onClick={() => setModalStep(2)}
                    >
                      Suivant
                    </button>
                  ) : canTest ? (
                    <button
                      className="btn btn-fixed-info btn-nohover"
                      type="button"
                      onClick={testIndexer}
                      disabled={capsLoading || saving}
                    >
                      {capsLoading ? "Test..." : "Tester"}
                    </button>
                  ) : null}
                  <button className="btn btn-fixed-danger btn-nohover" type="button" onClick={closeModal} disabled={capsLoading || saving}>
                    Annuler
                  </button>
                </div>
              </div>
            ) : (
              <div className="formactions-row">
                <div className="formactions-left" />
                <div className="formactions-right">
                  <button className="btn btn-hover-ok" type="button" onClick={save} disabled={saving}>
                    Enregistrer
                  </button>
                  <button className="btn btn-fixed-danger btn-nohover" type="button" onClick={closeModal} disabled={saving}>
                    Annuler
                  </button>
                </div>
              </div>
            )}
          </div>
        </form>
      </Modal>
    </div>
  );
}
