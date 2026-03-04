import test from "node:test";
import assert from "node:assert/strict";
import {
  buildArrPayload,
  collectChangedArrKeys,
  loadArrSettingsData,
  normalizeArrResponse,
  normalizeArrValidationErrors,
  saveArrSettingsData,
} from "../useArrSettings.js";

test("buildArrPayload normalizes ARR settings payload", () => {
  const payload = buildArrPayload({
    arrSyncIntervalMinutes: 5000,
    arrAutoSyncEnabled: 0,
    requestIntegrationMode: "OVERSEERR",
  });

  assert.equal(payload.arrSyncIntervalMinutes, 1440);
  assert.equal(payload.arrAutoSyncEnabled, false);
  assert.equal(payload.requestIntegrationMode, "overseerr");
});

test("loadArrSettingsData only requests /api/settings/arr", async () => {
  const calls = [];
  const data = await loadArrSettingsData(async (path) => {
    calls.push(path);
    return {
      arrSyncIntervalMinutes: 15,
      arrAutoSyncEnabled: false,
      requestIntegrationMode: "seer",
    };
  });

  assert.deepEqual(calls, ["/api/settings/arr"]);
  assert.equal(data.arrSyncIntervalMinutes, 15);
  assert.equal(data.arrAutoSyncEnabled, false);
  assert.equal(data.requestIntegrationMode, "seer");
});

test("saveArrSettingsData only requests /api/settings/arr", async () => {
  const calls = [];
  await saveArrSettingsData(
    {
      arrSyncIntervalMinutes: 20,
      arrAutoSyncEnabled: true,
      requestIntegrationMode: "jellyseerr",
    },
    async (path, body) => {
      calls.push({ path, body });
      return body;
    },
  );

  assert.equal(calls.length, 1);
  assert.equal(calls[0].path, "/api/settings/arr");
  assert.equal(calls[0].body.requestIntegrationMode, "jellyseerr");
});

test("normalizeArrValidationErrors maps inline field errors", () => {
  const errors = normalizeArrValidationErrors({
    extensions: {
      errors: {
        arrSyncIntervalMinutes: ["Intervalle invalide"],
        requestIntegrationMode: ["Mode invalide"],
      },
    },
  });

  assert.deepEqual(errors, {
    arrSyncIntervalMinutes: "Intervalle invalide",
    requestIntegrationMode: "Mode invalide",
  });
});

test("collectChangedArrKeys tracks dirty fields", () => {
  const changed = collectChangedArrKeys(
    buildArrPayload({
      arrSyncIntervalMinutes: 10,
      arrAutoSyncEnabled: false,
      requestIntegrationMode: "seer",
    }),
    buildArrPayload({}),
  );

  assert.deepEqual(
    [...changed].sort(),
    [
      "arr.arrAutoSyncEnabled",
      "arr.arrSyncIntervalMinutes",
      "arr.requestIntegrationMode",
    ],
  );
});

test("normalizeArrResponse keeps ARR settings normalized", () => {
  const normalized = normalizeArrResponse({
    arrSyncIntervalMinutes: 0,
    arrAutoSyncEnabled: true,
    requestIntegrationMode: "plex",
  });

  assert.equal(normalized.arrSyncIntervalMinutes, 1);
  assert.equal(normalized.arrAutoSyncEnabled, true);
  assert.equal(normalized.requestIntegrationMode, "arr");
});
