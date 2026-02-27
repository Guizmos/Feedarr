import React from "react";
import AppIcon from "../../ui/AppIcon.jsx";

const iconByVariant = {
  error: "error",
  warning: "warning",
  info: "lock",
  success: "check_circle",
};

export default function InlineNotice({ variant = "info", title = "", message = "" }) {
  if (!message) return null;

  const icon = iconByVariant[variant] || iconByVariant.info;

  return (
    <div className={`inline-notice inline-notice--${variant}`} role="status" aria-live="polite">
      <span className="inline-notice__icon" aria-hidden="true">
        <AppIcon name={icon} size={16} />
      </span>
      <div className="inline-notice__content">
        {!!title && <div className="inline-notice__title">{title}</div>}
        <div className="inline-notice__message">{message}</div>
      </div>
    </div>
  );
}
