function toValidIdSet(ids) {
  return new Set(
    Array.from(ids || [])
      .map((value) => Number(value))
      .filter((id) => Number.isFinite(id) && id > 0)
  );
}

export function isStandardById(id) {
  const numeric = Number(id);
  return Number.isFinite(numeric) && numeric >= 1000 && numeric <= 8999;
}

export function isStandardParentId(id) {
  const numeric = Number(id);
  return isStandardById(numeric) && numeric % 1000 === 0;
}

function isStandardCategory(category) {
  if (!category) return false;
  if (category.isStandard === true) return true;
  return isStandardById(category.id);
}

export function filterLeafOnly(selectedIds, categories) {
  const next = toValidIdSet(selectedIds);
  const byId = new Map(
    (Array.isArray(categories) ? categories : [])
      .map((category) => [Number(category?.id), category])
      .filter(([id]) => Number.isFinite(id) && id > 0)
  );

  for (const id of Array.from(next)) {
    if (!isStandardById(id)) continue;
    if (id >= 10000) continue;
    if (id % 1000 === 0) continue;

    const category = byId.get(id);
    if (category && !isStandardCategory(category)) continue;

    const parentId = Math.floor(id / 1000) * 1000;
    next.delete(parentId);
  }

  return next;
}

export function getAllowedCategoryIds(categories) {
  const list = Array.isArray(categories) ? categories : [];

  return new Set(
    list
      .filter((category) => {
        const id = Number(category?.id);
        if (!Number.isFinite(id) || id <= 0) return false;
        const standard = isStandardCategory(category);
        return !standard || category?.isSupported !== false;
      })
      .map((category) => Number(category.id))
  );
}

