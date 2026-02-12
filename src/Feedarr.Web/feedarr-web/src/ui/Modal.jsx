import React, { useEffect } from "react";
import AppIcon from "./AppIcon.jsx";

export default function Modal({ open, title, onClose, children, width = 560, titleClassName = "" }) {
  useEffect(() => {
    if (!open) return;
    function onKey(e) {
      if (e.key === "Escape") onClose?.();
    }
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [open, onClose]);

  if (!open) return null;

  return (
    <div className="modal-overlay" onClick={onClose}>
      <div
        className="modal"
        style={{ width }}
        onClick={(e) => e.stopPropagation()}
        role="dialog"
        aria-modal="true"
      >
        <div className="modal-head">
          <div className={`modal-title${titleClassName ? ` ${titleClassName}` : ""}`}>{title}</div>
          <button className="iconbtn" onClick={onClose} title="Fermer">
            <AppIcon name="close" />
          </button>
        </div>
        <div className="modal-body">{children}</div>
      </div>
    </div>
  );
}
