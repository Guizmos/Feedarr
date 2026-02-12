import React from "react";
import { ImageOff } from "lucide-react";
import Modal from "../../../ui/Modal.jsx";
import { getPosterSizeLabel } from "../utils/helpers.js";

/**
 * Modal de recherche manuelle de poster
 */
export default function ManualPosterModal({
  open,
  onClose,
  query,
  setQuery,
  results,
  loading,
  error,
  onSearch,
  onApply,
  mediaType,
}) {
  const handleSubmit = (e) => {
    e.preventDefault();
    onSearch(query, mediaType);
  };

  return (
    <Modal open={open} title="Poster manuel" onClose={onClose} width={820}>
      <div style={{ padding: 12 }}>
        <form onSubmit={handleSubmit} className="posterManualSearch">
          <input
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            placeholder="Rechercher un titre..."
            aria-label="Titre à rechercher pour le poster"
            autoFocus
          />
          <button className="btn" type="submit" disabled={loading}>
            {loading ? "Recherche…" : "Rechercher"}
          </button>
        </form>

        {error && <div className="errorbox" style={{ marginTop: 10 }}>{error}</div>}

        <div className="posterManualResults">
          {results.map((r, idx) => {
            const providerLabel = String(r?.provider || "tmdb").toUpperCase();
            const posterLang = String(r?.posterLang || r?.lang || "").toUpperCase();
            const posterSize = getPosterSizeLabel(r);
            const isPosterMissing = !r?.posterPath && !r?.posterUrl;
            const badges = [
              providerLabel ? { key: "provider", label: providerLabel } : null,
              posterLang ? { key: "lang", label: posterLang } : null,
              posterSize ? { key: "size", label: posterSize } : null,
            ].filter(Boolean);

            return (
              <button
                key={`${r.provider || "tmdb"}-${r.igdbId ?? r.tmdbId ?? "0"}-${r.posterPath || r.posterUrl || "noposter"}-${idx}`}
                type="button"
                className={`posterManualResult${isPosterMissing ? " posterManualResult--disabled" : ""}`}
                onClick={() => {
                  if (!isPosterMissing) onApply(r);
                }}
                aria-label={
                  isPosterMissing
                    ? `Poster indisponible pour ${r.title || "ce titre"}`
                    : `Appliquer le poster ${r.title || ""}`.trim()
                }
                title={isPosterMissing ? `Aucun poster disponible sur ${providerLabel}` : "Appliquer ce poster"}
                disabled={isPosterMissing}
              >
                {r.posterUrl ? (
                  <img src={r.posterUrl} alt={r.title || ""} loading="lazy" />
                ) : (
                  <div className="posterManualFallback">
                    <ImageOff className="posterManualFallback__icon" />
                  </div>
                )}
                <div className="posterManualMeta">
                  <div className="posterManualTitle">{r.title || "—"}</div>
                  {badges.length > 0 ? (
                    <div className="posterManualBadges">
                      {badges.map((badge) => (
                        <span
                          key={`${badge.key}-${badge.label}`}
                          className={`posterBadge posterBadge--${badge.key}`}
                        >
                          {badge.label}
                        </span>
                      ))}
                    </div>
                  ) : null}
                  <div className="posterManualSub">
                    {r.year ? <span>{r.year}</span> : null}
                    {r.mediaType ? <span>{r.mediaType}</span> : null}
                  </div>
                </div>
              </button>
            );
          })}

          {!loading && results.length === 0 && (
            <div className="muted" style={{ padding: 12 }}>Aucun résultat.</div>
          )}
        </div>
      </div>
    </Modal>
  );
}
