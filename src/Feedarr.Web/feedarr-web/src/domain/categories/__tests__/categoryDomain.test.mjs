import test from "node:test";
import assert from "node:assert/strict";
import {
  CATEGORY_GROUP_KEYS,
  CATEGORY_GROUP_LABELS,
  CATEGORY_GROUPS,
  buildMappingsPayload,
  mapFromCapsAssignments,
  normalizeCategoryGroupKey,
} from "../index.js";

test("normalizeCategoryGroupKey canonicalizes aliases and rejects unknown keys", () => {
  assert.equal(normalizeCategoryGroupKey("shows"), "emissions");
  assert.equal(normalizeCategoryGroupKey("show"), "emissions");
  assert.equal(normalizeCategoryGroupKey("Films"), "films");
  assert.equal(normalizeCategoryGroupKey("movie"), "films");
  assert.equal(normalizeCategoryGroupKey("serie"), "series");
  assert.equal(normalizeCategoryGroupKey("tv_series"), "series");
  assert.equal(normalizeCategoryGroupKey("series_tv"), "series");
  assert.equal(normalizeCategoryGroupKey("other"), null);
  assert.equal(normalizeCategoryGroupKey("totally-unknown"), null);
});

test("CATEGORY_GROUPS contains exactly the 10 canonical keys", () => {
  const expected = [
    "films",
    "series",
    "animation",
    "anime",
    "games",
    "comics",
    "books",
    "audio",
    "spectacle",
    "emissions",
  ];

  assert.equal(CATEGORY_GROUPS.length, 10);
  assert.deepEqual(CATEGORY_GROUPS.map((group) => group.key), expected);
  assert.equal(CATEGORY_GROUP_KEYS.size, 10);
});

test("buildMappingsPayload emits only canonical keys and canonical labels", () => {
  const mappings = new Map([
    [2000, "Films"],
    [5000, "serie"],
    [5080, "shows"],
    [9999, "other"],
    [0, "series"],
  ]);

  const payload = buildMappingsPayload(mappings);
  assert.equal(payload.length, 3);

  for (const row of payload) {
    assert.ok(CATEGORY_GROUP_KEYS.has(row.unifiedKey));
    assert.equal(row.unifiedLabel, CATEGORY_GROUP_LABELS[row.unifiedKey]);
    assert.equal(row.groupKey, row.unifiedKey);
    assert.equal(row.groupLabel, row.unifiedLabel);
  }
});

test("mapFromCapsAssignments canonicalizes assignedGroupKey values", () => {
  const categories = [
    { id: 1, assignedGroupKey: "shows" },
    { id: 2, assignedGroupKey: "Films" },
    { id: 3, assignedGroupKey: "serie" },
    { id: 4, assignedGroupKey: "other" },
    { id: 5, assignedGroupLabel: "Émissions" },
    { id: 6, assignedGroupKey: "anime", isAssigned: false },
  ];

  const map = mapFromCapsAssignments(categories);
  assert.equal(map.get(1), "emissions");
  assert.equal(map.get(2), "films");
  assert.equal(map.get(3), "series");
  assert.equal(map.get(4), undefined);
  assert.equal(map.get(5), "emissions");
  assert.equal(map.get(6), undefined);
});

