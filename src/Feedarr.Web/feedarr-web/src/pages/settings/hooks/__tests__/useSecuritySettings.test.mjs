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
    authMode: "strict",
    publicBaseUrl: "https://example.com/feedarr",
    username: "admin",
    password: "StrongP@ssw0rd!",
    passwordConfirmation: "StrongP@ssw0rd!",
  });

  assert.deepEqual(payload, {
    authMode: "strict",
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
      authMode: "smart",
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
      authMode: "open",
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
  assert.equal(calls[0].body.authMode, "open");
});

test("collectChangedSecurityKeys tracks dirty security fields", () => {
  const changed = collectChangedSecurityKeys(
    {
      authMode: "strict",
      publicBaseUrl: "https://example.com",
      username: "admin",
      password: "StrongP@ssw0rd!",
      passwordConfirmation: "StrongP@ssw0rd!",
    },
    {
      authMode: "smart",
      publicBaseUrl: "",
      username: "",
    },
  );

  assert.deepEqual(
    [...changed].sort(),
    [
      "security.authMode",
      "security.password",
      "security.passwordConfirmation",
      "security.publicBaseUrl",
      "security.username",
    ],
  );
});

test("normalizeSecurityResponse resets transient password fields", () => {
  const normalized = normalizeSecurityResponse({
    authMode: "strict",
    username: "admin",
    hasPassword: true,
  });

  assert.equal(normalized.authMode, "strict");
  assert.equal(normalized.username, "admin");
  assert.equal(normalized.password, "");
  assert.equal(normalized.passwordConfirmation, "");
  assert.equal(normalized.hasPassword, true);
});
