import React, { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { apiDelete, apiGet, apiPost, apiPut } from "../../api/client.js";
import ItemRow from "../../ui/ItemRow.jsx";
import Modal from "../../ui/Modal.jsx";
import { getAppLabel, isArrLibraryType } from "../../utils/appTypes.js";

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

  const loadApps = useCallback(async () => {
    setLoading(true);
    setErr("");
    try {
      const list = await apiGet("/api/apps");
      setApps(Array.isArray(list) ? list : []);
    } catch (e) {
      setErr(e?.message || "Erreur chargement applications");
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
        errorMsg = res?.error || "Test échoué";
      }
    } catch (e) {
      errorMsg = e?.message || "Erreur test connexion";
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
        setModalError(errorMsg || "Test échoué");
      }
      triggerPulse(ok ? "ok" : "error");
    }
  }

  async function saveModal() {
    setModalSaving(true);
    setModalError("");
    try {
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
      setModalError(e?.message || "Erreur sauvegarde");
    } finally {
      setModalSaving(false);
    }
  }

  async function deleteApp(app) {
    if (!app?.id) return;
    if (!window.confirm(`Supprimer ${app.name || app.type} ?`)) return;
    try {
      await apiDelete(`/api/apps/${app.id}`);
      await loadApps();
    } catch {}
  }

  const configTags = useMemo(
    () => (Array.isArray(config?.tags) ? config.tags : []),
    [config]
  );
  const isLibraryType = isArrLibraryType(modalType);
  const canUseStoredKey = modalMode === "edit" && modalApp?.id && !modalApiKey.trim();
  const canTestModal = !!modalBaseUrl.trim() && (modalApiKey.trim() || canUseStoredKey) && !modalTesting && !modalSaving;
  const canSaveModal = !!modalBaseUrl.trim() && !modalSaving && !modalTesting;
  const canPrimaryModal = modalTestState === "ok" ? canSaveModal : canTestModal;

  return (
    <div className="setup-step setup-arr">
      <div className="setup-arr__header">
        <div>
          <h2>Applications (Sonarr / Radarr / Overseerr / Jellyseerr / Seer)</h2>
          <p>Optionnel — configure une ou plusieurs apps.</p>
        </div>
        <div className="setup-arr__add settings-row settings-row--ui-select">
          <label>Ajouter</label>
          <select
            className="settings-field"
            value={addType}
            onChange={(e) => {
              const next = e.target.value;
              setAddType("");
              if (next) openAddForType(next);
            }}
          >
            <option value="" disabled>
              Sélectionner...
            </option>
            <option value="sonarr">Sonarr</option>
            <option value="radarr">Radarr</option>
            <option value="overseerr">Overseerr</option>
            <option value="jellyseerr">Jellyseerr</option>
            <option value="seer">Seer</option>
          </select>
        </div>
      </div>

      {err && <div className="onboarding__error">{err}</div>}

      <div className="indexer-list">
        {loading && (
          <div className="indexer-card">
            <div className="indexer-row">
              <span className="indexer-url muted">Chargement...</span>
            </div>
          </div>
        )}

        {!loading && apps.length === 0 && (
          <div className="indexer-card">
            <div className="indexer-row">
              <span className="indexer-url muted">Aucune application configurée</span>
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
                  title: "Éditer",
                  onClick: () => openEdit(app),
                },
                {
                  icon: "delete",
                  title: "Supprimer",
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
        title={modalMode === "add" ? "Ajouter une application" : `Éditer : ${modalApp?.name || modalApp?.type || "Application"}`}
        onClose={closeModal}
        width={620}
      >
        <div className="formgrid">
          {modalMode === "add" && (
            <div className="field">
              <label>Type</label>
              <select value={modalType} onChange={(e) => setModalType(e.target.value)}>
                <option value="sonarr">Sonarr</option>
                <option value="radarr">Radarr</option>
                <option value="overseerr">Overseerr</option>
                <option value="jellyseerr">Jellyseerr</option>
                <option value="seer">Seer</option>
              </select>
            </div>
          )}
          <div className="field">
            <label>Nom</label>
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
              placeholder={modalType === "sonarr" ? "http://192.168.1.x:8989 ou https://sonarr.domain.com" : modalType === "radarr" ? "http://192.168.1.x:7878 ou https://radarr.domain.com" : "http://192.168.1.x:5055 ou https://overseerr.domain.com"}
              disabled={modalSaving || modalTesting}
            />
            <span className="field-hint">IP, hostname ou URL reverse proxy (http/https)</span>
          </div>
          <div className="field">
            <label>Clé API</label>
            <input
              value={modalApiKey}
              onChange={(e) => setModalApiKey(e.target.value)}
              placeholder={modalMode === "edit" ? "••••••••••••••••" : "Entrez la clé API"}
              disabled={modalSaving || modalTesting}
            />
          </div>
        </div>

        <div className="setup-arr__modal-actions">
          {isLibraryType && (
            <button className="btn" type="button" onClick={() => setAdvancedOpen((v) => !v)}>
              {advancedOpen ? "Masquer options avancées" : "Options avancées"}
            </button>
          )}
        </div>

        {modalError && <div className="onboarding__error">{modalError}</div>}
        {modalTestState === "ok" && <div className="onboarding__ok">Connexion OK</div>}

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
                  {configLoading ? "Chargement..." : "Charger la config"}
                </button>
              </div>
            )}

            <div className="formgrid">
              <div className="field">
                <label>Root folder</label>
                {config?.rootFolders?.length > 0 ? (
                  <select value={rootFolder} onChange={(e) => setRootFolder(e.target.value)}>
                    <option value="" disabled>Sélectionner...</option>
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
                    <option value="" disabled>Sélectionner...</option>
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
                    <option value="yes">Oui</option>
                    <option value="no">Non</option>
                  </select>
                </div>
                <div className="field">
                  <label>Search missing</label>
                  <select value={searchMissing ? "yes" : "no"} onChange={(e) => setSearchMissing(e.target.value === "yes")}>
                    <option value="yes">Oui</option>
                    <option value="no">Non</option>
                  </select>
                </div>
                <div className="field">
                  <label>Search cutoff</label>
                  <select value={searchCutoff ? "yes" : "no"} onChange={(e) => setSearchCutoff(e.target.value === "yes")}>
                    <option value="yes">Oui</option>
                    <option value="no">Non</option>
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
                    <option value="yes">Oui</option>
                    <option value="no">Non</option>
                  </select>
                </div>
              </div>
            ) : null}
          </div>
        )}

        {advancedOpen && !isLibraryType && (
          <div className="setup-arr__advanced">
            <div className="muted">
              Pas d&apos;options avancées pour {getAppLabel(modalType)}.
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
