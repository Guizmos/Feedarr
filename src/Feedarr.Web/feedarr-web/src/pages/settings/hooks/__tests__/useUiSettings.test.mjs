import test from "node:test";
import assert from "node:assert/strict";
import {
  buildUiPayload,
  collectChangedUiKeys,
  loadUiSettingsData,
  normalizeUiResponse,
  normalizeUiValidationErrors,
  saveUiSettingsData,
} from "../useUiSettings.js";

test("buildUiPayload normalizes UI settings payload", () => {
  const payload = buildUiPayload({
    uiLanguage: "en-US",
    mediaInfoLanguage: "fr-FR",
    defaultLimit: null,
    animationsEnabled: false,
    badgeWarn: 0,
    showTop24DedupeControl: 1,
  });

  assert.equal(payload.uiLanguage, "en-US");
  assert.equal(payload.mediaInfoLanguage, "fr-FR");
  assert.equal(payload.defaultLimit, 100);
  assert.equal(payload.animationsEnabled, false);
  assert.equal(payload.badgeWarn, false);
  assert.equal(payload.showTop24DedupeControl, true);
});

test("loadUiSettingsData only requests /api/settings/ui", async () => {
  const calls = [];
  const data = await loadUiSettingsData(async (path) => {
    calls.push(path);
    return {
      uiLanguage: "en-US",
      sourceOptions: [{ value: "7", label: "Alpha" }],
      appOptions: [{ value: "4", label: "sonarr 4" }],
      categoryOptions: [{ value: "films", label: "Films", count: 12 }],
    };
  });

  assert.deepEqual(calls, ["/api/settings/ui"]);
  assert.equal(data.ui.uiLanguage, "en-US");
  assert.deepEqual(data.sourceOptions, [{ value: "7", label: "Alpha" }]);
  assert.deepEqual(data.appOptions, [{ value: "4", label: "sonarr 4" }]);
  assert.deepEqual(data.categoryOptions, [{ value: "films", label: "Films", count: 12 }]);
});

test("saveUiSettingsData only requests /api/settings/ui", async () => {
  const calls = [];
  await saveUiSettingsData(
    {
      theme: "dark",
      defaultView: "poster",
    },
    async (path, body) => {
      calls.push({ path, body });
      return body;
    },
  );

  assert.equal(calls.length, 1);
  assert.equal(calls[0].path, "/api/settings/ui");
  assert.equal(calls[0].body.theme, "dark");
  assert.equal(calls[0].body.defaultView, "poster");
});

test("normalizeUiValidationErrors maps inline field errors", () => {
  const errors = normalizeUiValidationErrors({
    extensions: {
      errors: {
        defaultView: ["Vue invalide"],
        theme: ["Theme invalide"],
      },
    },
  });

  assert.deepEqual(errors, {
    defaultView: "Vue invalide",
    theme: "Theme invalide",
  });
});

test("collectChangedUiKeys tracks dirty fields", () => {
  const changed = collectChangedUiKeys(
    buildUiPayload({
      theme: "dark",
      defaultSort: "downloads",
      badgeInfo: true,
    }),
    buildUiPayload({}),
  );

  assert.deepEqual(
    [...changed].sort(),
    ["ui.badgeInfo", "ui.defaultSort", "ui.theme"],
  );
});

test("normalizeUiResponse keeps top-level ui fields and options", () => {
  const normalized = normalizeUiResponse({
    theme: "system",
    sourceOptions: [{ value: "1", label: "Main" }, { value: "", label: "skip" }],
    categoryOptions: [{ value: "series", label: "Series", count: 3 }],
  });

  assert.equal(normalized.ui.theme, "system");
  assert.deepEqual(normalized.sourceOptions, [{ value: "1", label: "Main" }]);
  assert.deepEqual(normalized.categoryOptions, [{ value: "series", label: "Series", count: 3 }]);
});
