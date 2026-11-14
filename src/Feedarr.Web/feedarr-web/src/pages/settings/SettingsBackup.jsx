import React, { useEffect, useRef, useState } from "react";
import { useSubbarSetter } from "../../layout/useSubbar.js";
import { configurationFileError, configurationRequest, configurationUpload, downloadConfiguration } from "../../components/backup/configurationBackupApi.js";
import { loadUiSettingsData } from "./hooks/useUiSettings.js";
import { applyTheme } from "../../app/theme.js";
import { applyUiLanguage } from "../../app/locale.js";
import InlineNotice from "./InlineNotice.jsx";

export default function SettingsBackup() {
  const setContent = useSubbarSetter();
  const [password, setPassword] = useState("");
  const [confirmation, setConfirmation] = useState("");
  const [file, setFile] = useState(null);
  const [restorePassword, setRestorePassword] = useState("");
  const [preview, setPreview] = useState(null);
  const [busy, setBusy] = useState("");
  const [notice, setNotice] = useState(null);
  const request = useRef(null);
  const fileInput = useRef(null);
  useEffect(() => { setContent(null); return () => { request.current?.abort(); request.current = null; }; }, [setContent]);

  async function run(action, operation) {
    if (request.current) return;
    const controller = new AbortController(); request.current = controller;
    setBusy(action); setNotice(null);
    try { await operation(controller.signal); }
    catch (error) { if (!controller.signal.aborted) setNotice({ variant: "error", message: error.message || "L’opération a échoué." }); }
    finally { if (request.current === controller) { request.current = null; setBusy(""); } }
  }
  function exportBackup(event) {
    event.preventDefault();
    if (password !== confirmation) { setNotice({ variant: "error", message: "Les mots de passe ne correspondent pas." }); return; }
    void run("export", async signal => {
      const blob = await configurationRequest("export", { body: { password, confirmation }, binary: true, signal });
      downloadConfiguration(blob); setPassword(""); setConfirmation("");
      setNotice({ variant: "success", message: "Sauvegarde chiffrée créée. Le téléchargement a été lancé." });
    });
  }
  function analyse(event) {
    event.preventDefault(); setPreview(null);
    void run("preview", async signal => {
      const result = await configurationRequest("preview", { body: configurationUpload(file, restorePassword), signal });
      setPreview(result);
    });
  }
  function restore() {
    if (!preview) return;
    void run("restore", async signal => {
      const body = configurationUpload(file, restorePassword); body.append("expectedState", preview.expectedState); body.append("confirm", "true");
      // A failed or interrupted application requires a fresh analysis before retrying.
      setPreview(null);
      const result = await configurationRequest("restore", { body, signal });
      if (!result?.applied) throw new Error("Le serveur n’a pas confirmé la restauration.");
      try {
        const { ui } = await loadUiSettingsData();
        applyTheme(ui.theme, true); applyUiLanguage(ui.uiLanguage, true);
      } catch { /* The restore is committed; UI preferences can reload on the next visit. */ }
      setRestorePassword(""); setFile(null); if (fileInput.current) fileInput.current.value = "";
      setNotice({ variant: "success", message: result.restartRequired ? "Configuration restaurée. Redémarrez Feedarr pour appliquer tous les paramètres." : "Configuration restaurée. Vos connexions utilisent les nouveaux paramètres." });
    });
  }
  const fileError = file ? configurationFileError(file) : "";
  return <div className="page page--settings">
    <div className="pagehead"><div><h1>Sauvegarde / Restauration</h1><div className="muted">Conservez et retrouvez votre configuration</div></div></div>
    <div className="pagehead__divider" />
    {notice && <InlineNotice {...notice} />}
    <div className="settings-grid">
      <section className="settings-card settings-card--full">
        <h2 className="settings-card__title">Sauvegarder</h2>
        <p className="settings-help">Téléchargez vos paramètres et connexions dans un fichier chiffré. Conservez son mot de passe : il sera nécessaire pour le restaurer.</p>
        <form onSubmit={exportBackup}>
          <fieldset disabled={!!busy} className="configuration-backup-fields">
            <label htmlFor="backup-password">Mot de passe<input id="backup-password" type="password" autoComplete="new-password" required minLength={12} maxLength={1024} value={password} onChange={e => setPassword(e.target.value)} /></label>
            <label htmlFor="backup-confirmation">Confirmer le mot de passe<input id="backup-confirmation" type="password" autoComplete="new-password" required minLength={12} maxLength={1024} value={confirmation} onChange={e => setConfirmation(e.target.value)} /></label>
            <div className="formactions"><button className="btn btn-accent" type="submit">{busy === "export" ? "Création…" : "Télécharger la sauvegarde"}</button></div>
          </fieldset>
        </form>
      </section>
      <section className="settings-card settings-card--full">
        <h2 className="settings-card__title">Restaurer</h2>
        <p className="settings-help">Le compte administrateur, la politique HTTP/HTTPS et votre bibliothèque restent conservés.</p>
        <form onSubmit={analyse}>
          <fieldset disabled={!!busy} className="configuration-backup-fields">
            <label htmlFor="restore-file">Sauvegarde .feedarr<input ref={fileInput} id="restore-file" type="file" accept=".feedarr" required onChange={e => { setFile(e.target.files?.[0] || null); setPreview(null); }} /></label>
            <label htmlFor="restore-password">Mot de passe de la sauvegarde<input id="restore-password" type="password" autoComplete="off" required minLength={12} maxLength={1024} value={restorePassword} onChange={e => { setRestorePassword(e.target.value); setPreview(null); }} /></label>
            {fileError && <InlineNotice variant="error" message={fileError} />}
            <div className="formactions"><button className="btn" type="submit" disabled={!!fileError}>{busy === "preview" ? "Analyse…" : "Analyser"}</button></div>
          </fieldset>
        </form>
        {preview && <div className="configuration-backup-summary" role="status">
          <p>{preview.configuration.settings} sections de paramètres · {preview.configuration.sources} sources · {preview.configuration.indexerProviders} indexeurs · {preview.configuration.externalProviders + preview.configuration.applications} connexions</p>
          <p className="settings-help">{preview.matchedSources} sources mises à jour · {preview.newSources} ajoutées · {preview.disabledSources} désactivées. Les données associées sont conservées.</p>
          {preview.configuration.missingSecrets > 0 && <InlineNotice variant="warning" message="Certaines connexions n’ont pas de secret configuré dans cette sauvegarde." />}
          <div className="formactions"><button className="btn btn-accent" type="button" disabled={!!busy} onClick={restore}>Confirmer la restauration</button><button className="btn" type="button" disabled={!!busy} onClick={() => setPreview(null)}>Annuler</button></div>
        </div>}
        {busy === "restore" && <InlineNotice variant="info" message="Restauration en cours…" />}
      </section>
    </div>
  </div>;
}
