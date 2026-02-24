function toValidIdSet(ids) {
  return new Set(
    Array.from(ids || [])
      .map((v) => Number(v))
      .filter((id) => Number.isFinite(id) && id > 0)
  );
}

function isStandardById(id) {
  return id >= 1000 && id <= 8999;
}

function isStandardCategory(category) {
  if (!category) return false;
  if (category.isStandard === true) return true;
  const id = Number(category.id);
  return Number.isFinite(id) && isStandardById(id);
}

export function getAllowedCategoryIds(categories) {
  const list = Array.isArray(categories) ? categories : [];
  return new Set(
    list
      .filter((cat) => {
        const id = Number(cat?.id);
        if (!Number.isFinite(id) || id <= 0) return false;
        const standard = isStandardCategory(cat);
        return !standard || cat?.isSupported !== false;
      })
      .map((cat) => Number(cat.id))
  );
}

export function filterLeafOnly(selectedIds, categories) {
  const next = toValidIdSet(selectedIds);
  const byId = new Map(
    (Array.isArray(categories) ? categories : [])
      .map((cat) => [Number(cat?.id), cat])
      .filter(([id]) => Number.isFinite(id) && id > 0)
  );

  for (const id of Array.from(next)) {
    const cat = byId.get(id);
    const isStandard = cat ? isStandardCategory(cat) : isStandardById(id);
    if (!isStandard) continue;
    if (id % 1000 === 0) continue;

    const parentId = Math.floor(id / 1000) * 1000;
    next.delete(parentId);
  }

  return next;
}
