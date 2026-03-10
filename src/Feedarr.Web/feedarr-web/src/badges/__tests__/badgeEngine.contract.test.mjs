import test from "node:test";
import assert from "node:assert/strict";
import {
  badgeRegistry,
  getBadgeDefinition,
  selectRouteSeenKeys,
} from "../badgeRegistry.js";
import {
  computeBadgeSnapshot,
  hydrateSeenState,
  mergeSeenEntry,
  mergeSeenState,
  normalizePath,
  persistSeenEntry,
} from "../useBadgeStore.js";

function createStorage(seed = {}) {
  const map = new Map(Object.entries(seed));
  return {
    getItem(key) {
      return map.has(key) ? map.get(key) : null;
    },
    setItem(key, value) {
      map.set(key, String(value));
    },
    removeItem(key) {
      map.delete(key);
    },
    dump() {
      return Object.fromEntries(map.entries());
    },
  };
}

function latestTemplate() {
  return {
    releases: { cursor: 1700000010, meta: { totalCount: 42, newSinceTsCount: 2 } },
    activity: { cursor: 1700001000, meta: { unreadCount: 3, tone: "warn" } },
    updates: {
      cursor: "v1.2.3",
      meta: {
        isUpdateAvailable: true,
        latestRelease: { tagName: "v1.2.3" },
        releases: [],
        enabled: true,
        checkIntervalHours: 6,
        currentVersion: "1.2.2",
      },
    },
  };
}

test("computeBadgeSnapshot returns 0/false when latest cursor equals seen cursor", () => {
  const latest = latestTemplate();
  const seen = {
    releases: { cursor: 1700000010, meta: { value: 42 } },
    activity: { cursor: 1700001000, meta: {} },
    updates: { cursor: "v1.2.3", meta: { value: 100 } },
  };

  const computed = computeBadgeSnapshot(badgeRegistry, latest, seen);
  assert.equal(computed.releases.value, 0);
  assert.equal(computed.activity.value, 0);
  assert.equal(computed.updates.value, false);
});

test("computeBadgeSnapshot returns non-zero / true when latest cursor is newer", () => {
  const latest = latestTemplate();
  const seen = {
    releases: { cursor: 1700000000, meta: { value: 40 } },
    activity: { cursor: 1700000000, meta: {} },
    updates: { cursor: "v1.2.2", meta: { value: 100 } },
  };

  const computed = computeBadgeSnapshot(badgeRegistry, latest, seen);
  assert.equal(computed.releases.value, 2);
  assert.equal(computed.activity.value, 3);
  assert.equal(computed.updates.value, true);
});

test("hydrateSeenState + persistSeenEntry roundtrip localStorage", () => {
  const updatesDef = getBadgeDefinition("updates");
  const releasesDef = getBadgeDefinition("releases");
  const storage = createStorage({
    "feedarr:lastSeen:releases_ts": "1700000000",
    "feedarr:lastSeen:releases": "39",
    "feedarr:lastSeenReleaseTag": "v1.2.2",
    "feedarr:lastSeenReleaseTag:ts": "1234",
  });

  const seen = hydrateSeenState(badgeRegistry, storage);
  assert.equal(seen.releases.cursor, 1700000000);
  assert.equal(seen.releases.meta.value, 39);
  assert.equal(seen.updates.cursor, "v1.2.2");
  assert.equal(seen.updates.meta.value, 1234);

  persistSeenEntry(releasesDef, { cursor: 1700001111, meta: { value: 40 } }, storage);
  persistSeenEntry(updatesDef, { cursor: "v1.2.3", meta: { value: 2222 } }, storage);

  const dumped = storage.dump();
  assert.equal(dumped["feedarr:lastSeen:releases_ts"], "1700001111");
  assert.equal(dumped["feedarr:lastSeen:releases"], "40");
  assert.equal(dumped["feedarr:lastSeenReleaseTag"], "v1.2.3");
  assert.equal(dumped["feedarr:lastSeenReleaseTag:ts"], "2222");
});

test("route enter marks seen only when latest cursor is available", () => {
  const path = normalizePath("/feedarr/activity/", "/feedarr/");
  const noLatest = selectRouteSeenKeys({
    normalizedPath: path,
    latestByKey: {
      activity: { cursor: 0, meta: {} },
      releases: { cursor: 0, meta: {} },
      updates: { cursor: "", meta: {} },
    },
  });
  assert.deepEqual(noLatest, []);

  const withLatest = selectRouteSeenKeys({
    normalizedPath: path,
    latestByKey: {
      activity: { cursor: 1700002000, meta: {} },
      releases: { cursor: 0, meta: {} },
      updates: { cursor: "", meta: {} },
    },
  });
  assert.deepEqual(withLatest, ["activity"]);
});

test("normalizePath handles basename and trailing slash (prod-safe)", () => {
  assert.equal(normalizePath("/feedarr/library/", "/feedarr/"), "/library");
  assert.equal(normalizePath("feedarr/library", "/feedarr"), "/library");
  assert.equal(normalizePath("/library/", "/"), "/library");
  assert.equal(normalizePath("/feedarr", "/feedarr/"), "/");
});

test("mergeSeenState is monotonic for numeric cursors and update tag seenAt", () => {
  const current = {
    releases: { cursor: 1700000100, meta: { value: 50 } },
    activity: { cursor: 1700000300, meta: {} },
    updates: { cursor: "v1.2.3", meta: { value: 200 } },
  };

  const olderPatch = {
    releases: { cursor: 1700000000, meta: { value: 49 } },
    activity: { cursor: 1700000200, meta: {} },
    updates: { cursor: "v1.2.2", meta: { value: 100 } },
  };
  const mergedOlder = mergeSeenState(badgeRegistry, current, olderPatch);
  assert.equal(mergedOlder.releases.cursor, 1700000100);
  assert.equal(mergedOlder.activity.cursor, 1700000300);
  assert.equal(mergedOlder.updates.cursor, "v1.2.3");

  const newerPatch = {
    releases: { cursor: 1700000400, meta: { value: 51 } },
    activity: { cursor: 1700000401, meta: {} },
    updates: { cursor: "v1.2.4", meta: { value: 300 } },
  };
  const mergedNewer = mergeSeenState(badgeRegistry, current, newerPatch);
  assert.equal(mergedNewer.releases.cursor, 1700000400);
  assert.equal(mergedNewer.releases.meta.value, 51);
  assert.equal(mergedNewer.activity.cursor, 1700000401);
  assert.equal(mergedNewer.updates.cursor, "v1.2.4");
});

test("mergeSeenEntry keeps current update tag when incoming seenAt is stale", () => {
  const updatesDef = getBadgeDefinition("updates");
  const current = { cursor: "v2.0.0", meta: { value: 500 } };
  const incoming = { cursor: "v1.9.9", meta: { value: 499 } };
  const merged = mergeSeenEntry(updatesDef, current, incoming);
  assert.equal(merged.cursor, "v2.0.0");
  assert.equal(merged.meta.value, 500);
});

test("no reappear after seen on internal nav without new backend data", () => {
  const latest = latestTemplate();
  const unseen = {
    releases: { cursor: 0, meta: { value: 0 } },
    activity: { cursor: 0, meta: {} },
    updates: { cursor: "", meta: { value: 0 } },
  };
  const seen = {
    releases: { cursor: latest.releases.cursor, meta: { value: latest.releases.meta.totalCount } },
    activity: { cursor: latest.activity.cursor, meta: {} },
    updates: { cursor: latest.updates.cursor, meta: { value: 999 } },
  };

  const before = computeBadgeSnapshot(badgeRegistry, latest, unseen);
  const after = computeBadgeSnapshot(badgeRegistry, latest, seen);
  const afterInternalNav = computeBadgeSnapshot(badgeRegistry, latest, seen);

  assert.equal(before.releases.value > 0, true);
  assert.equal(before.activity.value > 0, true);
  assert.equal(before.updates.value, true);

  assert.equal(after.releases.value, 0);
  assert.equal(after.activity.value, 0);
  assert.equal(after.updates.value, false);

  assert.equal(afterInternalNav.releases.value, 0);
  assert.equal(afterInternalNav.activity.value, 0);
  assert.equal(afterInternalNav.updates.value, false);
});

