import React, { useCallback, useEffect, useRef, useState } from "react";
import { apiGet } from "../../api/client.js";
import InlineNotice from "../../pages/settings/InlineNotice.jsx";
import { backupRoot, configurationRequest, configurationUpload, downloadConfiguration } from "./configurationBackupApi.js";

export function ConfigurationPreview({ preview }) {
  if (!preview) return null;
  const c = preview.configuration || {};
  return <div className="card" style={{ padding: 16, marginTop: 12 }}>
    <strong>Aperçu de la configuration</strong>
    <dl className="configuration-backup-summary">
      <dt>Paramètres</dt><dd>{c.parameters ?? 0}</dd><dt>Providers RSS / métadonnées</dt><dd>{c.indexerProviders ?? 0} / {c.externalProviders ?? 0}</dd>
      <dt>Indexeurs / sources</dt><dd>{c.indexers ?? 0} / {c.sources ?? 0}</dd><dt>Catégories / sélections / mappings</dt><dd>{c.categories ?? 0} / {c.selections ?? 0} / {c.mappings ?? 0}</dd>
      <dt>Applications</dt><dd>{c.applications ?? 0}</dd><dt>Sources reconnues / nouvelles</dt><dd>{preview.matchedSources ?? 0} / {preview.newSources ?? 0}</dd>
    </dl>
    <p>Les paramètres et sélections importés remplacent leur configuration. La bibliothèque, les entities et les posters sont conservés.</p>
    {!!preview.disabledHistoricalSources && <InlineNotice variant="warning" message={`${preview.disabledHistoricalSources} source(s) historique(s) seront conservées et désactivées.`} />}
    {!!c.missingSecrets && <InlineNotice variant="warning" message={`${c.missingSecrets} secret(s) requis sont manquants ou à ressaisir. Les étapes incomplètes resteront à configurer.`} />}
    {preview.stricterRetention && <InlineNotice variant="warning" message="Les limites de rétention importées sont plus strictes. Elles resteront inactives jusqu’à une confirmation séparée ; leur activation pourra permettre une purge lors des synchronisations suivantes." />}
    {preview.hasLibrary && <InlineNotice variant="info" message="La première synchronisation attendra une confirmation explicite pour éviter une purge automatique surprise." />}
    {!preview.canCompleteSetup && <InlineNotice variant="warning" message="La configuration est incomplète. Après redémarrage, seules les étapes nécessaires seront proposées." />}
  </div>;
}

export default function ConfigurationBackupPanel({ initialSetup = false, onApplied, onBusyChange }) {
  const root = backupRoot(initialSetup);
  const [state, setState] = useState(null);
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");
  const [busy, setBusy] = useState(false);
  const [passphrase, setPassphrase] = useState("");
  const [confirmation, setConfirmation] = useState("");
  const [importPhrase, setImportPhrase] = useState("");
  const [bootstrap, setBootstrap] = useState("");
  const [file, setFile] = useState(null);
  const [consent, setConsent] = useState(false);
  const [securityConsent, setSecurityConsent] = useState(false);
  const active = useRef(null), sequence = useRef(0), submitting = useRef(false);
  const request = useCallback((path, options = {}) => configurationRequest(path, { bootstrap, ...options }), [bootstrap]);
  const refresh = useCallback(async () => {
    const current = ++sequence.current;
    const next = await request(`${root}/state`);
    if (current === sequence.current) setState(next);
    return next;
  }, [request, root]);
  useEffect(() => { onBusyChange?.(busy); }, [busy, onBusyChange]);
  useEffect(() => {
    let alive = true;
    const controller = new AbortController();
    const poll = async () => {
      if (submitting.current) return;
      const current = ++sequence.current;
      try {
        const next = await request(`${root}/state`, { signal: controller.signal });
        if (alive && !submitting.current && current === sequence.current) setState(next);
      } catch { /* The form remains usable for a remote bootstrap credential or reconnect. */ }
    };
    void poll(); const timer = window.setInterval(poll, 15000);
    return () => { alive = false; controller.abort(); window.clearInterval(timer); active.current?.abort(); };
  }, [request, root]);
  const run = async action => {
    if (submitting.current) return;
    submitting.current = true; sequence.current++; setBusy(true); setError(""); setNotice("");
    const controller = new AbortController(); active.current = controller;
    try { await action(controller.signal); }
    catch (e) { if (!controller.signal.aborted) setError(e?.message || "L’opération a échoué."); }
    finally { active.current = null; submitting.current = false; setBusy(false); }
  };
  const exportArchive = () => run(async signal => {
    const phrase = passphrase; setPassphrase(""); setConfirmation("");
    const blob = await request(`${root}/export`, { method: "POST", body: { passphrase: phrase }, signal, binary: true });
    downloadConfiguration(blob); setNotice("Configuration chiffrée téléchargée. Conservez sa phrase secrète séparément.");
  });
  const prepare = () => run(async signal => {
    const phrase = importPhrase; setImportPhrase("");
    const body = configurationUpload(file, phrase);
    const preview = await request(`${root}/prepare`, { method: "POST", body, signal });
    setState(previous => ({ ...previous, pending: preview, confirmed: false })); setConsent(false); setSecurityConsent(false);
  });
  const confirm = () => run(async signal => {
    const next = await request(`${root}/${state.pending.operationId}/confirm`, { method: "POST", body: { confirm: consent, securityChange: securityConsent }, signal });
    setState(next); setNotice("Restauration confirmée. Redémarrez le conteneur Feedarr pour l’appliquer.");
  });
  const cancel = () => run(async signal => {
    await request(`${root}/${state.pending.operationId}`, { method: "DELETE", signal }); await refresh(); setConsent(false); setSecurityConsent(false);
  });
  const appliedAction = (action, question) => {
    if (!window.confirm(question)) return;
    return run(async signal => setState(await request(`${root}/${state.applied.operationId}/${action}`, { method: "POST", body: { confirm: true }, signal })));
  };
  const checkRestart = () => run(async () => {
    if (initialSetup) {
      const setup = await apiGet("/api/setup/state");
      if (setup.onboardingDone || Array.isArray(setup.restoreSteps)) { onApplied?.(setup); return; }
    }
    const next = await refresh();
    if (next.applied && !next.pending) setNotice("Configuration appliquée. Les données de bibliothèque ont été conservées.");
  });
  return <div className="configuration-backup" style={{ gridColumn: "1 / -1" }}>
    {error && <div role="alert"><InlineNotice variant="error" message={error} /></div>}
    {notice && <InlineNotice variant="success" message={notice} />}
    {initialSetup && <label className="formrow">Secret bootstrap (uniquement si l’accès distant le demande)
      <input type="password" autoComplete="off" value={bootstrap} onChange={e => setBootstrap(e.target.value)} disabled={busy} />
    </label>}
    {state?.legacyRestorePending && <InlineNotice variant="warning" message="Une restauration V1 est en attente. Appliquez-la ou annulez-la via le bandeau existant avant tout import V2." />}
    {!initialSetup && <section className="card" style={{ padding: 16, marginBottom: 16 }}>
      <h3>Sauvegarder la configuration</h3><p className="muted">Paramètres, intégrations et secrets chiffrés. Aucune release, entity ou image.</p>
      <label className="formrow">Phrase secrète<input type="password" autoComplete="new-password" value={passphrase} onChange={e => setPassphrase(e.target.value)} disabled={busy} /></label>
      <label className="formrow">Confirmer la phrase secrète<input type="password" autoComplete="new-password" value={confirmation} onChange={e => setConfirmation(e.target.value)} disabled={busy} /></label>
      <button className="btn btn-accent" type="button" onClick={exportArchive} disabled={busy || passphrase.length < 12 || passphrase.length > 1024 || passphrase !== confirmation}>Sauvegarder la configuration</button>
    </section>}
    {!state?.pending && <section className="card" style={{ padding: 16 }}>
      <h3>Restaurer une configuration</h3>
      <label className="formrow">Archive V2 ou ancienne sauvegarde compatible<input type="file" accept=".zip,application/zip" onChange={e => { setFile(e.target.files?.[0] || null); setImportPhrase(""); }} disabled={busy || state?.legacyRestorePending} /></label>
      <label className="formrow">Phrase secrète (laisser vide pour une ancienne archive)<input type="password" autoComplete="off" value={importPhrase} onChange={e => setImportPhrase(e.target.value)} disabled={busy} /></label>
      <button className="btn btn-accent" type="button" onClick={prepare} disabled={busy || !file || state?.legacyRestorePending}>Analyser la sauvegarde</button>
    </section>}
    <ConfigurationPreview preview={state?.pending} />
    {state?.pending && <section className="card" style={{ padding: 16, marginTop: 12 }}>
      {!state.confirmed ? <>
        <label><input type="checkbox" checked={consent} onChange={e => setConsent(e.target.checked)} disabled={busy} /> Je confirme le remplacement de la configuration, avec conservation de la bibliothèque.</label>
        {state.pending.securityChangeRequiresConfirmation && <label className="formrow"><input type="checkbox" checked={securityConsent} onChange={e => setSecurityConsent(e.target.checked)} disabled={busy} /> J’accepte les changements de sécurité et utiliserai les identifiants restaurés après redémarrage.</label>}
        <div className="formactions"><button type="button" className="btn btn-accent" onClick={confirm} disabled={busy || !consent || state.pending.securityChangeRequiresConfirmation && !securityConsent}>Confirmer la restauration</button></div>
      </> : <InlineNotice variant="warning" message="Redémarrage requis. Redémarrez le conteneur Feedarr, puis vérifiez l’application. Les identifiants restaurés peuvent nécessiter une nouvelle connexion." />}
      <div className="formactions"><button className="btn" type="button" onClick={cancel} disabled={busy}>Annuler la restauration préparée</button><button className="btn" type="button" onClick={checkRestart} disabled={busy}>Vérifier après redémarrage</button></div>
    </section>}
    {!state?.pending && state?.applied && <section className="card" style={{ padding: 16, marginTop: 12 }}>
      <h3>Configuration appliquée</h3><p>Synchronisation initiale : {state.applied.syncState} — {state.applied.completedSources}/{state.applied.totalSources} sources.</p>
      {!initialSetup && state.applied.retentionPending && <button className="btn btn-danger" type="button" disabled={busy} onClick={() => appliedAction("retention", "Activer les limites importées plus strictes ? Les synchronisations suivantes pourront supprimer des releases selon ces règles. Cette confirmation ne lance pas de purge.")}>Activer la rétention importée</button>}
      {!initialSetup && state.applied.syncState === "held" && <div className="formactions"><button className="btn btn-accent" type="button" disabled={busy || state.applied.retentionPending} onClick={() => appliedAction("sync", "Autoriser la première synchronisation ? Elle appliquera les règles de rétention actives et pourra purger des releases.")}>Autoriser la première synchronisation</button><button className="btn" type="button" disabled={busy} onClick={() => appliedAction("sync/cancel", "Annuler la demande de synchronisation initiale ?")}>Annuler cette synchronisation</button></div>}
      <button className="btn" type="button" onClick={checkRestart} disabled={busy}>Actualiser l’état</button>
    </section>}
    {busy && <p role="status">Opération en cours…</p>}
  </div>;
}
