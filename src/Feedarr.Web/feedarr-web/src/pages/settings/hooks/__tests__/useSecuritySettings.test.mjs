import test from "node:test";
import assert from "node:assert/strict";
import {
  buildSecurityPayload,
  collectChangedSecurityKeys,
  loadSecuritySettingsData,
  normalizeSecurityResponse,
  saveSecuritySettingsData,
} from "../useSecuritySettings.js";

test("buildSecurityPayload keeps security payload scoped to /api/settings/security", () => {
  const payload = buildSecurityPayload({
    requireHttps: true,
    publicBaseUrl: "https://example.com/feedarr",
    username: "admin",
    password: "StrongP@ssw0rd!",
    passwordConfirmation: "StrongP@ssw0rd!",
  });

  assert.deepEqual(payload, {
    requireHttps: true,
    publicBaseUrl: "https://example.com/feedarr",
    username: "admin",
    password: "StrongP@ssw0rd!",
    passwordConfirmation: "StrongP@ssw0rd!",
  });
});

test("loadSecuritySettingsData only requests /api/settings/security", async () => {
  const calls = [];
  const data = await loadSecuritySettingsData(async (path) => {
    calls.push(path);
    return {
      requireHttps: false,
      publicBaseUrl: "",
      username: "feedarr",
      hasPassword: true,
      authConfigured: true,
      authRequired: false,
    };
  });

  assert.deepEqual(calls, ["/api/settings/security"]);
  assert.equal(data.username, "feedarr");
  assert.equal(data.hasPassword, true);
});

test("saveSecuritySettingsData only requests /api/settings/security", async () => {
  const calls = [];
  await saveSecuritySettingsData(
    {
      requireHttps: false,
      publicBaseUrl: "",
      username: "",
    },
    async (path, body) => {
      calls.push({ path, body });
      return body;
    },
  );

  assert.equal(calls.length, 1);
  assert.equal(calls[0].path, "/api/settings/security");
  assert.equal(calls[0].body.requireHttps, false);
});

test("collectChangedSecurityKeys tracks dirty security fields", () => {
  const changed = collectChangedSecurityKeys(
    {
      requireHttps: true,
      publicBaseUrl: "https://example.com",
      username: "admin",
      password: "StrongP@ssw0rd!",
      passwordConfirmation: "StrongP@ssw0rd!",
    },
    {
      requireHttps: false,
      publicBaseUrl: "",
      username: "",
    },
  );

  assert.deepEqual(
    [...changed].sort(),
    [
      "security.password",
      "security.passwordConfirmation",
      "security.publicBaseUrl",
      "security.requireHttps",
      "security.username",
    ],
  );
});

test("normalizeSecurityResponse resets transient password fields", () => {
  const normalized = normalizeSecurityResponse({
    requireHttps: true,
    username: "admin",
    hasPassword: true,
  });

  assert.equal(normalized.requireHttps, true);
  assert.equal(normalized.username, "admin");
  assert.equal(normalized.password, "");
  assert.equal(normalized.passwordConfirmation, "");
  assert.equal(normalized.hasPassword, true);
});
