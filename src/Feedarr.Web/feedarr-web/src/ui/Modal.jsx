import React, { useEffect } from "react";
import { createPortal } from "react-dom";
import AppIcon from "./AppIcon.jsx";

export default function Modal({
  open,
  title,
  onClose,
  children,
  width = 560,
  titleClassName = "",
  modalClassName = "",
}) {
  useEffect(() => {
    if (!open) return;
    function onKey(e) {
      if (e.key === "Escape") onClose?.();
    }
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [open, onClose]);

  useEffect(() => {
    if (!open || typeof document === "undefined") return;

    const body = document.body;
    const html = document.documentElement;
    const lockCount = Number(body.dataset.modalLockCount || "0");

    if (lockCount === 0) {
      const scrollbarWidth = Math.max(0, window.innerWidth - html.clientWidth);

      body.dataset.modalPrevBodyOverflow = body.style.overflow || "";
      body.dataset.modalPrevBodyPaddingRight = body.style.paddingRight || "";
      body.dataset.modalPrevHtmlOverflow = html.style.overflow || "";
      body.dataset.modalPrevHtmlOverscroll = html.style.overscrollBehavior || "";

      if (scrollbarWidth > 0) {
        body.style.paddingRight = `${scrollbarWidth}px`;
      }

      body.style.overflow = "hidden";
      html.style.overflow = "hidden";
      html.style.overscrollBehavior = "none";
      body.classList.add("modal-open");
    }

    body.dataset.modalLockCount = String(lockCount + 1);

    return () => {
      const nextCount = Math.max(0, Number(body.dataset.modalLockCount || "1") - 1);

      if (nextCount === 0) {
        body.style.overflow = body.dataset.modalPrevBodyOverflow || "";
        body.style.paddingRight = body.dataset.modalPrevBodyPaddingRight || "";
        html.style.overflow = body.dataset.modalPrevHtmlOverflow || "";
        html.style.overscrollBehavior = body.dataset.modalPrevHtmlOverscroll || "";
        body.classList.remove("modal-open");
        delete body.dataset.modalLockCount;
        delete body.dataset.modalPrevBodyOverflow;
        delete body.dataset.modalPrevBodyPaddingRight;
        delete body.dataset.modalPrevHtmlOverflow;
        delete body.dataset.modalPrevHtmlOverscroll;
      } else {
        body.dataset.modalLockCount = String(nextCount);
      }
    };
  }, [open]);

  if (!open) return null;
  if (typeof document === "undefined") return null;

  return createPortal(
    <div className="modal-overlay" onClick={onClose}>
      <div
        className={`modal${modalClassName ? ` ${modalClassName}` : ""}`}
        style={{ width }}
        onClick={(e) => e.stopPropagation()}
        role="dialog"
        aria-modal="true"
      >
        <div className="modal-head">
          <div className={`modal-title${titleClassName ? ` ${titleClassName}` : ""}`}>{title}</div>
          <button className="iconbtn" type="button" onClick={onClose} title="Fermer">
            <AppIcon name="close" />
          </button>
        </div>
        <div className="modal-body">{children}</div>
      </div>
    </div>,
    document.body
  );
}
