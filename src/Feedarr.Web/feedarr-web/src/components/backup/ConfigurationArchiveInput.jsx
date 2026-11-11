import React, { useRef } from "react";
import InlineNotice from "../../pages/settings/InlineNotice.jsx";
import { fmtBytes } from "../../pages/settings/settingsUtils.js";
import { configurationFileError } from "./configurationBackupApi.js";

// Shared selection feedback. Only the server can validate archive contents.
export default function ConfigurationArchiveInput({ file, onChange, disabled, status = "none", error = "", id }) {
  const input = useRef(null);
  const invalid = file ? configurationFileError(file) : "";
  const analyzing = status === "analyzing";
  const failed = !!invalid || status === "error";
  const variant = analyzing ? "info" : failed ? "error" : file ? "success" : "warning";
  const message = analyzing ? "Analyse du contenu de l’archive en cours…"
    : failed ? `${file ? `${file.name} — ${fmtBytes(file.size) || "0 B"}. ` : ""}${invalid || error || "L’archive n’a pas pu être validée. Réessayez après vérification du fichier et de la phrase secrète."}`
    : file ? `${file.name} — ${fmtBytes(file.size)}. ${status === "valid" ? "Contenu vérifié." : "Fichier accepté — contenu non encore vérifié."}`
    : "Aucune archive sélectionnée — 64 Mio maximum";
  return <div className="indexer-card configuration-archive" aria-busy={analyzing}>
    <div className="indexer-row indexer-row--settings">
      <span className="indexer-title">Archive de configuration</span>
      <div className="indexer-actions">
        <input ref={input} id={id} type="file" hidden accept=".zip,application/zip" aria-label="Archive de configuration" disabled={disabled}
          onChange={e => { onChange(e.target.files?.[0] || null); e.target.value = ""; }} />
        <button className="btn" type="button" disabled={disabled} onClick={() => input.current?.click()}>{file ? "Remplacer le fichier" : "Choisir un fichier"}</button>
        {file && <button className="btn" type="button" disabled={disabled} onClick={() => onChange(null)}>Annuler la sélection</button>}
      </div>
    </div>
    <InlineNotice variant={variant} message={message} />
  </div>;
}
