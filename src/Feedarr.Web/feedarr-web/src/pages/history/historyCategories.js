function toFiniteCategoryId(value) {
  const id = Number(value);
  return Number.isFinite(id) && id > 0 ? id : null;
}

export function parseCategoryIdsFromData(data) {
  if (!data || typeof data !== "object") return [];

  const directIds = Array.isArray(data.categoryIds) ? data.categoryIds : [];
  const nestedCategories = Array.isArray(data.categories) ? data.categories : [];

  const ids = [];
  directIds.forEach((raw) => {
    const id = toFiniteCategoryId(raw);
    if (id != null) ids.push(id);
  });

  nestedCategories.forEach((raw) => {
    const id = toFiniteCategoryId(raw?.id);
    if (id != null) ids.push(id);
  });

  return Array.from(new Set(ids));
}

export function parseCategoryIdsFromMessage(message) {
  const raw = String(message ?? "").toLowerCase();
  const parts = raw.split("cats=");
  if (parts.length < 2) return [];

  const catsSection = parts[1].split("missing=")[0];
  if (!catsSection) return [];

  return Array.from(new Set(
    catsSection
      .split(",")
      .map((segment) => segment.trim().split("=")[0])
      .map((value) => toFiniteCategoryId(value))
      .filter((value) => value != null)
  ));
}

export function extractCategoryIds(entry, data) {
  const fromData = parseCategoryIdsFromData(data);
  if (fromData.length > 0) return fromData;
  return parseCategoryIdsFromMessage(entry?.message);
}
