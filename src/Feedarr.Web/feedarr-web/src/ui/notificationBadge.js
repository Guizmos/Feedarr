export function shouldShowBadge(value) {
  return value != null && value !== false && value !== 0;
}

export function getBadgeLabel(value) {
  return value === "warn" ? "!" : value;
}

export function buildBadgeClass(baseClass, tone, extraClass) {
  let cls = baseClass;
  if (tone) cls += ` ${baseClass}--${tone}`;
  if (extraClass) cls += ` ${extraClass}`;
  return cls;
}
