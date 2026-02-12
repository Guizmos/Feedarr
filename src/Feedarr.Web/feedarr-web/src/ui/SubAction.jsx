import React from "react";
import AppIcon from "./AppIcon.jsx";

export default function SubAction({
  icon,
  label,
  active,
  disabled,
  onClick,
  title,
  className,
}) {
  return (
    <button
      className={
        "subaction" +
        (active ? " is-active" : "") +
        (className ? ` ${className}` : "")
      }
      type="button"
      disabled={disabled}
      onClick={onClick}
      title={title || label}
      aria-label={label}
    >
      <AppIcon name={icon} className="subaction__icon" />
      <span className="subaction__label">{label}</span>
    </button>
  );
}
