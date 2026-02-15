import React, { useEffect } from "react";
import SystemStatistics from "./system/SystemStatistics.jsx";
import { useLocation } from "react-router-dom";
import { useSubbarSetter } from "../layout/useSubbar.js";
import SubAction from "../ui/SubAction.jsx";
import Loader from "../ui/Loader.jsx";
import useSystemController from "./system/useSystemController.js";
import { fmtBytes, fmtMs, fmtTs, fmtUptime } from "./system/systemUtils.js";

function fmtVersion(value) {
  const raw = String(value || "").trim();
  if (!raw) return "-";
  const match = raw.match(/(\d+)\.(\d+)\.(\d+)/);
  if (!match) return raw;
  return `v${match[1]}.${match[2]}.${match[3]}`;
}

export default function System() {
  const setContent = useSubbarSetter();
  const location = useLocation();
  const section = location.pathname.split("/")[2] || "overview";
  const {
    systemTitle,
    showStorage,
    showProviders,
    showOverview,
    showStatistics,
    loading,
    err,
    status,
    external,
    providerStats,
    missingPosterCount,
    storageInfo,
    load,
    matchingPercent,
    matchingColor,
  } = useSystemController(section);

  useEffect(() => {
    setContent(
      <>
        <SubAction icon="refresh" label="Rafraîchir" onClick={load} />
        <SubAction icon="dns" label="Status" disabled />
      </>
    );
    return () => setContent(null);
  }, [setContent, load]);

  return (
    <div className="page page--system">
      <div className="pagehead">
        <div>
          <h1>{systemTitle}</h1>
          <div className="muted">Configuration de l'application</div>
        </div>
      </div>
      <div className="pagehead__divider" />


      {loading && <Loader label="Chargement du statut système…" />}

      {!loading && err && (
        <div className="errorbox">
          <div className="errorbox__title">Attention</div>
          <div className="muted">{err}</div>
        </div>
      )}

      {!loading && (
        <div className="settings-grid">
          {showOverview && (
            <div className="settings-card--wide">
              <div className="card-row card-row-fourth system-overview-cards">
                <div className="card card-fourth">
                  <div className="card-title">Application</div>
                  <div className="card-value">{status?.appName || "Feedarr"}</div>
                  <div className="card-meta">
                    <div className="card-meta__row">
                      <span className="card-meta__label">Version</span>
                      <span className="card-meta__value">{fmtVersion(status?.version)}</span>
                    </div>
                    <div className="card-meta__row">
                      <span className="card-meta__label">Environnement</span>
                      <span className="card-meta__value">{status?.environment || "-"}</span>
                    </div>
                    <div className="card-meta__row">
                      <span className="card-meta__label">Base de données</span>
                      <span className="card-meta__value">
                        {status?.dbSizeMB ? `SQLite (${status.dbSizeMB} Mo)` : "SQLite"}
                      </span>
                    </div>
                    <div className="card-meta__row">
                      <span className="card-meta__label">Dossier data</span>
                      <span className="card-meta__value">{status?.dataDir || "-"}</span>
                    </div>
                    <div className="card-meta__row">
                      <span className="card-meta__label">Uptime</span>
                      <span className="card-meta__value">{fmtUptime(status?.uptimeSeconds)}</span>
                    </div>
                  </div>
                </div>
                <div className="card card-fourth">
                  <div className="card-title">Version</div>
                  <div className="card-value">{fmtVersion(status?.version)}</div>
                  <div className="card-meta">
                    <div className="card-meta__row">
                      <span className="card-meta__label">Sources</span>
                      <span className="card-meta__value">{status?.sourcesCount ?? 0}</span>
                    </div>
                    <div className="card-meta__row">
                      <span className="card-meta__label">Releases</span>
                      <span className="card-meta__value">{status?.releasesCount ?? 0}</span>
                    </div>
                  </div>
                </div>
                <div className="card card-fourth">
                  <div className="card-title">Environnement</div>
                  <div className="card-value">{status?.environment || "-"}</div>
                  <div className="card-meta">
                    <div className="card-meta__row">
                      <span className="card-meta__label">Dossier data</span>
                      <span className="card-meta__value">{status?.dataDir || "-"}</span>
                    </div>
                    <div className="card-meta__row">
                      <span className="card-meta__label">Chemin DB</span>
                      <span className="card-meta__value">{status?.dbPath || "-"}</span>
                    </div>
                  </div>
                </div>
                <div className="card card-fourth">
                  <div className="card-title">Uptime</div>
                  <div className="card-value">{fmtUptime(status?.uptimeSeconds)}</div>
                  <div className="card-meta">
                    <div className="card-meta__row">
                      <span className="card-meta__label">Dernier sync</span>
                      <span className="card-meta__value">{fmtTs(status?.lastSyncAtTs)}</span>
                    </div>
                    <div className="card-meta__row">
                      <span className="card-meta__label">Dernière release</span>
                      <span className="card-meta__value">{fmtTs(status?.releasesLatestTs)}</span>
                    </div>
                  </div>
                </div>
              </div>
            </div>
          )}

          {showStatistics && (
            <SystemStatistics />
          )}

          {showStorage && (
            <>
              <div className="card-row card-row-third" style={{ gridColumn: "1 / -1", marginBottom: 20 }}>
                <div className="card card-third">
                  <div className="card-title">Base de données</div>
                  <div className="card-value">{fmtBytes(storageInfo.usage?.databaseBytes || 0)}</div>
                </div>
                <div className="card card-third">
                  <div className="card-title">Posters</div>
                  <div className="card-value">{fmtBytes(storageInfo.usage?.postersBytes || 0)}</div>
                  <div className="muted">{storageInfo.usage?.postersCount || 0} fichiers</div>
                </div>
                <div className="card card-third">
                  <div className="card-title">Sauvegardes</div>
                  <div className="card-value">{fmtBytes(storageInfo.usage?.backupsBytes || 0)}</div>
                  <div className="muted">{storageInfo.usage?.backupsCount || 0} fichiers</div>
                </div>
              </div>

              <div className="settings-card settings-card--full" id="storage-volumes">
                <div className="settings-card__title">Espace disque</div>
                <div className="storage-table stats-table">
                  <div className="thead">
                    <div className="th">Emplacement</div>
                    <div className="th th-right">Espace libre</div>
                    <div className="th th-right">Espace total</div>
                    <div className="th th-progress">Utilisation</div>
                  </div>
                  {(storageInfo.volumes || []).length === 0 ? (
                    <div className="trow">
                      <div className="td td-path"><span className="muted">Aucun volume détecté</span></div>
                      <div className="td td-right">-</div>
                      <div className="td td-right">-</div>
                      <div className="td td-progress">-</div>
                    </div>
                  ) : (
                    (storageInfo.volumes || []).map((vol) => {
                      const usedPercent = vol.totalBytes > 0
                        ? Math.round(((vol.totalBytes - vol.freeBytes) / vol.totalBytes) * 100)
                        : 0;
                      const isWarning = usedPercent >= 80;
                      const isCritical = usedPercent >= 95;
                      return (
                        <div className="trow" key={vol.path}>
                          <div className="td td-path">{vol.path}</div>
                          <div className="td td-right">{fmtBytes(vol.freeBytes)}</div>
                          <div className="td td-right">{fmtBytes(vol.totalBytes)}</div>
                          <div className="td td-progress">
                            <div className={`storage-bar${isWarning ? " storage-bar--warn" : ""}${isCritical ? " storage-bar--critical" : ""}`}>
                              <div
                                className="storage-bar__fill"
                                style={{ width: `${usedPercent}%` }}
                              />
                            </div>
                            <span className="storage-usage">{usedPercent}%</span>
                          </div>
                        </div>
                      );
                    })
                  )}
                </div>
              </div>
            </>
          )}

          {showProviders && (
            <>
              <div className="card-row card-row-third" style={{ gridColumn: "1 / -1" }}>
                <div className="card card-third">
                  <div className="card-title">Dernier sync</div>
                  <div className="card-value">{fmtTs(status?.lastSyncAtTs)}</div>
                </div>
                <div className="card card-third">
                  <div className="card-title">Matching</div>
                  <div className="card-value" style={{ color: matchingColor }}>
                    {matchingPercent}%
                  </div>
                  <div className="muted">Posters</div>
                </div>
                <div className="card card-third">
                  <div className="card-title">Posters manquants</div>
                  <div className="card-value">{missingPosterCount}</div>
                </div>
              </div>
              <div className="settings-card settings-card--full">
                <div className="settings-card__title">Providers</div>
                <div className="indexer-list">
                  <div className="indexer-card">
                    <div className="indexer-row indexer-row--settings indexer-row--providers">
                      <span className="indexer-title">TMDB</span>
                      <div className="provider-badges provider-badges--left">
                        <span className={`settings-badge settings-badge--lg ${external.hasTmdbApiKey ? "ok" : "warn"}`}>
                          Cle API {external.hasTmdbApiKey ? "OK" : "NO"}
                        </span>
                        <span className="settings-badge settings-badge--lg settings-badge--fixed">
                          Reponse {fmtMs(providerStats.tmdb.avgMs)}
                        </span>
                      </div>
                      <div className="provider-badges provider-badges--right">
                        <span className="settings-badge settings-badge--lg settings-badge--fixed">Appels {providerStats.tmdb.calls}</span>
                        <span className="settings-badge settings-badge--lg settings-badge--fixed">Echecs {providerStats.tmdb.failures}</span>
                      </div>
                    </div>
                  </div>
                  <div className="indexer-card">
                    <div className="indexer-row indexer-row--settings indexer-row--providers">
                      <span className="indexer-title">TVmaze</span>

                      <div className="provider-badges provider-badges--left">
                        <span className="settings-badge settings-badge--lg settings-badge--fixed">
                          Cle API N/A
                        </span>
                        <span className="settings-badge settings-badge--lg settings-badge--fixed">
                          Reponse {fmtMs(providerStats.tvmaze.avgMs)}
                        </span>
                      </div>
                      <div className="provider-badges provider-badges--right">
                        <span className="settings-badge settings-badge--lg settings-badge--fixed">
                          Appels {providerStats.tvmaze.calls}
                        </span>
                        <span className="settings-badge settings-badge--lg settings-badge--fixed">
                          Echecs {providerStats.tvmaze.failures}
                        </span>
                      </div>
                    </div>
                  </div>
                  <div className="indexer-card">
                    <div className="indexer-row indexer-row--settings indexer-row--providers">
                      <span className="indexer-title">Fanart.tv</span>
                      <div className="provider-badges provider-badges--left">
                        <span className={`settings-badge settings-badge--lg ${external.hasFanartApiKey ? "ok" : "warn"}`}>
                          Cle API {external.hasFanartApiKey ? "OK" : "NO"}
                        </span>
                        <span className="settings-badge settings-badge--lg settings-badge--fixed">
                          Reponse {fmtMs(providerStats.fanart.avgMs)}
                        </span>
                      </div>
                      <div className="provider-badges provider-badges--right">
                        <span className="settings-badge settings-badge--lg settings-badge--fixed">Appels {providerStats.fanart.calls}</span>
                        <span className="settings-badge settings-badge--lg settings-badge--fixed">Echecs {providerStats.fanart.failures}</span>
                      </div>
                    </div>
                  </div>
                  <div className="indexer-card">
                    <div className="indexer-row indexer-row--settings indexer-row--providers">
                      <span className="indexer-title">IGDB</span>
                      <div className="provider-badges provider-badges--left">
                        <span
                          className={`settings-badge settings-badge--lg ${
                            external.hasIgdbClientId && external.hasIgdbClientSecret ? "ok" : "warn"
                          }`}
                        >
                          Cle API {external.hasIgdbClientId && external.hasIgdbClientSecret ? "OK" : "NO"}
                        </span>
                        <span className="settings-badge settings-badge--lg settings-badge--fixed">
                          Reponse {fmtMs(providerStats.igdb.avgMs)}
                        </span>
                      </div>
                      <div className="provider-badges provider-badges--right">
                        <span className="settings-badge settings-badge--lg settings-badge--fixed">Appels {providerStats.igdb.calls}</span>
                        <span className="settings-badge settings-badge--lg settings-badge--fixed">Echecs {providerStats.igdb.failures}</span>
                      </div>
                    </div>
                  </div>
                </div>
              </div>
            </>
          )}

        </div>
      )}
    </div>
  );
}
