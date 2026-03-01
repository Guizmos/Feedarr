import React, { useCallback, useEffect, useMemo, useRef, useState } from "react";
import Modal from "../../ui/Modal.jsx";
import Loader from "../../ui/Loader.jsx";
import { apiGet, apiPost } from "../../api/client.js";
import { fmtBytes, fmtDateFromTs } from "../../utils/formatters.js";
import { tr } from "../../app/uiText.js";

/**
 * CategoryPreviewModal — two modes:
 *  - sourceId mode (saved source): DB first, then LIVE fallback
 *  - previewCredentials mode (wizard add / onboarding): LIVE-only via temp endpoint
 *
 * @param {number|null}  sourceId          - existing source ID (or null)
 * @param {object|null}  previewCredentials - { providerId?, torznabUrl, indexerId?, authMode?, apiKey?, sourceName? }
 * @param {number}       catId             - category ID to preview
 * @param {string}       catName           - display name of the category
 * @param {Object<string, string>|null} categoryNameMap - known category names by id
 * @param {function}     onClose           - close handler
 */
export default function CategoryPreviewModal({ sourceId, previewCredentials, catId, catName, categoryNameMap = null, onClose }) {
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(null);
  const [items, setItems] = useState([]);
  const [isLive, setIsLive] = useState(false);
  const requestedCatId = Number(catId);
  const abortCtrlRef = useRef(null);

  const fetchData = useCallback(
    (signal) => {
      if (!catId) return;

      setLoading(true);
      setError(null);
      setItems([]);
      setIsLive(false);

      if (sourceId) {
        // Mode A: saved source — LIVE first to mirror Jackett behaviour
        const liveUrl = `/api/sources/${sourceId}/category-preview-live?catId=${catId}&limit=20`;

        apiGet(liveUrl, { signal })
          .then((liveData) => {
            const liveItems = Array.isArray(liveData) ? liveData : [];
            setItems(liveItems);
            setIsLive(true);
            setLoading(false);
          })
          .catch((err) => {
            if (err?.name === "AbortError") return;
            setError(err?.message || tr("Erreur inconnue", "Unknown error"));
            setLoading(false);
          });
      } else if (previewCredentials?.torznabUrl) {
        // Mode B: no saved source (wizard) — LIVE-only via temp endpoint
        const body = {
          providerId: previewCredentials.providerId ?? null,
          torznabUrl: previewCredentials.torznabUrl,
          indexerId: previewCredentials.indexerId ?? null,
          authMode: previewCredentials.authMode ?? "query",
          apiKey: previewCredentials.apiKey ?? "",
          sourceName: previewCredentials.sourceName ?? "",
          catId,
          limit: 20,
        };

        apiPost("/api/sources/category-preview-live-temp", body, { signal })
          .then((liveData) => {
            const liveItems = Array.isArray(liveData) ? liveData : [];
            setItems(liveItems);
            setIsLive(liveItems.length > 0);
            setLoading(false);
          })
          .catch((err) => {
            if (err?.name === "AbortError") return;
            setError(err?.message || tr("Erreur inconnue", "Unknown error"));
            setLoading(false);
          });
      } else {
        setLoading(false);
      }
    },
    [sourceId, previewCredentials, catId]
  );

  useEffect(() => {
    const controller = new AbortController();
    abortCtrlRef.current = controller;
    fetchData(controller.signal);
    return () => {
      // Abort whatever is currently active (may differ from `controller` if
      // the retry button was clicked while this effect's fetch was in flight).
      abortCtrlRef.current?.abort();
      abortCtrlRef.current = null;
    };
  }, [fetchData]);

  const catSummary = useMemo(() => {
    const counts = {};
    for (const item of items) {
      const id = item.categoryId;
      if (!id) continue;
      counts[id] = {
        count: (counts[id]?.count || 0) + 1,
        name: counts[id]?.name || item.resultCategoryName || null,
      };
    }
    return Object.entries(counts).sort((a, b) => b[1].count - a[1].count);
  }, [items]);
  const returnedCategoryIds = useMemo(
    () =>
      catSummary
        .map(([id]) => Number(id))
        .filter((id) => Number.isFinite(id) && id > 0),
    [catSummary]
  );
  const requestedCategoryReturned =
    Number.isFinite(requestedCatId) &&
    returnedCategoryIds.includes(requestedCatId);
  const showReturnedCategoryMismatch =
    returnedCategoryIds.length > 0 && !requestedCategoryReturned;

  const emptyMessage = tr(
    "Aucun résultat retourné par l'indexeur pour cette catégorie.",
    "No results returned by the indexer for this category."
  );

  function resolveCategoryName(item) {
    if (item?.resultCategoryName) return item.resultCategoryName;
    const itemCatId = Number(item?.categoryId);
    if (!Number.isFinite(itemCatId) || !categoryNameMap) return null;
    return categoryNameMap[itemCatId] || null;
  }

  const title = (
    <span>
      {tr(
        `Aperçu : ${catName} (id\u00a0${catId})`,
        `Preview: ${catName} (id\u00a0${catId})`
      )}
      {isLive && (
        <span className="preview-live-badge">{tr("LIVE", "LIVE")}</span>
      )}
    </span>
  );

  return (
    <Modal open title={title} onClose={onClose} width="75vw" modalClassName="category-preview-modal">
      <div className="category-preview-modal__body">
        {loading && (
          <div className="category-preview-modal__loader">
            <Loader />
          </div>
        )}

        {!loading && error && (
          <div className="category-preview-modal__error">
            <span>{error}</span>
            <button
              type="button"
              className="btn btn--sm"
              onClick={() => {
                abortCtrlRef.current?.abort();
                const controller = new AbortController();
                abortCtrlRef.current = controller;
                fetchData(controller.signal);
              }}
            >
              {tr("Réessayer", "Retry")}
            </button>
          </div>
        )}

        {!loading && !error && items.length === 0 && (
          <div className="category-preview-modal__empty">
            {emptyMessage}
          </div>
        )}

        {!loading && !error && (catSummary.length > 0 || Number.isFinite(requestedCatId)) && (
          <div className="preview-cat-summary">
            {Number.isFinite(requestedCatId) && (
              <span className="preview-cat-summary__chip preview-cat-summary__chip--requested">
                {tr("Demandée", "Requested")}: {requestedCatId}{catName ? ` • ${catName}` : ""}
              </span>
            )}
            {catSummary.map(([id, { count, name }]) => (
              <span
                key={id}
                className={`preview-cat-summary__chip${
                  showReturnedCategoryMismatch ? " preview-cat-summary__chip--returned" : ""
                }`}
              >
                {id}{name ? ` • ${name}` : ""}
                <span className="preview-cat-summary__count">×{count}</span>
              </span>
            ))}
          </div>
        )}

        {!loading && !error && items.length > 0 && (
          <>
            <div className="category-preview-modal__note">
              {showReturnedCategoryMismatch
                ? tr(
                    `La requête a bien été lancée sur ${requestedCatId}${catName ? ` • ${catName}` : ""}, mais l'indexeur classe les items retournés dans d'autres catégories. La colonne "Catégorie item" affiche toujours la valeur brute remontée par l'indexeur.`,
                    `The request was sent for ${requestedCatId}${catName ? ` • ${catName}` : ""}, but the indexer classifies returned items in different categories. The "Item category" column always shows the raw value returned by the indexer.`
                  )
                : tr(
                    "Catégorie item: valeur brute remontée par l'indexeur pour chaque release. Aucune catégorie Feedarr n'est réappliquée ici.",
                    "Item category: raw value returned by the indexer for each release. No Feedarr mapping is reapplied here."
                  )}
            </div>
            <table className="preview-table">
              <thead>
                <tr>
                  <th>{tr("Publié", "Published")}</th>
                  <th>{tr("Tracker", "Tracker")}</th>
                  <th>{tr("Nom", "Name")}</th>
                  <th>{tr("Taille", "Size")}</th>
                  <th>{tr("Catégorie item", "Item category")}</th>
                </tr>
              </thead>
              <tbody>
                {items.map((item, idx) => {
                  const resolvedCategoryName = resolveCategoryName(item);
                  return (
                    <tr key={idx}>
                      <td className="preview-table__col-date">
                        {fmtDateFromTs(item.publishedAtTs)}
                      </td>
                      <td className="preview-table__col-tracker">
                        {item.sourceName || "—"}
                      </td>
                      <td className="preview-table__col-name" title={item.title}>
                        {item.title}
                      </td>
                      <td className="preview-table__col-size">
                        {fmtBytes(item.sizeBytes) || "—"}
                      </td>
                      <td className="preview-table__col-cat">
                        <span className="preview-table__cat-label">
                          {item.categoryId
                            ? `${item.categoryId}${resolvedCategoryName ? ` • ${resolvedCategoryName}` : ""}`
                            : item.unifiedCategory || "—"}
                        </span>
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </>
        )}
      </div>

      <div className="category-preview-modal__footer">
        <button type="button" className="btn" onClick={onClose}>
          {tr("Fermer", "Close")}
        </button>
      </div>
    </Modal>
  );
}
