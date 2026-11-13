import React, { useCallback, useEffect, useRef, useState } from "react";
import AppIcon from "../../ui/AppIcon.jsx";
import ConfigurationArchiveInput from "./ConfigurationArchiveInput.jsx";
import Modal from "../../ui/Modal.jsx";
import InlineNotice from "../../pages/settings/InlineNotice.jsx";
import { backupRoot, configurationFileError, configurationRequest, configurationUpload, downloadConfiguration } from "./configurationBackupApi.js";

export function ConfigurationPreview({ preview }) {
  if (!preview) return null;
  const c = preview.configuration || {};
  const rows = [["Paramètres", c.parameters], ["Providers RSS / métadonnées", `${c.indexerProviders ?? 0} / ${c.externalProviders ?? 0}`],
    ["Indexeurs / sources", `${c.indexers ?? 0} / ${c.sources ?? 0}`], ["Catégories / sélections / mappings", `${c.categories ?? 0} / ${c.selections ?? 0} / ${c.mappings ?? 0}`],
    ["Applications", c.applications], ["Sources reconnues / nouvelles", `${preview.matchedSources ?? 0} / ${preview.newSources ?? 0}`]];
  return <div className="indexer-list">
    <div className="settings-card__title">Aperçu de la configuration</div>
    {rows.map(([label, value]) => <div className="indexer-card" key={label}><div className="indexer-row indexer-row--settings">
      <span className="indexer-title">{label}</span><span className="indexer-status">{value ?? 0}</span>
    </div></div>)}
    <p className="settings-help">Les paramètres et sélections importés remplacent leur configuration. La bibliothèque, les entities et les posters sont conservés.</p>
    {!!preview.disabledHistoricalSources && <InlineNotice variant="warning" message={`${preview.disabledHistoricalSources} source(s) historique(s) seront conservées et désactivées.`} />}
    {!!c.missingSecrets && <InlineNotice variant="warning" message={`${c.missingSecrets} secret(s) requis sont manquants ou à ressaisir. Les étapes incomplètes resteront à configurer.`} />}
    {preview.stricterRetention && <InlineNotice variant="warning" message="Les limites de rétention importées sont plus strictes. Elles resteront inactives jusqu’à une confirmation séparée ; leur activation pourra permettre une purge lors des synchronisations suivantes." />}
    {preview.hasLibrary && <InlineNotice variant="info" message="La première synchronisation attendra une confirmation explicite pour éviter une purge automatique surprise." />}
    {!preview.canCompleteSetup && <InlineNotice variant="warning" message="La configuration est incomplète. Complétez les secrets et services manquants dans les paramètres après redémarrage. Le compte actuel est conservé si les identifiants importés sont absents." />}
  </div>;
}

export default function ConfigurationBackupPanel({ onBusyChange, onActionsChange }) {
  const root = backupRoot(false);
  const [state, setState] = useState(null);
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");
  const [busy, setBusy] = useState(false);
  const [passphrase, setPassphrase] = useState("");
  const [confirmation, setConfirmation] = useState("");
  const [importPhrase, setImportPhrase] = useState("");
  const [file, setFile] = useState(null);
  const [consent, setConsent] = useState(false);
  const [securityConsent, setSecurityConsent] = useState(false);
  const [operation, setOperation] = useState("");
  const [decision, setDecision] = useState(null);
  const [analysisStatus, setAnalysisStatus] = useState("none");
  const exportRef = useRef(null);
  const refreshRef = useRef(null);
  const active = useRef(null), sequence = useRef(0), submitting = useRef(false);
  const request = configurationRequest;
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
      } catch (e) { if (alive && !controller.signal.aborted && !submitting.current && current === sequence.current) setError(e?.message || "Impossible de charger l’état des sauvegardes."); }
    };
    void poll(); const timer = window.setInterval(poll, 15000);
    return () => { alive = false; controller.abort(); window.clearInterval(timer); active.current?.abort(); };
  }, [request, root]);
  const run = async (action, kind = "state") => {
    if (submitting.current) return;
    submitting.current = true; sequence.current++; setBusy(true); setOperation(kind); setError(""); setNotice("");
    const controller = new AbortController(); active.current = controller;
    try { await action(controller.signal); }
    catch (e) { if (!controller.signal.aborted) { setError(e?.message || "L’opération a échoué."); if (kind === "prepare") setAnalysisStatus("error"); } }
    finally { active.current = null; submitting.current = false; setBusy(false); }
  };
  const exportArchive = () => run(async signal => {
    if (passphrase.length < 12 || passphrase.length > 1024) throw new Error("Utilisez une phrase secrète de 12 à 1 024 caractères.");
    if (passphrase !== confirmation) throw new Error("Les deux phrases secrètes doivent être identiques.");
    const phrase = passphrase; setPassphrase(""); setConfirmation("");
    const blob = await request(`${root}/export`, { method: "POST", body: { passphrase: phrase }, signal, binary: true });
    downloadConfiguration(blob); setNotice("Téléchargement de la configuration chiffrée lancé. Conservez sa phrase secrète séparément.");
  }, "export");
  const prepare = () => run(async signal => {
    const phrase = importPhrase; setImportPhrase("");
    const body = configurationUpload(file, phrase);
    setAnalysisStatus("analyzing");
    const preview = await request(`${root}/prepare`, { method: "POST", body, signal });
    setAnalysisStatus("valid");
    setState(previous => ({ ...previous, pending: preview, confirmed: false })); setConsent(false); setSecurityConsent(false); setNotice("Archive analysée. Vérifiez l’aperçu avant de confirmer.");
  }, "prepare");
  const confirm = () => run(async signal => {
    const next = await request(`${root}/${state.pending.operationId}/confirm`, { method: "POST", body: { confirm: consent, securityChange: securityConsent }, signal });
    setState(next); setNotice("Restauration confirmée. Redémarrez le conteneur Feedarr pour l’appliquer.");
  });
  const cancel = () => run(async signal => {
    await request(`${root}/${state.pending.operationId}`, { method: "DELETE", signal }); await refresh(); setAnalysisStatus(file ? "selected" : "none"); setConsent(false); setSecurityConsent(false); setNotice("Restauration préparée annulée.");
  });
  const appliedAction = (action, question) => {
    setDecision({ action, question });
  };
  const checkRestart = () => run(async () => {
    const next = await refresh();
    if (next.applied && !next.pending) setNotice("Configuration appliquée. Les données de bibliothèque ont été conservées.");
  });
  const phraseError = passphrase.length < 12 || passphrase.length > 1024
    ? "Utilisez une phrase secrète de 12 à 1 024 caractères." : "";
  const confirmationError = passphrase !== confirmation ? "Les deux phrases secrètes doivent être identiques." : "";
  const canExport = !busy && !phraseError && !confirmationError;
  const exportState = operation !== "export" ? "idle" : busy ? "loading" : error ? "error" : notice ? "success" : "idle";
  exportRef.current = exportArchive;
  refreshRef.current = () => run(async () => {
    await refresh();
    setNotice("État des sauvegardes actualisé.");
  });
  useEffect(() => {
    if (!onActionsChange) return;
    onActionsChange({
      canExport, busy, saveState: exportState,
      validationMessage: phraseError || confirmationError,
      export: () => exportRef.current?.(),
      refresh: () => refreshRef.current?.(),
    });
    return () => onActionsChange(null);
  }, [onActionsChange, canExport, busy, exportState, phraseError, confirmationError]);

  const fieldPrefix = "settings-configuration";
  const sectionClass = "settings-card";
  const title = label => <div className="settings-card__title">{label}</div>;
  const secretRow = (key, label, value, update, hint, validation = null) => <div className="indexer-card">
    <div className="indexer-row indexer-row--settings">
      <label className="indexer-title" htmlFor={`${fieldPrefix}-${key}`}>{label}</label>
      <div className="indexer-actions"><div className={`fieldWrap${value && validation ? " fieldWrap--error" : value && validation === "" ? " fieldWrap--valid" : ""}`}><input id={`${fieldPrefix}-${key}`} type="password" autoComplete="off"
        value={value} onChange={e => update(e.target.value)} disabled={busy} aria-invalid={!!value && !!validation} aria-describedby={hint ? `${fieldPrefix}-${key}-hint` : undefined} /></div></div>
    </div>
    {hint && <div className="settings-help" id={`${fieldPrefix}-${key}-hint`}>{hint}</div>}
  </div>;
  return <>
    {<section className={sectionClass} aria-label="Sauvegarder la configuration">
      {title("Sauvegarder la configuration")}
      <p className="settings-help settings-help--spaced">Paramètres, intégrations et secrets chiffrés. Aucune release, entity ou image.</p>
      <form noValidate onSubmit={e => { e.preventDefault(); void exportArchive(); }}>
        <div className="indexer-list">
          {secretRow("phrase", "Phrase secrète", passphrase, setPassphrase, passphrase && phraseError ? phraseError : "De 12 à 1 024 caractères. Conservez cette phrase séparément de la sauvegarde.", phraseError)}
          {secretRow("confirmation", "Confirmer la phrase secrète", confirmation, setConfirmation, confirmation && confirmationError ? confirmationError : "Saisissez à nouveau la même phrase secrète.", confirmationError)}
        </div>
      </form>
      {operation === "export" && error && <InlineNotice variant="error" title="Sauvegarde impossible" message={error} />}
      {operation === "export" && notice && <InlineNotice variant="success" message={notice} />}
      {busy && operation === "export" && <InlineNotice variant="info" message="Chiffrement et préparation du téléchargement…" />}
    </section>}
    <section className={sectionClass} aria-label="Restaurer une configuration">
      {title("Restaurer une configuration")}
      <div className="indexer-list">
        <p className="settings-help settings-help--spaced">Importez une archive V2 ou une ancienne sauvegarde compatible. Vérifiez l’aperçu avant de confirmer.</p>
        {state?.legacyRestorePending && <InlineNotice variant="warning" message="Une restauration V1 est en attente. Appliquez-la ou annulez-la via le bandeau existant avant tout import V2." />}
        {!state?.pending && <>
          <ConfigurationArchiveInput id={`${fieldPrefix}-archive`} file={file} disabled={busy || !!state?.legacyRestorePending}
            status={analysisStatus} error={error} onChange={next => {
              setFile(next); setImportPhrase(""); setNotice(""); setError(next ? configurationFileError(next) : "");
              setAnalysisStatus(next ? configurationFileError(next) ? "error" : "selected" : "none");
            }} />
          {secretRow("import-phrase", "Phrase secrète", importPhrase, setImportPhrase, "Laissez vide pour une ancienne archive non chiffrée.")}
          <div className="formactions"><button className="btn btn-accent" type="button" onClick={prepare} disabled={busy || !!state?.legacyRestorePending || !!configurationFileError(file)}>
            {busy && operation === "prepare" ? "Analyse en cours…" : "Analyser la sauvegarde"}
          </button></div>
        </>}
        <ConfigurationPreview preview={state?.pending} />
        {state?.pending && <>
          {!state.confirmed ? <>
            <label className="checkline"><input type="checkbox" checked={consent} onChange={e => setConsent(e.target.checked)} disabled={busy} />Je confirme le remplacement de la configuration, avec conservation de la bibliothèque.</label>
            {state.pending.securityChangeRequiresConfirmation && <label className="checkline"><input type="checkbox" checked={securityConsent} onChange={e => setSecurityConsent(e.target.checked)} disabled={busy} />J’accepte les changements de sécurité et utiliserai les identifiants restaurés après redémarrage.</label>}
            <div className="formactions"><button type="button" className="btn btn-accent" onClick={confirm} disabled={busy || !consent || state.pending.securityChangeRequiresConfirmation && !securityConsent}>Confirmer la restauration</button></div>
          </> : <InlineNotice variant="warning" message="Redémarrage requis. Redémarrez le conteneur Feedarr, puis vérifiez l’application. Les identifiants restaurés peuvent nécessiter une nouvelle connexion." />}
          <div className="settings-card__actions"><button className="btn" type="button" onClick={cancel} disabled={busy}>Annuler la restauration préparée</button><button className="btn" type="button" onClick={checkRestart} disabled={busy}><AppIcon name="refresh" />Vérifier après redémarrage</button></div>
        </>}
        {!state?.pending && state?.applied && <>
          <div className="indexer-card"><div className="indexer-row indexer-row--settings"><span className="indexer-title">Configuration appliquée</span><span className="indexer-status">{state.applied.completedSources}/{state.applied.totalSources} sources</span></div>
            <div className="settings-help">Synchronisation initiale : {({ held: "En attente de confirmation", pending: "Planifiée", running: "En cours", done: "Terminée", cancelled: "Annulée" })[state.applied.syncState] || "En attente"}</div>
          </div>
          <div className="settings-card__actions">
            {state.applied.retentionPending && <button className="btn btn-danger" type="button" disabled={busy} onClick={() => appliedAction("retention", "Activer les limites importées plus strictes ? Les synchronisations suivantes pourront supprimer des releases selon ces règles. Cette confirmation ne lance pas de purge.")}>Activer la rétention importée</button>}
            {state.applied.syncState === "held" && <>
              <button className="btn btn-accent" type="button" disabled={busy || state.applied.retentionPending} onClick={() => appliedAction("sync", "Autoriser la première synchronisation ? Elle appliquera les règles de rétention actives et pourra purger des releases.")}>Autoriser la première synchronisation</button>
              <button className="btn" type="button" disabled={busy} onClick={() => appliedAction("sync/cancel", "Annuler la demande de synchronisation initiale ?")}>Annuler cette synchronisation</button>
            </>}
            <button className="btn" type="button" onClick={checkRestart} disabled={busy}><AppIcon name="refresh" />Actualiser l’état</button>
          </div>
        </>}
        {operation !== "export" && error && <InlineNotice variant="error" title="Restauration impossible" message={error} />}
        {operation !== "export" && notice && <InlineNotice variant="success" message={notice} />}
        {busy && operation !== "export" && <InlineNotice variant="info" message="Opération en cours…" />}
      </div>
    </section>
    <Modal open={!!decision} title="Confirmer l’action" onClose={() => setDecision(null)} closeDisabled={busy} width={520}>
      <p className="settings-help">{decision?.question}</p>
      <div className="formactions">
        <button className="btn" type="button" disabled={busy} onClick={() => setDecision(null)}>Annuler</button>
        <button className="btn btn-danger" type="button" disabled={busy} onClick={() => run(async signal => {
          setState(await request(`${root}/${state.applied.operationId}/${decision.action}`, { method: "POST", body: { confirm: true }, signal }));
          setDecision(null); setNotice("Confirmation enregistrée.");
        })}>Confirmer</button>
      </div>
      {error && <InlineNotice variant="error" message={error} />}
    </Modal>
  </>;
}
