import React, { useCallback, useEffect, useRef, useState } from "react";
import { apiDelete, apiGet } from "../api/client.js";
import AppIcon from "../ui/AppIcon.jsx";
import { cancelPreparedRestore, restoreChangedEvent, restoreStatePath } from "./restorePendingActions.js";

export default function RestorePendingBanner() {
  const [status, setStatus] = useState(null);
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);
  const sequence = useRef(0);
  const refresh = useCallback(async () => {
    const request = ++sequence.current;
    const next = await apiGet(restoreStatePath);
    if (request === sequence.current) setStatus(next);
  }, []);
  useEffect(() => {
    let active = true;
    const controller = new AbortController();
    const poll = async () => {
      const request = ++sequence.current;
      try {
        const next = await apiGet(restoreStatePath, { signal: controller.signal });
        if (active && request === sequence.current) setStatus(next);
      } catch { /* Keep the last known state during transient network errors. */ }
    };
    void poll();
    const timer = window.setInterval(poll, 15000);
    window.addEventListener(restoreChangedEvent, poll);
    return () => {
      active = false;
      controller.abort();
      window.clearInterval(timer);
      window.removeEventListener(restoreChangedEvent, poll);
    };
  }, []);
  const cancel = async () => {
    setBusy(true);
    setError("");
    try {
      await cancelPreparedRestore(status, { confirm: window.confirm.bind(window), cancel: apiDelete, refresh });
      window.dispatchEvent(new Event(restoreChangedEvent));
    } catch (e) { setError(e?.message || "Impossible d’annuler la restauration en attente."); }
    finally { setBusy(false); }
  };
  return <RestorePendingNotice status={status} error={error} busy={busy} onCancel={cancel} />;
}

export function RestorePendingNotice({ status, error = "", busy = false, onCancel }) {
  if (!status?.pending) return null;
  return (
    <div className="restore-pending-banner" role="status">
      <AppIcon name="warning" />
      <div className="restore-pending-banner__text">
        <strong>Restauration en attente</strong> — Redémarrez le conteneur Feedarr pour appliquer la base restaurée.
        <small>Les modifications effectuées depuis sa préparation ne seront pas intégrées à la base restaurée.</small>
        {status.missingPosters > 0 && <small>{status.missingPosters} référence(s) de posters introuvables ont été retirées de la copie préparée.</small>}
        {status.error && <small role="alert">État de restauration illisible. Consultez les journaux avant de redémarrer.</small>}
        {error && <small role="alert">{error}</small>}
      </div>
      <button className="btn" type="button" onClick={onCancel} disabled={busy || !status.id}>Annuler</button>
    </div>
  );
}
