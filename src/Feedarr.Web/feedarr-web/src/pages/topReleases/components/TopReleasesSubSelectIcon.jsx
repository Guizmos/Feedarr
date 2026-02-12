import React from "react";
import AppIcon from "../../../ui/AppIcon.jsx";

export default function TopReleasesSubSelectIcon({
  icon,
  label,
  value,
  onChange,
  children,
  title,
  active,
}) {
  return (
    <div
      className={`subdropdown${active ? " is-active" : ""}`}
      title={title || label}
    >
      {icon ? (
        <AppIcon name={icon} className="subdropdown__icon" />
      ) : null}
      <span className="subdropdown__label">{label}</span>
      <span className="subdropdown__caret">▾</span>
      <select
        className="subdropdown__select"
        value={value}
        onChange={onChange}
        aria-label={label}
      >
        {children}
      </select>
    </div>
  );
}
