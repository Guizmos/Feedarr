import React, { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { apiDelete, apiGet, apiPost, apiPut } from "../../api/client.js";
import ItemRow from "../../ui/ItemRow.jsx";
import Modal from "../../ui/Modal.jsx";
import { getAppBaseUrlPlaceholder, getAppLabel, isArrLibraryType } from "../../utils/appTypes.js";
import { tr } from "../../app/uiText.js";

const ALL_APP_TYPES = ["sonarr", "radarr", "overseerr", "jellyseerr", "seer"];

export default function Step4ArrApps() {
  const [apps, setApps] = useState([]);
  const [loading, setLoading] = useState(false);
  const [err, setErr] = useState("");

  // Modal state
  const [modalOpen, setModalOpen] = useState(false);
  const [modalMode, setModalMode] = useState("add"); // add | edit
  const [modalApp, setModalApp] = useState(null);
  const [modalType, setModalType] = useState("sonarr");
  const [modalName, setModalName] = useState("");
  const [modalBaseUrl, setModalBaseUrl] = useState("");
  const [modalApiKey, setModalApiKey] = useState("");
  const [modalTesting, setModalTesting] = useState(false);
  const [modalTestState, setModalTestState] = useState("idle");
  const [modalPulse, setModalPulse] = useState("");
  const [modalError, setModalError] = useState("");
  const [modalSaving, setModalSaving] = useState(false);
  const [addType, setAddType] = useState("");
  const pulseTimerRef = useRef(null);

  // Advanced config
  const [advancedOpen, setAdvancedOpen] = useState(false);
  const [configLoading, setConfigLoading] = useState(false);
  const [config, setConfig] = useState(null); // { rootFolders, qualityProfiles, tags }
  const [rootFolder, setRootFolder] = useState("");
  const [qualityProfile, setQualityProfile] = useState("");
  const [tags, setTags] = useState([]);
  // Sonarr-specific
  const [seriesType, setSeriesType] = useState("standard");
  const [seasonFolder, setSeasonFolder] = useState(true);
  const [monitorMode, setMonitorMode] = useState("all");
  const [searchMissing, setSearchMissing] = useState(true);
  const [searchCutoff, setSearchCutoff] = useState(false);
  // Radarr-specific
  const [minAvail, setMinAvail] = useState("released");
  const [searchForMovie, setSearchForMovie] = useState(true);

  const availableAddTypes = useMemo(() => {
    const existingTypes = new Set(
      (apps || []).map((app) => String(app?.type || "").toLowerCase())
    );
    return ALL_APP_TYPES.filter((type) => !existingTypes.has(type));
  }, [apps]);
  const noAvailableAddTypes = availableAddTypes.length === 0;

  const loadApps = useCallback(async () => {
    setLoading(true);
    setErr("");
    try {
      const list = await apiGet("/api/apps");
      setApps(Array.isArray(list) ? list : []);
    } catch (e) {
      setErr(e?.message || tr("Erreur chargement applications", "Applications loading error"));
      setApps([]);
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    loadApps();
  }, [loadApps]);

  useEffect(() => {
    if (!modalOpen) return;
    setModalTestState("idle");
    setModalPulse("");
    setModalError("");
  }, [modalBaseUrl, modalApiKey, modalType, modalOpen]);

  useEffect(() => {
    if (!modalOpen || modalMode !== "add") return;
    if (availableAddTypes.length === 0) {
      setModalType("");
      return;
    }
    if (!availableAddTypes.includes(modalType)) {
      setModalType(availableAddTypes[0]);
    }
  }, [modalMode, modalOpen, modalType, availableAddTypes]);

  function resetModal() {
    setModalMode("add");
    setModalApp(null);
    setModalType("sonarr");
    setModalName("");
    setModalBaseUrl("");
    setModalApiKey("");
    setModalTesting(false);
    setModalTestState("idle");
    setModalPulse("");
    setModalError("");
    setModalSaving(false);
    setAdvancedOpen(false);
    setConfig(null);
    setConfigLoading(false);
    setRootFolder("");
    setQualityProfile("");
    setTags([]);
    setSeriesType("standard");
    setSeasonFolder(true);
    setMonitorMode("all");
    setSearchMissing(true);
    setSearchCutoff(false);
    setMinAvail("released");
    setSearchForMovie(true);
  }

  function openAddForType(type) {
    if (!type) return;
    resetModal();
    setModalMode("add");
    setModalType(type);
    setModalOpen(true);
  }

  function openEdit(app) {
    resetModal();
    setModalMode("edit");
    setModalApp(app);
    setModalType(app.type);
    setModalName(app.name || "");
    setModalBaseUrl(app.baseUrl || "");
    setModalApiKey("");
    setRootFolder(app.rootFolderPath || "");
    setQualityProfile(app.qualityProfileId ? String(app.qualityProfileId) : "");
    setTags(Array.isArray(app.tags) ? app.tags : []);
    setSeriesType(app.seriesType || "standard");
    setSeasonFolder(app.seasonFolder !== false);
    setMonitorMode(app.monitorMode || "all");
    setSearchMissing(app.searchMissing !== false);
    setSearchCutoff(!!app.searchCutoff);
    setMinAvail(app.minimumAvailability || "released");
    setSearchForMovie(app.searchForMovie !== false);
    setModalOpen(true);
    if (app.hasApiKey) {
      loadConfig(app.id);
    }
  }

  function closeModal() {
    setModalOpen(false);
    resetModal();
  }

  async function loadConfig(appId) {
    setConfigLoading(true);
    try {
      const cfg = await apiGet(`/api/apps/${appId}/config`);
      setConfig(cfg);
    } catch {
      setConfig(null);
    } finally {
      setConfigLoading(false);
    }
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

  async function testModal() {
    const canUseStored = modalMode === "edit" && modalApp?.id && !modalApiKey.trim();
    if (!modalBaseUrl.trim() || (!modalApiKey.trim() && !canUseStored)) return;
    setModalTesting(true);
    setModalError("");
    setModalTestState("idle");
    setModalPulse("");

    const start = Date.now();
    let ok = false;
    let errorMsg = "";

    try {
      let res;
      if (canUseStored) {
        res = await apiPost(`/api/apps/${modalApp.id}/test`);
      } else {
        res = await apiPost(`/api/apps/test?type=${modalType}`, {
          baseUrl: modalBaseUrl.trim(),
          apiKey: modalApiKey.trim(),
        });
      }
      ok = !!res?.ok;
      if (!ok) {
        errorMsg = res?.error || tr("Test echoue", "Test failed");
      }
    } catch (e) {
      errorMsg = e?.message || tr("Erreur test connexion", "Connection test error");
    } finally {
      const elapsed = Date.now() - start;
      if (elapsed < 2000) {
        await new Promise((r) => setTimeout(r, 2000 - elapsed));
      }
      setModalTesting(false);
      setModalTestState(ok ? "ok" : "error");
      if (ok) {
        setModalError("");
      } else {
        setModalError(errorMsg || tr("Test echoue", "Test failed"));
      }
      triggerPulse(ok ? "ok" : "error");
    }
  }

  async function saveModal() {
    setModalSaving(true);
    setModalError("");
    try {
      if (modalMode === "add" && !modalType) {
        throw new Error(tr("Toutes les applications sont deja ajoutees.", "All applications are already added."));
      }

      const payload = {
        type: modalType,
        name: modalName.trim() || null,
        baseUrl: modalBaseUrl.trim(),
        rootFolderPath: rootFolder || null,
        qualityProfileId: qualityProfile ? Number(qualityProfile) : null,
        tags: tags.length > 0 ? tags : null,
      };

      if (modalApiKey.trim()) {
        payload.apiKey = modalApiKey.trim();
      }

      if (modalType === "sonarr") {
        payload.seriesType = seriesType;
        payload.seasonFolder = seasonFolder;
        payload.monitorMode = monitorMode;
        payload.searchMissing = searchMissing;
        payload.searchCutoff = searchCutoff;
      } else if (modalType === "radarr") {
        payload.minimumAvailability = minAvail;
        payload.searchForMovie = searchForMovie;
      }

      if (modalMode === "add") {
        await apiPost("/api/apps", payload);
      } else if (modalApp) {
        await apiPut(`/api/apps/${modalApp.id}`, payload);
      }

      closeModal();
      await loadApps();
    } catch (e) {
      setModalError(e?.message || tr("Erreur sauvegarde", "Save error"));
    } finally {
      setModalSaving(false);
    }
  }

  async function deleteApp(app) {
    if (!app?.id) return;
    if (!window.confirm(tr(`Supprimer ${app.name || app.type} ?`, `Delete ${app.name || app.type}?`))) return;
    try {
      await apiDelete(`/api/apps/${app.id}`);
      await loadApps();
    } catch (e) {
      console.error("[Step4 ArrApps] Impossible de supprimer l'application.", e);
      setErr(e?.message || tr("Impossible de supprimer l'application.", "Failed to delete the application."));
    }
  }

  const configTags = useMemo(
    () => (Array.isArray(config?.tags) ? config.tags : []),
    [config]
  );
  const isLibraryType = isArrLibraryType(modalType);
  const hasValidModalType = modalMode !== "add" || !!modalType;
  const canUseStoredKey = modalMode === "edit" && modalApp?.id && !modalApiKey.trim();
  const canTestModal = hasValidModalType && !!modalBaseUrl.trim() && (modalApiKey.trim() || canUseStoredKey) && !modalTesting && !modalSaving;
  const canSaveModal = hasValidModalType && !!modalBaseUrl.trim() && !modalSaving && !modalTesting;
  const canPrimaryModal = modalTestState === "ok" ? canSaveModal : canTestModal;

  return (
    <div className="setup-step setup-arr">
      <div className="setup-arr__header">
        <div>
          <h2>{tr("Applications (Sonarr / Radarr / Overseerr / Jellyseerr / Seer)", "Applications (Sonarr / Radarr / Overseerr / Jellyseerr / Seer)")}</h2>
          <p>{tr("Optionnel - configure une ou plusieurs apps.", "Optional - configure one or more apps.")}</p>
        </div>
        <div className="setup-arr__add settings-row settings-row--ui-select">
          <label>{tr("Ajouter", "Add")}</label>
          <select
            className="settings-field"
            value={addType}
            disabled={noAvailableAddTypes}
            onChange={(e) => {
              const next = e.target.value;
              setAddType("");
              if (next) openAddForType(next);
            }}
          >
            <option value="" disabled>
              {noAvailableAddTypes
                ? tr("Toutes les applications sont deja ajoutees", "All applications are already added")
                : tr("Selectionner...", "Select...")}
            </option>
            {availableAddTypes.map((type) => (
              <option key={type} value={type}>
                {getAppLabel(type)}
              </option>
            ))}
          </select>
        </div>
      </div>

      {err && <div className="onboarding__error">{err}</div>}

      <div className="indexer-list">
        {loading && (
          <div className="indexer-card">
            <div className="indexer-row">
              <span className="indexer-url muted">{tr("Chargement...", "Loading...")}</span>
            </div>
          </div>
        )}

        {!loading && apps.length === 0 && (
          <div className="indexer-card">
            <div className="indexer-row">
              <span className="indexer-url muted">{tr("Aucune application configuree", "No configured application")}</span>
            </div>
          </div>
        )}

        {!loading && apps.map((app, idx) => {
          const appLabel = getAppLabel(app.type);
          const badges = [
            { label: appLabel },
            {
              label: app.hasApiKey ? "API OK" : "NO KEY",
              className: app.hasApiKey ? "pill-ok" : "pill-warn",
            },
          ];

          return (
            <ItemRow
              key={app.id}
              id={idx + 1}
              title={app.name || appLabel}
              meta={app.baseUrl || ""}
              enabled={app.isEnabled}
              badges={badges}
              actions={[
                {
                  icon: "edit",
                  title: tr("Editer", "Edit"),
                  onClick: () => openEdit(app),
                },
                {
                  icon: "delete",
                  title: tr("Supprimer", "Delete"),
                  onClick: () => deleteApp(app),
                  className: "iconbtn--danger",
                },
              ]}
              showToggle={false}
            />
          );
        })}
      </div>

      <Modal
        open={modalOpen}
        title={
          modalMode === "add"
            ? tr("Ajouter une application", "Add an application")
            : `${tr("Editer", "Edit")}: ${modalApp?.name || modalApp?.type || tr("Application", "Application")}`
        }
        onClose={closeModal}
        width={620}
      >
        <div className="formgrid">
          {modalMode === "add" && (
            <div className="field">
              <label>Type</label>
              <select value={modalType} onChange={(e) => setModalType(e.target.value)}>
                {noAvailableAddTypes && <option value="">Aucun type disponible</option>}
                {availableAddTypes.map((type) => (
                  <option key={type} value={type}>
                    {getAppLabel(type)}
                  </option>
                ))}
              </select>
            </div>
          )}
          <div className="field">
            <label>{tr("Nom", "Name")}</label>
            <input
              value={modalName}
              onChange={(e) => setModalName(e.target.value)}
              placeholder={`Mon ${getAppLabel(modalType)}`}
              disabled={modalSaving || modalTesting}
            />
          </div>
          <div className="field">
            <label>Base URL</label>
            <input
              value={modalBaseUrl}
              onChange={(e) => setModalBaseUrl(e.target.value)}
              placeholder={getAppBaseUrlPlaceholder(modalType)}
              disabled={modalSaving || modalTesting}
            />
            <span className="field-hint">IP, hostname ou URL reverse proxy (http/https)</span>
          </div>
          <div className="field">
            <label>{tr("Cle API", "API key")}</label>
            <input
              value={modalApiKey}
              onChange={(e) => setModalApiKey(e.target.value)}
              placeholder={modalMode === "edit" ? "••••••••••••••••" : tr("Entrez la cle API", "Enter API key")}
              disabled={modalSaving || modalTesting}
            />
          </div>
        </div>

        <div className="setup-arr__modal-actions">
          {isLibraryType && (
            <button className="btn" type="button" onClick={() => setAdvancedOpen((v) => !v)}>
              {advancedOpen ? tr("Masquer options avancees", "Hide advanced options") : tr("Options avancees", "Advanced options")}
            </button>
          )}
        </div>

        {modalError && <div className="onboarding__error">{modalError}</div>}
        {modalTestState === "ok" && <div className="onboarding__ok">{tr("Connexion OK", "Connection OK")}</div>}

        {advancedOpen && isLibraryType && (
          <div className="setup-arr__advanced">
            {modalMode === "edit" && modalApp?.hasApiKey && (
              <div className="setup-arr__config">
                <button
                  className="btn"
                  type="button"
                  onClick={() => loadConfig(modalApp.id)}
                  disabled={configLoading}
                >
                  {configLoading ? tr("Chargement...", "Loading...") : tr("Charger la config", "Load config")}
                </button>
              </div>
            )}

            <div className="formgrid">
              <div className="field">
                <label>Root folder</label>
                {config?.rootFolders?.length > 0 ? (
                  <select value={rootFolder} onChange={(e) => setRootFolder(e.target.value)}>
                    <option value="" disabled>{tr("Selectionner...", "Select...")}</option>
                    {config.rootFolders.map((rf) => (
                      <option key={rf.id} value={rf.path}>
                        {rf.path}
                      </option>
                    ))}
                  </select>
                ) : (
                  <input
                    value={rootFolder}
                    onChange={(e) => setRootFolder(e.target.value)}
                    placeholder="/data/series"
                  />
                )}
              </div>

              <div className="field">
                <label>Quality profile</label>
                {config?.qualityProfiles?.length > 0 ? (
                  <select value={qualityProfile} onChange={(e) => setQualityProfile(e.target.value)}>
                    <option value="" disabled>{tr("Selectionner...", "Select...")}</option>
                    {config.qualityProfiles.map((qp) => (
                      <option key={qp.id} value={qp.id}>
                        {qp.name}
                      </option>
                    ))}
                  </select>
                ) : (
                  <input
                    value={qualityProfile}
                    onChange={(e) => setQualityProfile(e.target.value)}
                    placeholder="1"
                  />
                )}
              </div>
            </div>

            {configTags.length > 0 && (
              <div className="setup-arr__tags">
                {configTags.map((tag) => {
                  const checked = tags.includes(tag.id);
                  return (
                    <label key={tag.id} className="pill">
                      <input
                        type="checkbox"
                        checked={checked}
                        onChange={(e) => {
                          const next = new Set(tags);
                          if (e.target.checked) next.add(tag.id);
                          else next.delete(tag.id);
                          setTags(Array.from(next));
                        }}
                      />
                      {tag.label}
                    </label>
                  );
                })}
              </div>
            )}

            {modalType === "sonarr" ? (
              <div className="formgrid">
                <div className="field">
                  <label>Series type</label>
                  <select value={seriesType} onChange={(e) => setSeriesType(e.target.value)}>
                    <option value="standard">Standard</option>
                    <option value="daily">Daily</option>
                    <option value="anime">Anime</option>
                  </select>
                </div>
                <div className="field">
                  <label>Monitor mode</label>
                  <select value={monitorMode} onChange={(e) => setMonitorMode(e.target.value)}>
                    <option value="all">All</option>
                    <option value="future">Future</option>
                    <option value="missing">Missing</option>
                    <option value="existing">Existing</option>
                    <option value="latest">Latest</option>
                    <option value="pilot">Pilot</option>
                    <option value="firstSeason">First Season</option>
                  </select>
                </div>
                <div className="field">
                  <label>Season folder</label>
                  <select value={seasonFolder ? "yes" : "no"} onChange={(e) => setSeasonFolder(e.target.value === "yes")}>
                    <option value="yes">{tr("Oui", "Yes")}</option>
                    <option value="no">{tr("Non", "No")}</option>
                  </select>
                </div>
                <div className="field">
                  <label>Search missing</label>
                  <select value={searchMissing ? "yes" : "no"} onChange={(e) => setSearchMissing(e.target.value === "yes")}>
                    <option value="yes">{tr("Oui", "Yes")}</option>
                    <option value="no">{tr("Non", "No")}</option>
                  </select>
                </div>
                <div className="field">
                  <label>Search cutoff</label>
                  <select value={searchCutoff ? "yes" : "no"} onChange={(e) => setSearchCutoff(e.target.value === "yes")}>
                    <option value="yes">{tr("Oui", "Yes")}</option>
                    <option value="no">{tr("Non", "No")}</option>
                  </select>
                </div>
              </div>
            ) : modalType === "radarr" ? (
              <div className="formgrid">
                <div className="field">
                  <label>Minimum availability</label>
                  <select value={minAvail} onChange={(e) => setMinAvail(e.target.value)}>
                    <option value="announced">Announced</option>
                    <option value="inCinemas">In Cinemas</option>
                    <option value="released">Released</option>
                  </select>
                </div>
                <div className="field">
                  <label>Search for movie</label>
                  <select value={searchForMovie ? "yes" : "no"} onChange={(e) => setSearchForMovie(e.target.value === "yes")}>
                    <option value="yes">{tr("Oui", "Yes")}</option>
                    <option value="no">{tr("Non", "No")}</option>
                  </select>
                </div>
              </div>
            ) : null}
          </div>
        )}

        {advancedOpen && !isLibraryType && (
          <div className="setup-arr__advanced">
            <div className="muted">
              {tr("Pas d'options avancees pour", "No advanced options for")} {getAppLabel(modalType)}.
            </div>
          </div>
        )}

        <div className="setup-arr__footer">
          <button
            className={`btn btn-accent btn-test${modalPulse === "ok" ? " btn-pulse-ok" : ""}${modalPulse === "error" ? " btn-pulse-err" : ""}`}
            type="button"
            onClick={modalTestState === "ok" ? saveModal : testModal}
            disabled={!canPrimaryModal}
          >
            {modalTesting ? (
              <>
                <span className="btn-spinner" />
                {tr("Test en cours...", "Test in progress...")}
              </>
            ) : modalPulse === "ok" ? (
              tr("Valide", "Valid")
            ) : modalPulse === "error" ? (
              tr("Invalide", "Invalid")
            ) : modalTestState === "ok" ? (
              tr("Sauvegarder", "Save")
            ) : (
              tr("Tester", "Test")
            )}
          </button>
        </div>
      </Modal>
    </div>
  );
}
