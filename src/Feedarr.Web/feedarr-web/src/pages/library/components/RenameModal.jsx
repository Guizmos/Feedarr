import React from "react";
import Modal from "../../../ui/Modal.jsx";

/**
 * Modal de renommage d'une release
 */
export default function RenameModal({
  open,
  onClose,
  renameValue,
  setRenameValue,
  renameOriginal,
  onSave,
}) {
  const renameInputId = "rename-release-input";

  return (
    <Modal open={open} title="Renommer" onClose={onClose} width={520}>
      <form onSubmit={onSave} style={{ padding: 12 }}>
        <div className="field" style={{ marginBottom: 12 }}>
          <label className="muted">Titre original</label>
          <div style={{ padding: "4px 0", color: "var(--text-primary)" }}>
            {renameOriginal || "—"}
          </div>
        </div>
        <div className="field" style={{ marginBottom: 12 }}>
          <label className="muted" htmlFor={renameInputId}>Nouveau titre</label>
          <input
            id={renameInputId}
            value={renameValue}
            onChange={(e) => setRenameValue(e.target.value)}
            placeholder="Titre"
            aria-label="Nouveau titre"
            autoFocus
          />
        </div>
        <div className="formactions">
          <button className="btn" type="submit">Enregistrer</button>
          <button className="btn" type="button" onClick={onClose}>Annuler</button>
        </div>
      </form>
    </Modal>
  );
}
