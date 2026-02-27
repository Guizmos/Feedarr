import React, { useCallback, useEffect, useMemo, useState } from "react";
import Modal from "../../ui/Modal.jsx";
import Loader from "../../ui/Loader.jsx";
import { apiGet } from "../../api/client.js";
import { fmtBytes, fmtDateFromTs } from "../../utils/formatters.js";
import { tr } from "../../app/uiText.js";

export default function CategoryPreviewModal({ sourceId, catId, catName, onClose }) {
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(null);
  const [items, setItems] = useState([]);
  const [isLive, setIsLive] = useState(false);

  const fetchData = useCallback(
    (signal) => {
      if (!sourceId || !catId) return;

      setLoading(true);
      setError(null);
      setItems([]);
      setIsLive(false);

      const dbUrl = `/api/sources/${sourceId}/category-preview?catId=${catId}&limit=20`;
      const liveUrl = `/api/sources/${sourceId}/category-preview-live?catId=${catId}&limit=20`;

      apiGet(dbUrl, { signal })
        .then((dbData) => {
          const dbItems = Array.isArray(dbData) ? dbData : [];
          if (dbItems.length > 0) {
            setItems(dbItems);
            setIsLive(false);
            setLoading(false);
            return;
          }
          // DB vide → fallback live
          return apiGet(liveUrl, { signal }).then((liveData) => {
            const liveItems = Array.isArray(liveData) ? liveData : [];
            setItems(liveItems);
            setIsLive(liveItems.length > 0);
            setLoading(false);
          });
        })
        .catch((err) => {
          if (err?.name === "AbortError") return;
          setError(err?.message || tr("Erreur inconnue", "Unknown error"));
          setLoading(false);
        });
    },
    [sourceId, catId]
  );

  useEffect(() => {
    const controller = new AbortController();
    fetchData(controller.signal);
    return () => controller.abort();
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
    <Modal open title={title} onClose={onClose} width={1280} modalClassName="category-preview-modal">
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
                const controller = new AbortController();
                fetchData(controller.signal);
              }}
            >
              {tr("Réessayer", "Retry")}
            </button>
          </div>
        )}

        {!loading && !error && items.length === 0 && (
          <div className="category-preview-modal__empty">
            {tr(
              "Aucun résultat pour cette catégorie (DB et indexeur).",
              "No results for this category (DB and indexer)."
            )}
          </div>
        )}

        {!loading && !error && catSummary.length > 0 && (
          <div className="preview-cat-summary">
            {catSummary.map(([id, { count, name }]) => (
              <span key={id} className="preview-cat-summary__chip">
                {id}{name ? ` • ${name}` : ""}
                <span className="preview-cat-summary__count">×{count}</span>
              </span>
            ))}
          </div>
        )}

        {!loading && !error && items.length > 0 && (
          <table className="preview-table">
            <thead>
              <tr>
                <th>{tr("Publié", "Published")}</th>
                <th>{tr("Tracker", "Tracker")}</th>
                <th>{tr("Nom", "Name")}</th>
                <th>{tr("Taille", "Size")}</th>
                <th>{tr("Catégorie", "Category")}</th>
              </tr>
            </thead>
            <tbody>
              {items.map((item, idx) => (
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
                        ? `${item.categoryId}${item.resultCategoryName ? ` • ${item.resultCategoryName}` : ""}`
                        : item.unifiedCategory || "—"}
                    </span>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
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
