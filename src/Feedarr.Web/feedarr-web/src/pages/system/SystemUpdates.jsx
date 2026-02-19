import React, { useMemo } from "react";

function fmtDate(value) {
  if (!value) return "-";
  const parsed = new Date(value);
  if (Number.isNaN(parsed.getTime())) return "-";
  return parsed.toLocaleString();
}

function fmtVersion(value) {
  const raw = String(value || "").trim();
  if (!raw) return "-";
  if (/(^|-)v\d+\.\d+\.\d+/.test(raw)) return raw;
  return raw.startsWith("v") ? raw : `v${raw}`;
}

function getReleaseSummary(body) {
  const lines = String(body || "")
    .split(/\r?\n/)
    .map((x) => x.trim())
    .filter(Boolean);

  const firstUseful = lines.find((line) => !line.startsWith("#"));
  if (!firstUseful) return "Aucun detail de changelog.";
  return firstUseful.length > 140 ? `${firstUseful.slice(0, 137)}...` : firstUseful;
}

export default function SystemUpdates({
  loading,
  error,
  updatesEnabled,
  currentVersion,
  isUpdateAvailable,
  latestRelease,
  releases,
  checkIntervalHours,
}) {
  const statusLabel = useMemo(() => {
    if (!updatesEnabled) return "Desactive";
    if (!latestRelease?.tagName) return "Inconnu";
    if (isUpdateAvailable) return "Mise a jour disponible";
    return "A jour";
  }, [updatesEnabled, latestRelease?.tagName, isUpdateAvailable]);

  const statusClass = useMemo(() => {
    if (!updatesEnabled) return "warn";
    if (isUpdateAvailable) return "warn";
    return "ok";
  }, [updatesEnabled, isUpdateAvailable]);

  return (
    <>
      <div className="settings-card settings-card--full system-updates__card">
        <div className="settings-card__title">Update</div>
        <div className="indexer-list">
          <div className="indexer-card">
            <div className="indexer-row indexer-row--settings">
              <span className="indexer-title">Version actuelle</span>
              <div className="indexer-actions">
                <span className="indexer-status">{fmtVersion(currentVersion)}</span>
              </div>
            </div>
          </div>
          <div className="indexer-card">
            <div className="indexer-row indexer-row--settings">
              <span className="indexer-title">Derniere release</span>
              <div className="indexer-actions">
                <span className="indexer-status">{latestRelease?.tagName ? fmtVersion(latestRelease.tagName) : "-"}</span>
              </div>
            </div>
          </div>
          <div className="indexer-card">
            <div className="indexer-row indexer-row--settings">
              <span className="indexer-title">Publie le</span>
              <div className="indexer-actions">
                <span className="indexer-status">{fmtDate(latestRelease?.publishedAt)}</span>
              </div>
            </div>
          </div>
          <div className="indexer-card">
            <div className="indexer-row indexer-row--settings">
              <span className="indexer-title">Statut</span>
              <div className="indexer-actions">
                <span className={`settings-badge settings-badge--lg ${statusClass}`}>{statusLabel}</span>
              </div>
            </div>
          </div>
        </div>

        {error ? (
          <div className="onboarding__error" style={{ marginTop: 12 }}>
            {error}
          </div>
        ) : null}

        <div className="system-updates__footer">
          <div className="muted system-updates__footer-text">
            Intervalle de verification: {checkIntervalHours}h {loading ? "• chargement..." : ""}
          </div>
        </div>
      </div>

      <div className="settings-card settings-card--full">
        <div className="settings-card__title">Changelog</div>
        <div className="indexer-list">
          {releases?.length ? (
            releases.map((release) => (
              <div className="indexer-card system-changelog__item" key={release.tagName || release.htmlUrl || release.name}>
                <div className="system-changelog__head">
                  <span className="indexer-title system-changelog__version">
                    {fmtVersion(release.tagName)} {release.name && release.name !== release.tagName ? `• ${release.name}` : ""}
                  </span>
                  <span className="indexer-status system-changelog__date">{fmtDate(release.publishedAt)}</span>
                </div>
                <div className="muted system-changelog__summary">
                  {getReleaseSummary(release.body)}
                </div>
                <div className="indexer-actions system-changelog__actions">
                    {release.isPrerelease ? (
                      <span className="settings-badge settings-badge--lg warn">Prerelease</span>
                    ) : (
                      <span className="settings-badge settings-badge--lg ok">Stable</span>
                    )}
                    {release.htmlUrl ? (
                      <a
                        className="btn system-updates__release-link system-updates__release-link--inline"
                        href={release.htmlUrl}
                        target="_blank"
                        rel="noopener noreferrer"
                      >
                        Ouvrir
                      </a>
                    ) : null}
                </div>
              </div>
            ))
          ) : (
            <div className="indexer-card">
              <div className="indexer-row indexer-row--settings">
                <span className="indexer-title">Aucune release disponible</span>
              </div>
            </div>
          )}
        </div>
      </div>
    </>
  );
}
