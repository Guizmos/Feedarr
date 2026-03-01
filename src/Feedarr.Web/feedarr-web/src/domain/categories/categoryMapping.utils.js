import {
  CATEGORY_GROUP_LABELS,
  assertCanonicalKey,
  normalizeCategoryGroupKey,
} from "./categoryGroups.constants.js";

export const UNKNOWN_GROUP_KEY = "unknown";

function toValidCategoryId(value) {
  const id = Number(value);
  return Number.isFinite(id) && id > 0 ? id : null;
}

function toCanonicalMap(input) {
  const source = input instanceof Map ? input : new Map();
  const next = new Map();
  for (const [rawId, rawKey] of source.entries()) {
    const id = toValidCategoryId(rawId);
    if (!id) continue;
    const key = normalizeCategoryGroupKey(rawKey);
    if (!key) continue;
    next.set(id, key);
  }
  return next;
}

export function mapFromCapsAssignments(categories) {
  const map = new Map();

  for (const category of Array.isArray(categories) ? categories : []) {
    const id = toValidCategoryId(category?.id);
    if (!id) continue;

    const keyCandidates = [category?.assignedGroupKey, category?.groupKey, category?.unifiedKey];
    for (const candidate of keyCandidates) {
      if (candidate == null || String(candidate).trim() === "") continue;
      assertCanonicalKey(candidate, `mapFromCapsAssignments:catId=${id}`);
    }

    const key =
      normalizeCategoryGroupKey(category?.assignedGroupKey) ||
      normalizeCategoryGroupKey(category?.groupKey) ||
      normalizeCategoryGroupKey(category?.unifiedKey) ||
      normalizeCategoryGroupKey(category?.assignedGroupLabel) ||
      normalizeCategoryGroupKey(category?.groupLabel) ||
      normalizeCategoryGroupKey(category?.unifiedLabel);

    if (!key) continue;
    if (category?.isAssigned === false) continue;
    map.set(id, key);
  }

  return map;
}

export function buildMappingsPayload(map) {
  const canonical = toCanonicalMap(map);
  const payload = [];

  for (const [categoryId, unifiedKey] of canonical.entries()) {
    assertCanonicalKey(unifiedKey, `buildMappingsPayload:categoryId=${categoryId}`);
    const unifiedLabel = CATEGORY_GROUP_LABELS[unifiedKey] || unifiedKey;
    payload.push({
      categoryId,
      unifiedKey,
      unifiedLabel,
      catId: categoryId,
      groupKey: unifiedKey,
      groupLabel: unifiedLabel,
    });
  }

  return payload;
}

export function buildMappingDiff(previousMap, nextMap) {
  const prev = toCanonicalMap(previousMap);
  const next = toCanonicalMap(nextMap);
  const allIds = new Set([...prev.keys(), ...next.keys()]);
  const diff = [];

  for (const id of allIds) {
    const before = prev.get(id) || null;
    const after = next.get(id) || null;
    if (before === after) continue;
    diff.push({
      categoryId: id,
      unifiedKey: after,
      unifiedLabel: after ? CATEGORY_GROUP_LABELS[after] || after : null,
      catId: id,
      groupKey: after,
      groupLabel: after ? CATEGORY_GROUP_LABELS[after] || after : null,
    });
  }

  return diff;
}

export function buildCategoryMappingsPatchDto({ selectedCategoryIds, ...rest } = {}) {
  const ids = Array.isArray(selectedCategoryIds)
    ? selectedCategoryIds
        .map((value) => Number(value))
        .filter((id) => Number.isInteger(id) && id > 0)
    : [];

  const normalizedIds = Array.from(new Set(ids)).sort((a, b) => a - b);

  return {
    ...rest,
    selectedCategoryIds: normalizedIds,
  };
}

export function dedupeCategoriesById(categories) {
  if (!Array.isArray(categories)) return [];

  const map = new Map();
  const score = (cat) => {
    let result = 0;
    if (normalizeCategoryGroupKey(cat?.unifiedKey || cat?.groupKey || cat?.assignedGroupKey)) result += 4;
    if (cat?.unifiedLabel || cat?.groupLabel || cat?.assignedGroupLabel) result += 2;
    if (cat?.parentId) result += 1;
    if (cat?.isSub) result += 1;
    return result;
  };

  for (const category of categories) {
    const id = toValidCategoryId(category?.id);
    if (!id) continue;
    const normalized = { ...category, id };
    const existing = map.get(id);
    if (!existing || score(normalized) > score(existing)) {
      map.set(id, normalized);
    }
  }

  return Array.from(map.values());
}

export function dedupeBubblesByUnifiedKey(list) {
  const seen = new Set();
  const result = [];

  for (const item of Array.isArray(list) ? list : []) {
    const normalizedKey = normalizeCategoryGroupKey(item?.unifiedKey || item?.key);
    const fallbackKey = String(item?.unifiedLabel || item?.label || item?.name || "")
      .trim()
      .toLowerCase();
    const key = normalizedKey || fallbackKey;

    if (!key || seen.has(key)) continue;
    seen.add(key);
    result.push(item);
  }

  return result;
}
