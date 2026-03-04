import test from "node:test";
import assert from "node:assert/strict";
import {
  buildMaintenancePayload,
  buildMaintenanceState,
  getMaintenancePerformanceNotice,
  getVisibleMaintenanceProviderRows,
} from "../useMaintenanceSettings.js";

test("buildMaintenancePayload normalizes and clamps maintenance settings", () => {
  const payload = buildMaintenancePayload({
    maintenanceAdvancedOptionsEnabled: 1,
    syncSourcesMaxConcurrency: 9,
    posterWorkers: 0,
    providerRateLimitMode: "MANUAL",
    providerConcurrencyManual: {
      tmdb: 99,
      igdb: -1,
      fanart: 2,
      tvmaze: 3,
      others: 0,
    },
    syncRunTimeoutMinutes: 99,
  });

  assert.equal(payload.maintenanceAdvancedOptionsEnabled, true);
  assert.equal(payload.syncSourcesMaxConcurrency, 4);
  assert.equal(payload.posterWorkers, 1);
  assert.equal(payload.providerRateLimitMode, "manual");
  assert.deepEqual(payload.providerConcurrencyManual, {
    tmdb: 3,
    igdb: 1,
    fanart: 2,
    tvmaze: 2,
    others: 1,
  });
  assert.equal(payload.syncRunTimeoutMinutes, 30);
});

test("getMaintenancePerformanceNotice returns danger for poster workers with manual rate limits", () => {
  const notice = getMaintenancePerformanceNotice({
    syncSourcesMaxConcurrency: 2,
    posterWorkers: 2,
    providerRateLimitMode: "manual",
  });

  assert.equal(notice.tone, "danger");
});

test("getMaintenancePerformanceNotice returns warning for high source concurrency", () => {
  const notice = getMaintenancePerformanceNotice({
    syncSourcesMaxConcurrency: 3,
    posterWorkers: 1,
    providerRateLimitMode: "auto",
  });

  assert.equal(notice.tone, "warning");
});

test("getMaintenancePerformanceNotice returns info for single source concurrency", () => {
  const notice = getMaintenancePerformanceNotice({
    syncSourcesMaxConcurrency: 1,
  });

  assert.equal(notice.tone, "info");
});

test("buildMaintenanceState preserves configured providers from backend", () => {
  const state = buildMaintenanceState({
    configuredProviders: ["TMDB", "igdb", "tmdb", ""],
  });

  assert.deepEqual(state.configuredProviders, ["tmdb", "igdb"]);
});

test("getVisibleMaintenanceProviderRows filters to configured providers and keeps others row", () => {
  const visible = getVisibleMaintenanceProviderRows(
    { configuredProviders: ["tmdb"] },
    [
      { key: "tmdb" },
      { key: "igdb" },
      { key: "others", alwaysVisible: true },
    ],
  );

  assert.deepEqual(
    visible.map((item) => item.key),
    ["tmdb", "others"],
  );
});
