import React from "react";

export default function ToggleSwitch({
  checked = false,
  onIonChange,
  onChange,
  disabled = false,
  className = "",
  title,
  ...rest
}) {
  const emit = onIonChange || onChange;

  const handleClick = () => {
    if (disabled) return;
    const nextChecked = !checked;
    if (typeof emit === "function") {
      emit({
        detail: { checked: nextChecked },
        target: { checked: nextChecked },
      });
    }
  };

  return (
    <button
      type="button"
      role="switch"
      aria-checked={checked}
      aria-disabled={disabled}
      onClick={handleClick}
      disabled={disabled}
      className={`settings-toggle settings-toggle-native ${checked ? "is-on" : "is-off"}${className ? ` ${className}` : ""}`}
      title={title}
      {...rest}
    >
      <span className="settings-toggle-native__thumb" />
    </button>
  );
}
