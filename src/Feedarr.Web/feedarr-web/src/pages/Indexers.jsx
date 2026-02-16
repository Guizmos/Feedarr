import React, { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { apiDelete, apiGet, apiPost, apiPut } from "../api/client.js";
import Loader from "../ui/Loader.jsx";
import { useSubbarSetter } from "../layout/useSubbar.js";
import SubAction from "../ui/SubAction.jsx";
import Modal from "../ui/Modal.jsx";
import AppIcon from "../ui/AppIcon.jsx";
import ToggleSwitch from "../ui/ToggleSwitch.jsx";
import { startNewIndexerTest, completeNewIndexerTest } from "../tasks/indexerTasks.js";
import { startCapsRefresh, completeCapsRefresh } from "../tasks/categoriesTasks.js";
import ItemRow, { CategoryBubble } from "../ui/ItemRow.jsx";
import IndexersSyncSettingsCard from "../components/indexers/IndexersSyncSettingsCard.jsx";
import useIndexerModal from "./indexers/hooks/useIndexerModal.js";
import useIndexerSync from "./indexers/hooks/useIndexerSync.js";
import {
  SOURCE_COLOR_PALETTE,
  getSourceColor,
  normalizeHexColor,
} from "../utils/sourceColors.js";

const UNIFIED_PRIORITY = ["series", "anime", "films", "games", "spectacle", "shows"];
const MANUAL_INDEXER_VALUE = "__manual__";

function dedupeCategoriesById(categories) {
  if (!Array.isArray(categories)) return [];
  const map = new Map();
  const score = (cat) => {
    let s = 0;
    if (cat?.unifiedKey) s += 4;
    if (cat?.unifiedLabel) s += 2;
    if (cat?.parentId) s += 1;
    if (cat?.isSub) s += 1;
    return s;
  };

  for (const cat of categories) {
    const id = Number(cat?.id);
    if (!Number.isFinite(id) || id <= 0) continue;
    const normalized = { ...cat, id };
    const existing = map.get(id);
    if (!existing || score(normalized) > score(existing)) {
      map.set(id, normalized);
    }
  }

  return Array.from(map.values());
}

function areSetsEqual(a, b) {
  if (a === b) return true;
  if (!a || !b) return false;
  if (a.size !== b.size) return false;
  for (const value of a) {
    if (!b.has(value)) return false;
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
  const [useRecommendedFilter, setUseRecommendedFilter] = useState(true);
  const [selectedCategoryIds, setSelectedCategoryIds] = useState(() => new Set());
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
  const [syncSettingsSaveState, setSyncSettingsSaveState] = useState("idle");
  const [syncSettingsDirty, setSyncSettingsDirty] = useState(false);
  const syncSettingsSaveRef = useRef(null);
  const initialEditRef = useRef(null);
  const initialCategoryIdsRef = useRef(null);

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
            const cats = await apiGet(`/api/categories/${s.id}`);
            catsMap[s.id] = Array.isArray(cats) ? cats : [];
          } catch {
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
    setUseRecommendedFilter(true);
    setSelectedCategoryIds(new Set());
    setProviderOptions([]);
    setProviderOptionsWarning("");
    setSelectedProviderId("");
    setIndexerOptions([]);
    setIndexerOptionsWarning("");
    setIndexerOptionsError("");
    setSelectedIndexerId("");
    initialEditRef.current = null;
    initialCategoryIdsRef.current = null;
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
    const existing = (categoriesById[s?.id] || [])
      .map((row) => Number(row?.id))
      .filter((id) => Number.isFinite(id) && id > 0);
    initialCategoryIdsRef.current = new Set(existing);

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
    setUseRecommendedFilter(true);
    setSelectedCategoryIds(new Set());
    setSelectedProviderId("");
    setSelectedIndexerId("");
  }, [categoriesById]);

  const resetModalState = useCallback(() => {
    setCapsError("");
    setCapsWarning("");
    setCapsCategories([]);
    setUseRecommendedFilter(true);
    setSelectedCategoryIds(new Set());
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
        ? await apiGet(`/api/categories/caps?sourceId=${editing.id}`)
        : await apiPost("/api/categories/caps/provider", {
          providerId: Number(selectedProviderId),
          torznabUrl: payload.torznabUrl,
          indexerName: payload.indexerName,
          indexerId: isManualIndexer ? null : (selectedIndexerId || null),
        });

      const cats = Array.isArray(res?.categories) ? res.categories : [];
      const uniqueCats = dedupeCategoriesById(cats);
      const warnings = Array.isArray(res?.warnings) ? res.warnings : [];
      if (warnings.length > 0) {
        setCapsWarning(warnings.join(" "));
      }

      setCapsCategories(uniqueCats);
      if (uniqueCats.length === 0 && warnings.length === 0) {
        setCapsWarning("Aucune catégorie disponible.");
      }

      if (editing?.id) {
        const existing = await apiGet(`/api/categories/${editing.id}`);
        const existingIds = new Set(
          (Array.isArray(existing) ? existing : [])
            .map((row) => Number(row?.id))
            .filter((id) => Number.isFinite(id) && id > 0)
        );
        const selected = uniqueCats
          .filter((c) => existingIds.has(c.id))
          .map((c) => c.id);
        const selectedSet = new Set(selected);
        setSelectedCategoryIds(selectedSet);
        initialCategoryIdsRef.current = new Set(selectedSet);
        setModalStep(2);
      } else {
        const recommended = uniqueCats.filter((c) => c?.isRecommended);
        const base = recommended.length > 0 ? recommended : uniqueCats;
        setSelectedCategoryIds(new Set(base.map((c) => c.id)));
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
      if (editing?.id) {
        if (modalStep === 2) {
          await apiPut(`/api/sources/${editing.id}`, payload);
          await apiPut(`/api/sources/${editing.id}/enabled`, { enabled });
          const selected = dedupeCategoriesById(
            capsCategories.filter((c) => selectedCategoryIds.has(c.id))
          );
          if (selected.length === 0) {
            setCapsError("Selectionne au moins une categorie.");
            return;
          }
          await apiPut(`/api/sources/${editing.id}/categories`, {
            categories: selected.map((c) => ({
              id: c.id,
              name: c.name,
              isSub: c.isSub,
              parentId: c.parentId,
              unifiedKey: c.unifiedKey,
              unifiedLabel: c.unifiedLabel,
            })),
          });
        } else {
          await apiPut(`/api/sources/${editing.id}`, payload);
          await apiPut(`/api/sources/${editing.id}/enabled`, { enabled });
        }
      } else {
        if (modalStep !== 2) return;
        const selected = dedupeCategoriesById(
          capsCategories.filter((c) => selectedCategoryIds.has(c.id))
        );
        if (selected.length === 0) {
          setCapsError("Selectionne au moins une categorie.");
          return;
        }
        const categoriesPayload = selected.map((c) => ({
          id: c.id,
          name: c.name,
          isSub: c.isSub,
          parentId: c.parentId,
          unifiedKey: c.unifiedKey,
          unifiedLabel: c.unifiedLabel,
        }));

        // If source was already created during test, update it instead of creating
        if (testSourceId) {
          await apiPut(`/api/sources/${testSourceId}`, payload);
          await apiPut(`/api/sources/${testSourceId}/enabled`, { enabled });
          await apiPut(`/api/sources/${testSourceId}/categories`, { categories: categoriesPayload });
        } else {
          await apiPost("/api/sources", {
            ...payload,
            categories: categoriesPayload,
          });
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

  // Subbar (comme Library)
  useEffect(() => {
    setContent(
      <>
        <SubAction icon="refresh" label="Refresh" onClick={load} />
        <SubAction icon="add_circle" label="Ajouter" onClick={openAdd} />
        <SubAction
          icon={
            syncSettingsSaveState === "loading"
              ? "progress_activity"
              : syncSettingsSaveState === "success"
              ? "check_circle"
              : syncSettingsSaveState === "error"
              ? "cancel"
              : "save"
          }
          label="Sauver"
          onClick={() => syncSettingsSaveRef.current?.()}
          disabled={syncSettingsSaveState === "loading" || !syncSettingsDirty}
          className={
            syncSettingsSaveState === "loading"
              ? "is-loading"
              : syncSettingsSaveState === "success"
              ? "is-success"
              : syncSettingsSaveState === "error"
              ? "is-error"
              : ""
          }
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
      </>
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
    syncSettingsSaveState,
    syncSettingsDirty,
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
    const initial = initialCategoryIdsRef.current;
    if (!initial) return false;
    return !areSetsEqual(selectedCategoryIds, initial);
  }, [isEditing, modalStep, selectedCategoryIds]);
  const canSaveEdit = isEditing && (editDirty || categoriesDirty);
  const selectedCount = selectedCategoryIds.size;
  const capsCount = capsCategories.length;

  // Filter out indexers that are already added (but always keep the currently selected one)
  const existingIndexerUrls = useMemo(
    () => new Set((items || []).map((s) => s.torznabUrl).filter(Boolean)),
    [items]
  );
  const availableIndexerOptions = useMemo(() => {
    const filtered = (indexerOptions || []).filter((opt) => !existingIndexerUrls.has(opt.torznabUrl));
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

  const allCategories = useMemo(() => capsCategories || [], [capsCategories]);
  const recommendedCategories = useMemo(
    () => (capsCategories || []).filter((c) => c?.isRecommended),
    [capsCategories]
  );
  const visibleCategories = useMemo(
    () => (useRecommendedFilter ? recommendedCategories : allCategories),
    [useRecommendedFilter, recommendedCategories, allCategories]
  );

  useEffect(() => {
    if (!useRecommendedFilter) return;
    if (allCategories.length > 0 && recommendedCategories.length === 0) {
      setUseRecommendedFilter(false);
      setCapsWarning((prev) =>
        prev || "Aucune catégorie recommandée. Affichage complet activé."
      );
    }
  }, [useRecommendedFilter, recommendedCategories.length, allCategories.length]);

  const handleSyncSettingsStateChange = useCallback((nextState) => {
    if (!nextState) return;
    syncSettingsSaveRef.current = nextState.onSave;
    setSyncSettingsDirty(!!nextState.isDirty);
    setSyncSettingsSaveState(nextState.saveState || "idle");
  }, []);

  return (
    <div className="page page--indexers">
      <div className="pagehead">
        <div>
          <h1>Indexers</h1>
          <div className="muted">Sources Torznab / Jackett / Prowlarr</div>
        </div>
      </div>

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

            // Categories as badges
            const categoryBadges = (categoriesById[s.id] || [])
              .slice()
              .sort((a, b) => {
                const orderA = UNIFIED_PRIORITY.indexOf(a.unifiedKey);
                const orderB = UNIFIED_PRIORITY.indexOf(b.unifiedKey);
                return (orderA === -1 ? 999 : orderA) - (orderB === -1 ? 999 : orderB);
              })
              .map((cat) => (
                <CategoryBubble
                  key={cat.id}
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
                    title: "Éditer",
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

      <div className="settings-grid" style={{ marginTop: 16 }}>
        <IndexersSyncSettingsCard onStateChange={handleSyncSettingsStateChange} />
      </div>

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
        width={650}
      >
        <form onSubmit={(e) => e.preventDefault()} className="formgrid formgrid--edit">
          {/* Step 1 fields - hidden in step 2 for add mode */}
          {(isEditing || modalStep === 1) && (
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
                      setUseRecommendedFilter(true);
                      setSelectedCategoryIds(new Set());
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
                <label className="muted">Nom</label>
                <input
                  value={name}
                  onChange={(e) => setName(e.target.value)}
                  placeholder="Nom de l'indexeur"
                  disabled={isAdding && !selectedIndexerId}
                />
              </div>

              <div className="field" style={{ gridColumn: "1 / -1" }}>
                <label className="muted">Couleur</label>
                <div className={`color-slider${colorOpen ? " is-open" : ""}`} ref={colorPickerRef}>
                  <button
                    className="color-dot color-dot--main"
                    type="button"
                    onClick={() => !colorPickerDisabled && setColorOpen((v) => !v)}
                    disabled={colorPickerDisabled}
                    style={{ background: selectedColor }}
                    aria-label="Choisir une couleur"
                    title="Choisir une couleur"
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
                          aria-label={`Couleur ${swatch}`}
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

          {isEditing && showAdvanced && (
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
              <label className="muted">Categories retenues ({selectedCount}/{capsCount || allCategories.length})</label>
              <div style={{ display: "flex", alignItems: "center", gap: 10, margin: "6px 0 10px" }}>
                <span className="muted">Afficher seulement les catégories recommandées</span>
                <ToggleSwitch
                  checked={useRecommendedFilter}
                  onIonChange={(e) => setUseRecommendedFilter(e.detail.checked)}
                  className="settings-toggle settings-toggle--sm"
                />
              </div>
              <div className="category-picker">
                {visibleCategories.map((cat) => (
                  <div key={cat.id} className="category-row">
                    <ToggleSwitch
                      checked={selectedCategoryIds.has(cat.id)}
                      onIonChange={(e) => {
                        const checked = e.detail.checked;
                        setSelectedCategoryIds((prev) => {
                          const next = new Set(prev);
                          if (checked) next.add(cat.id);
                          else next.delete(cat.id);
                          return next;
                        });
                      }}
                      className="settings-toggle settings-toggle--sm"
                    />
                    <span className="category-id">{cat.id}</span>
                    <span className="category-name">{cat.name}</span>
                    <span className="category-pill">{cat.unifiedLabel}</span>
                  </div>
                ))}
                {visibleCategories.length === 0 && useRecommendedFilter && (
                  <div className="muted">Aucune catégorie recommandée. Désactive le filtre pour tout voir.</div>
                )}
                {allCategories.length === 0 && (
                  <div className="muted">Aucune catégorie disponible.</div>
                )}
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
                <div className="formactions-left" />
                <div className="formactions-right">
                  {canSaveEdit && (
                    <button className="btn btn-hover-ok" type="button" onClick={save} disabled={selectedCount === 0 || saving}>
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
                  <button className="btn btn-hover-ok" type="button" onClick={save} disabled={selectedCount === 0 || saving}>
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
