import React, { useEffect, useMemo, useRef, useState } from "react";
import Modal from "../../ui/Modal.jsx";
import ItemRow from "../../ui/ItemRow.jsx";
import { fmtBytes } from "./settingsUtils.js";
import AppIcon from "../../ui/AppIcon.jsx";
import ToggleSwitch from "../../ui/ToggleSwitch.jsx";
import { getAppLabel, isArrLibraryType, normalizeRequestMode } from "../../utils/appTypes.js";

export default function SettingsApplications({
  arrApps,
  availableAddTypes,
  arrAppsLoading,
  arrTestingId,
  arrTestStatusById,
  arrSyncingId,
  arrSyncStatusById,
  arrSyncing,
  arrSyncSettings,
  arrSyncStatus,
  arrSyncSaving,
  arrRequestModeDraft,
  arrPulseKeys,
  isRequestModeDirty,
  hasEnabledArrApps,
  syncArrApp,
  testArrApp,
  openArrModalEdit,
  openArrDelete,
  toggleArrEnabled,
  setArrSyncSettings,
  setArrRequestModeDraft,
  saveArrSyncSettings,
  optionsModalOpen,
  closeOptionsModal,
  arrModalOpen,
  arrModalMode,
  arrModalApp,
  arrModalType,
  arrModalName,
  arrModalBaseUrl,
  arrModalApiKey,
  arrModalTesting,
  arrModalTested,
  arrModalError,
  arrModalSaving,
  arrModalAdvanced,
  arrModalConfig,
  arrModalConfigLoading,
  arrModalAdvancedInitial,
  arrModalRootFolder,
  arrModalQualityProfile,
  arrModalSeriesType,
  arrModalSeasonFolder,
  arrModalMonitorMode,
  arrModalSearchMissing,
  arrModalSearchCutoff,
  arrModalMinAvail,
  arrModalSearchForMovie,
  setArrModalType,
  setArrModalName,
  setArrModalBaseUrl,
  setArrModalApiKey,
  setArrModalTested,
  setArrModalError,
  setArrModalRootFolder,
  setArrModalQualityProfile,
  setArrModalSeriesType,
  setArrModalSeasonFolder,
  setArrModalMonitorMode,
  setArrModalSearchMissing,
  setArrModalSearchCutoff,
  setArrModalMinAvail,
  setArrModalSearchForMovie,
  setArrModalAdvanced,
  setArrModalAdvancedInitial,
  closeArrModal,
  testArrModal,
  saveArrModal,
  arrDeleteOpen,
  arrDeleteApp,
  arrDeleteLoading,
  confirmArrDelete,
  closeArrDelete,
}) {
  const hasApps = (arrApps || []).length > 0;
  const noAvailableAddTypes = (availableAddTypes || []).length === 0;
  const [syncModalInitial, setSyncModalInitial] = useState(null);
  const [optionPulseKeys, setOptionPulseKeys] = useState(() => new Set());
  const optionPulseTimerRef = useRef(null);

  useEffect(() => {
    if (!optionsModalOpen) return;
    setSyncModalInitial({
      arrAutoSyncEnabled: !!arrSyncSettings.arrAutoSyncEnabled,
      arrSyncIntervalMinutes: Number(arrSyncSettings.arrSyncIntervalMinutes ?? 60),
    });
  }, [optionsModalOpen]);

  useEffect(() => {
    return () => {
      if (optionPulseTimerRef.current) clearTimeout(optionPulseTimerRef.current);
    };
  }, []);

  const isSyncDirty = useMemo(() => {
    if (!syncModalInitial) return false;
    return (
      !!arrSyncSettings.arrAutoSyncEnabled !== syncModalInitial.arrAutoSyncEnabled
      || Number(arrSyncSettings.arrSyncIntervalMinutes ?? 60) !== syncModalInitial.arrSyncIntervalMinutes
    );
  }, [arrSyncSettings.arrAutoSyncEnabled, arrSyncSettings.arrSyncIntervalMinutes, syncModalInitial]);

  const isOptionsDirty = isRequestModeDirty || isSyncDirty;

  const optionPulseClass = (key) => (optionPulseKeys.has(key) ? " pulse-ok" : "");

  async function handleSaveOptionsModal() {
    if (!isOptionsDirty || arrSyncSaving) return;
    const changed = new Set();
    if (isRequestModeDirty) changed.add("arr.requestIntegrationMode");
    if (syncModalInitial) {
      if (!!arrSyncSettings.arrAutoSyncEnabled !== syncModalInitial.arrAutoSyncEnabled) {
        changed.add("arr.arrAutoSyncEnabled");
      }
      if (Number(arrSyncSettings.arrSyncIntervalMinutes ?? 60) !== syncModalInitial.arrSyncIntervalMinutes) {
        changed.add("arr.arrSyncIntervalMinutes");
      }
    }

    await saveArrSyncSettings({
      ...arrSyncSettings,
      requestIntegrationMode: arrRequestModeDraft,
    });

    if (changed.size > 0) {
      if (optionPulseTimerRef.current) clearTimeout(optionPulseTimerRef.current);
      setOptionPulseKeys(new Set(changed));
      optionPulseTimerRef.current = setTimeout(() => {
        setOptionPulseKeys(new Set());
      }, 1200);
    }

    setSyncModalInitial({
      arrAutoSyncEnabled: !!arrSyncSettings.arrAutoSyncEnabled,
      arrSyncIntervalMinutes: Number(arrSyncSettings.arrSyncIntervalMinutes ?? 60),
    });
  }

  return (
    <>
      <div className="indexer-list itemrow-grid">
        {arrAppsLoading ? (
          <div className="indexer-card">
            <div className="indexer-row">
              <span className="indexer-url muted">Chargement...</span>
            </div>
          </div>
        ) : arrApps.length === 0 ? (
          <div className="indexer-card">
            <div className="indexer-row">
              <span className="indexer-url muted">Aucune application configurée</span>
            </div>
          </div>
        ) : (
          arrApps.map((app, idx) => {
            const appLabel = getAppLabel(app.type);
            const testStatus = arrTestStatusById[app.id];
            const syncStatus = arrSyncStatusById[app.id];
            const isTesting = arrTestingId === app.id;
            const isSyncing = arrSyncingId === app.id || syncStatus === "pending";
            const isBusy = isTesting || isSyncing || arrSyncing;

            const statusClass = [
              testStatus === "ok" && "test-ok",
              testStatus === "error" && "test-err",
              syncStatus === "ok" && "sync-ok",
              syncStatus === "error" && "sync-err",
            ].filter(Boolean).join(" ");

            const badges = [
              { label: appLabel },
              {
                label: app.hasApiKey ? "API OK" : "NO KEY",
                className: app.hasApiKey ? "pill-ok" : "pill-warn",
              },
            ];
            if (app.isDefault) {
              badges.push({ label: "Par défaut", className: "pill--accent" });
            }

            return (
              <ItemRow
                key={app.id}
                id={idx + 1}
                title={app.name || appLabel}
                meta={app.baseUrl || ""}
                enabled={app.isEnabled}
                statusClass={statusClass}
                badges={badges}
                actions={[
                  {
                    icon: "sync",
                    title: isSyncing ? "Sync en cours..." : "Sync",
                    onClick: () => syncArrApp(app),
                    disabled: isBusy || !app.isEnabled,
                    spinning: isSyncing,
                  },
                  {
                    icon: "science",
                    title: isTesting ? "Test en cours..." : "Test",
                    onClick: () => testArrApp(app.id),
                    disabled: isBusy || !app.isEnabled,
                    spinning: isTesting,
                  },
                  {
                    icon: "edit",
                    title: "Éditer",
                    onClick: () => openArrModalEdit(app),
                    disabled: isBusy || !app.isEnabled,
                  },
                  {
                    icon: "delete",
                    title: "Supprimer",
                    onClick: () => openArrDelete(app),
                    disabled: isBusy || !app.isEnabled,
                    className: "iconbtn--danger",
                  },
                ]}
                showToggle
                onToggle={() => toggleArrEnabled(app)}
                toggleDisabled={isBusy}
              />
            );
          })
        )}
      </div>

      <Modal
        open={optionsModalOpen}
        title="Options applications"
        onClose={closeOptionsModal}
        width={560}
      >
        <div style={{ padding: 12 }}>
          {!hasApps ? (
            <div className="muted">Aucune application configurée.</div>
          ) : (
            <>
              <div className="settings-card" id="request-integration">
                <div className="settings-card__title">Mode d&apos;envoi</div>
                <div className="indexer-list">
                  <div className={`indexer-card${arrPulseKeys?.has("arr.requestIntegrationMode") ? " pulse-ok" : ""}${optionPulseClass("arr.requestIntegrationMode")}`}>
                    <div className="indexer-row indexer-row--settings">
                      <span className="indexer-title">Intégration active</span>
                      <div className="indexer-actions">
                        {isRequestModeDirty && (
                          <span className="indexer-status">À sauvegarder</span>
                        )}
                        <select
                          value={arrRequestModeDraft}
                          onChange={(e) => setArrRequestModeDraft(normalizeRequestMode(e.target.value))}
                          disabled={arrSyncSaving}
                        >
                          <option value="arr">Sonarr/Radarr</option>
                          <option value="overseerr">Overseerr</option>
                          <option value="jellyseerr">Jellyseerr</option>
                          <option value="seer">Seer</option>
                        </select>
                      </div>
                    </div>
                  </div>
                </div>
              </div>

              <div className="settings-card" id="arr-sync" style={{ marginTop: 16 }}>
                <div className="settings-card__title">Synchronisation</div>
                <div className="indexer-list">
                  <div className={`indexer-card${optionPulseClass("arr.arrAutoSyncEnabled")}${!hasEnabledArrApps ? " is-disabled" : ""}`}>
                    <div className="indexer-row indexer-row--settings">
                      <span className="indexer-title">Synchronisation Automatique</span>
                      <div className="indexer-actions">
                        <span className="indexer-status">
                          {arrSyncSettings.arrAutoSyncEnabled ? "Actif" : "Desactive"}
                        </span>
                        <ToggleSwitch
                          checked={arrSyncSettings.arrAutoSyncEnabled}
                          onIonChange={(e) => {
                            const enabled = e.detail.checked;
                            const updated = { ...arrSyncSettings, arrAutoSyncEnabled: enabled };
                            setArrSyncSettings(updated);
                          }}
                          className="settings-toggle"
                          disabled={!hasEnabledArrApps || arrSyncSaving}
                        />
                      </div>
                    </div>
                  </div>
                  <div className={`indexer-card${optionPulseClass("arr.arrSyncIntervalMinutes")}${(!hasEnabledArrApps || !arrSyncSettings.arrAutoSyncEnabled) ? " is-disabled" : ""}`}>
                    <div className="indexer-row indexer-row--settings">
                      <span className="indexer-title">Intervalle Sync (minutes)</span>
                      <div className="indexer-actions">
                        <input
                          type="number"
                          min={1}
                          max={1440}
                          value={arrSyncSettings.arrSyncIntervalMinutes}
                          onChange={(e) => {
                            const val = Math.max(1, Math.min(1440, Number(e.target.value) || 10));
                            setArrSyncSettings((prev) => ({ ...prev, arrSyncIntervalMinutes: val }));
                          }}
                          disabled={!arrSyncSettings.arrAutoSyncEnabled || !hasEnabledArrApps}
                        />
                      </div>
                    </div>
                  </div>
                </div>

                {arrSyncStatus.length > 0 && (
                  <div className="indexer-list" style={{ marginTop: 16 }}>
                    <div className="settings-card__title" style={{ fontSize: 14, marginBottom: 8 }}>
                      État de synchronisation
                    </div>
                    {arrSyncStatus.map((status) => {
                      const hasError = !!status.lastError;
                      let lastSyncDisplay = "Jamais";
                      if (status.lastSyncAt) {
                        const syncDate = new Date(status.lastSyncAt);
                        const now = new Date();
                        const isToday = syncDate.toDateString() === now.toDateString();
                        const timeStr = syncDate.toLocaleTimeString("fr-FR", { hour: "2-digit", minute: "2-digit" });
                        if (isToday) {
                          lastSyncDisplay = timeStr;
                        } else {
                          const dateStr = syncDate.toLocaleDateString("fr-FR", { day: "2-digit", month: "2-digit" });
                          lastSyncDisplay = `${dateStr} ${timeStr}`;
                        }
                      }
                      return (
                        <div
                          key={status.appId}
                          className={`indexer-card ${!status.isEnabled ? "is-disabled" : ""}`}
                        >
                          <div className="indexer-row indexer-row--settings">
                            <span className={`dot ${hasError ? "warn" : status.isEnabled ? "ok" : "off"}`} />
                            <span className="indexer-title">
                              {status.appName || `App ${status.appId}`}
                              <span className="muted" style={{ marginLeft: 8, fontSize: 12 }}>
                                ({getAppLabel(status.appType)})
                              </span>
                            </span>
                            <div className="indexer-actions">
                              <span className="indexer-url muted">
                                Dernier sync: {lastSyncDisplay}
                              </span>
                              {status.lastSyncCount > 0 && (
                                <span className="pill">
                                  {status.lastSyncCount} {(isArrLibraryType(status.appType) ? "items" : "demandes")}
                                </span>
                              )}
                              {hasError && (
                                <span className="pill pill-warn">
                                  Erreur
                                </span>
                              )}
                            </div>
                          </div>
                          {hasError && (
                            <div className="settings-help" style={{ color: "var(--color-warn)" }}>
                              {status.lastError}
                            </div>
                          )}
                        </div>
                      );
                    })}
                  </div>
                )}
              </div>

              {isOptionsDirty && (
                <div className="formactions" style={{ marginTop: 16 }}>
                  <button
                    className="btn btn-accent"
                    type="button"
                    onClick={handleSaveOptionsModal}
                    disabled={arrSyncSaving}
                  >
                    {arrSyncSaving ? "Enregistrement..." : "Enregistrer"}
                  </button>
                </div>
              )}
            </>
          )}
        </div>
      </Modal>

      <Modal
        open={arrModalOpen}
        title={arrModalMode === "add" ? "Ajouter une application" : `Éditer : ${arrModalApp?.name || arrModalApp?.type || "Application"}`}
        onClose={closeArrModal}
        width={560}
      >
        <div style={{ padding: 12 }}>
          {arrModalMode === "add" && (
            <div className="field" style={{ marginBottom: 12 }}>
              <label className="muted">Type</label>
              <select
                value={arrModalType}
                onChange={(e) => {
                  setArrModalType(e.target.value);
                  setArrModalTested(false);
                  setArrModalError("");
                }}
                disabled={arrModalTesting || arrModalSaving}
              >
                {noAvailableAddTypes && (
                  <option value="">Aucun type disponible</option>
                )}
                {(availableAddTypes || []).map((type) => (
                  <option key={type} value={type}>{getAppLabel(type)}</option>
                ))}
              </select>
            </div>
          )}

          <div className="field" style={{ marginBottom: 12 }}>
            <label className="muted">Nom (optionnel)</label>
            <input
              type="text"
              value={arrModalName}
              onChange={(e) => setArrModalName(e.target.value)}
              placeholder={`Mon ${getAppLabel(arrModalType)}`}
              disabled={arrModalTesting || arrModalSaving}
            />
          </div>

          <div className="field" style={{ marginBottom: 12 }}>
            <label className="muted">URL</label>
            <input
              type="text"
              value={arrModalBaseUrl}
              onChange={(e) => {
                setArrModalBaseUrl(e.target.value);
                setArrModalTested(false);
                setArrModalError("");
              }}
              placeholder={arrModalType === "sonarr" ? "http://192.168.1.x:8989 ou https://sonarr.domain.com" : arrModalType === "radarr" ? "http://192.168.1.x:7878 ou https://radarr.domain.com" : "http://192.168.1.x:5055 ou https://overseerr.domain.com"}
              disabled={arrModalTesting || arrModalSaving}
            />
            <span className="field-hint">IP, hostname ou URL reverse proxy (http/https)</span>
          </div>

          <div className="field" style={{ marginBottom: 12 }}>
            <label className="muted">
              Clé API
              {arrModalMode === "edit" && arrModalApp?.hasApiKey && (
                <span style={{ fontStyle: "italic", marginLeft: 6 }}>(laisser vide pour conserver)</span>
              )}
            </label>
            <input
              type="password"
              value={arrModalApiKey}
              onChange={(e) => {
                setArrModalApiKey(e.target.value);
                setArrModalTested(false);
                setArrModalError("");
              }}
              placeholder={arrModalMode === "edit" && arrModalApp?.hasApiKey ? "••••••••••••••••" : "Entrez la clé API"}
              disabled={arrModalTesting || arrModalSaving}
            />
          </div>

          {arrModalError && (
            <div className="onboarding__error" style={{ marginBottom: 12 }}>
              {arrModalError}
            </div>
          )}

          {arrModalTested && (
            <div className="onboarding__ok" style={{ marginBottom: 12 }}>
              Connexion réussie ! Vous pouvez enregistrer.
            </div>
          )}

          {arrModalMode === "add" && !arrModalTested && (
            <div className="muted" style={{ marginBottom: 12 }}>
              {noAvailableAddTypes
                ? "Toutes les applications sont déjà ajoutées."
                : "Testez d'abord la connexion avant d'enregistrer."}
            </div>
          )}

          <div className="formactions">
            {arrModalMode === "add" && !arrModalTested ? (
              <button
                className="btn btn-accent"
                type="button"
                onClick={testArrModal}
                disabled={noAvailableAddTypes || !arrModalBaseUrl.trim() || !arrModalApiKey.trim() || arrModalTesting}
              >
                {arrModalTesting ? (
                  <>
                    <AppIcon name="progress_activity" className="iconbtn--spin" size={16} style={{ marginRight: 6 }} />
                    Test en cours...
                  </>
                ) : "Tester"}
              </button>
            ) : (
              <>
                {isArrLibraryType(arrModalType) && (
                  <button
                    className="btn"
                    type="button"
                    onClick={() => {
                      setArrModalAdvancedInitial({
                        rootFolder: arrModalRootFolder,
                        qualityProfile: arrModalQualityProfile,
                        seriesType: arrModalSeriesType,
                        seasonFolder: arrModalSeasonFolder,
                        monitorMode: arrModalMonitorMode,
                        searchMissing: arrModalSearchMissing,
                        searchCutoff: arrModalSearchCutoff,
                        minAvail: arrModalMinAvail,
                        searchForMovie: arrModalSearchForMovie,
                      });
                      setArrModalAdvanced(true);
                    }}
                    disabled={arrModalSaving}
                  >
                    Options avancées
                  </button>
                )}
                <button
                  className="btn btn-accent"
                  type="button"
                  onClick={saveArrModal}
                  disabled={
                    arrModalSaving ||
                    !arrModalBaseUrl.trim() ||
                    (arrModalMode === "add" && (noAvailableAddTypes || !arrModalApiKey.trim()))
                  }
                >
                  {arrModalSaving ? (
                    <>
                      <AppIcon name="progress_activity" className="iconbtn--spin" size={16} style={{ marginRight: 6 }} />
                      Enregistrement...
                    </>
                  ) : "Enregistrer"}
                </button>
              </>
            )}
            <button
              className="btn"
              type="button"
              onClick={closeArrModal}
              disabled={arrModalTesting || arrModalSaving}
            >
              Annuler
            </button>
          </div>
        </div>
      </Modal>

      <Modal
        open={arrModalAdvanced}
        title="Options avancées"
        onClose={() => setArrModalAdvanced(false)}
        width={520}
      >
        <div style={{ padding: 12 }}>
          {arrModalConfigLoading ? (
            <div className="muted" style={{ textAlign: "center", padding: 20 }}>
              <AppIcon name="progress_activity" className="iconbtn--spin" size={24} />
              <div style={{ marginTop: 8 }}>Chargement de la configuration...</div>
            </div>
          ) : (
            <>
              <div className="field" style={{ marginBottom: 12 }}>
                <label className="muted">Dossier racine</label>
                {arrModalConfig?.rootFolders?.length > 0 ? (
                  <select
                    value={arrModalRootFolder}
                    onChange={(e) => setArrModalRootFolder(e.target.value)}
                  >
                    <option value="">-- Sélectionner --</option>
                    {arrModalConfig.rootFolders.map((rf) => (
                      <option key={rf.id} value={rf.path}>
                        {rf.path} ({fmtBytes(rf.freeSpace)} libre)
                      </option>
                    ))}
                  </select>
                ) : (
                  <input
                    type="text"
                    value={arrModalRootFolder}
                    onChange={(e) => setArrModalRootFolder(e.target.value)}
                    placeholder="/media/series"
                  />
                )}
              </div>

              <div className="field" style={{ marginBottom: 12 }}>
                <label className="muted">Profil qualité</label>
                {arrModalConfig?.qualityProfiles?.length > 0 ? (
                  <select
                    value={arrModalQualityProfile}
                    onChange={(e) => setArrModalQualityProfile(e.target.value)}
                  >
                    <option value="">-- Sélectionner --</option>
                    {arrModalConfig.qualityProfiles.map((qp) => (
                      <option key={qp.id} value={qp.id}>
                        {qp.name}
                      </option>
                    ))}
                  </select>
                ) : (
                  <input
                    type="number"
                    value={arrModalQualityProfile}
                    onChange={(e) => setArrModalQualityProfile(e.target.value)}
                    placeholder="ID du profil"
                  />
                )}
              </div>

              {arrModalType === "sonarr" && (
                <>
                  <div className="field" style={{ marginBottom: 12 }}>
                    <label className="muted">Type de série</label>
                    <select
                      value={arrModalSeriesType}
                      onChange={(e) => setArrModalSeriesType(e.target.value)}
                    >
                      <option value="standard">Standard</option>
                      <option value="daily">Daily</option>
                      <option value="anime">Anime</option>
                    </select>
                  </div>

                  <div className="field" style={{ marginBottom: 12 }}>
                    <label className="muted">Mode de surveillance</label>
                    <select
                      value={arrModalMonitorMode}
                      onChange={(e) => setArrModalMonitorMode(e.target.value)}
                    >
                      <option value="all">Tous les épisodes</option>
                      <option value="future">Futurs épisodes</option>
                      <option value="missing">Épisodes manquants</option>
                      <option value="existing">Épisodes existants</option>
                      <option value="firstSeason">Première saison</option>
                      <option value="lastSeason">Dernière saison</option>
                      <option value="pilot">Pilote</option>
                      <option value="none">Aucun</option>
                    </select>
                  </div>

                  <div className="indexer-card" style={{ marginBottom: 8 }}>
                    <div className="indexer-row indexer-row--settings">
                      <span className="indexer-title">Dossiers par saison</span>
                      <div className="indexer-actions">
                        <ToggleSwitch
                          checked={arrModalSeasonFolder}
                          onIonChange={(e) => setArrModalSeasonFolder(e.detail.checked)}
                          className="settings-toggle"
                        />
                      </div>
                    </div>
                  </div>

                  <div className="indexer-card" style={{ marginBottom: 8 }}>
                    <div className="indexer-row indexer-row--settings">
                      <span className="indexer-title">Rechercher épisodes manquants</span>
                      <div className="indexer-actions">
                        <ToggleSwitch
                          checked={arrModalSearchMissing}
                          onIonChange={(e) => setArrModalSearchMissing(e.detail.checked)}
                          className="settings-toggle"
                        />
                      </div>
                    </div>
                  </div>

                  <div className="indexer-card" style={{ marginBottom: 8 }}>
                    <div className="indexer-row indexer-row--settings">
                      <span className="indexer-title">Rechercher cutoff non atteint</span>
                      <div className="indexer-actions">
                        <ToggleSwitch
                          checked={arrModalSearchCutoff}
                          onIonChange={(e) => setArrModalSearchCutoff(e.detail.checked)}
                          className="settings-toggle"
                        />
                      </div>
                    </div>
                  </div>
                </>
              )}

              {arrModalType === "radarr" && (
                <>
                  <div className="field" style={{ marginBottom: 12 }}>
                    <label className="muted">Disponibilité minimale</label>
                    <select
                      value={arrModalMinAvail}
                      onChange={(e) => setArrModalMinAvail(e.target.value)}
                    >
                      <option value="announced">Annoncé</option>
                      <option value="inCinemas">En salle</option>
                      <option value="released">Sorti</option>
                    </select>
                  </div>

                  <div className="indexer-card" style={{ marginBottom: 8 }}>
                    <div className="indexer-row indexer-row--settings">
                      <span className="indexer-title">Rechercher le film à l'ajout</span>
                      <div className="indexer-actions">
                        <ToggleSwitch
                          checked={arrModalSearchForMovie}
                          onIonChange={(e) => setArrModalSearchForMovie(e.detail.checked)}
                          className="settings-toggle"
                        />
                      </div>
                    </div>
                  </div>
                </>
              )}
            </>
          )}

          <div className="formactions" style={{ marginTop: 16 }}>
            <button
              className="btn btn-accent"
              type="button"
              onClick={() => setArrModalAdvanced(false)}
              disabled={
                arrModalAdvancedInitial &&
                arrModalRootFolder === arrModalAdvancedInitial.rootFolder &&
                arrModalQualityProfile === arrModalAdvancedInitial.qualityProfile &&
                arrModalSeriesType === arrModalAdvancedInitial.seriesType &&
                arrModalSeasonFolder === arrModalAdvancedInitial.seasonFolder &&
                arrModalMonitorMode === arrModalAdvancedInitial.monitorMode &&
                arrModalSearchMissing === arrModalAdvancedInitial.searchMissing &&
                arrModalSearchCutoff === arrModalAdvancedInitial.searchCutoff &&
                arrModalMinAvail === arrModalAdvancedInitial.minAvail &&
                arrModalSearchForMovie === arrModalAdvancedInitial.searchForMovie
              }
            >
              Enregistrer
            </button>
            <button
              className="btn"
              type="button"
              onClick={() => {
                if (arrModalAdvancedInitial) {
                  setArrModalRootFolder(arrModalAdvancedInitial.rootFolder);
                  setArrModalQualityProfile(arrModalAdvancedInitial.qualityProfile);
                  setArrModalSeriesType(arrModalAdvancedInitial.seriesType);
                  setArrModalSeasonFolder(arrModalAdvancedInitial.seasonFolder);
                  setArrModalMonitorMode(arrModalAdvancedInitial.monitorMode);
                  setArrModalSearchMissing(arrModalAdvancedInitial.searchMissing);
                  setArrModalSearchCutoff(arrModalAdvancedInitial.searchCutoff);
                  setArrModalMinAvail(arrModalAdvancedInitial.minAvail);
                  setArrModalSearchForMovie(arrModalAdvancedInitial.searchForMovie);
                }
                setArrModalAdvanced(false);
              }}
            >
              Retour
            </button>
          </div>
        </div>
      </Modal>

      <Modal
        open={arrDeleteOpen}
        title="Supprimer l'application"
        onClose={closeArrDelete}
        width={520}
      >
        <div style={{ padding: 12 }}>
          <div style={{ fontWeight: 700, marginBottom: 8 }}>
            Confirmer la suppression ?
          </div>
          <div className="muted" style={{ marginBottom: 12 }}>
            Cette action va supprimer l'application{" "}
            <strong>{arrDeleteApp?.name || arrDeleteApp?.type || "-"}</strong>.
            <br />
            Cette action est définitive.
          </div>
          <div className="formactions">
            <button
              className="btn btn-danger"
              type="button"
              onClick={confirmArrDelete}
              disabled={arrDeleteLoading}
            >
              {arrDeleteLoading ? "Suppression..." : "Supprimer"}
            </button>
            <button
              className="btn"
              type="button"
              onClick={closeArrDelete}
              disabled={arrDeleteLoading}
            >
              Annuler
            </button>
          </div>
        </div>
      </Modal>
    </>
  );
}
