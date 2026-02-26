import test from "node:test";
import assert from "node:assert/strict";
import {
  normalizeInstance,
  normalizeDefinition,
  toHasFlagName,
  hasRequiredAuth,
  getMissingRequiredFields,
  buildCreatePayload,
  buildUpdatePayload,
} from "../metadataProviders.model.js";

// ─── toHasFlagName ────────────────────────────────────────────────────────────

test("toHasFlagName generates correct flag name", () => {
  assert.equal(toHasFlagName("apiKey"), "hasApiKey");
  assert.equal(toHasFlagName("clientId"), "hasClientId");
  assert.equal(toHasFlagName("clientSecret"), "hasClientSecret");
  assert.equal(toHasFlagName(""), "");
  assert.equal(toHasFlagName(null), "");
});

// ─── normalizeInstance ────────────────────────────────────────────────────────

test("normalizeInstance fills all fields with defaults", () => {
  const result = normalizeInstance({});
  assert.equal(result.instanceId, "");
  assert.equal(result.providerKey, "");
  assert.equal(result.displayName, "");
  assert.equal(result.enabled, true);
  assert.equal(result.baseUrl, "");
  assert.deepEqual(result.authFlags, {});
});

test("normalizeInstance lowercases providerKey", () => {
  const result = normalizeInstance({ providerKey: "TMDB", instanceId: "abc-123", enabled: false });
  assert.equal(result.providerKey, "tmdb");
  assert.equal(result.instanceId, "abc-123");
  assert.equal(result.enabled, false);
});

test("normalizeInstance preserves authFlags", () => {
  const authFlags = { hasApiKey: true };
  const result = normalizeInstance({ providerKey: "tmdb", authFlags });
  assert.deepEqual(result.authFlags, { hasApiKey: true });
});

// ─── normalizeDefinition ──────────────────────────────────────────────────────

test("normalizeDefinition fills all fields with defaults", () => {
  const result = normalizeDefinition({});
  assert.equal(result.providerKey, "");
  assert.equal(result.displayName, "");
  assert.equal(result.defaultBaseUrl, "");
  assert.equal(result.kind, "");
  assert.deepEqual(result.fieldsSchema, []);
});

test("normalizeDefinition lowercases providerKey", () => {
  const result = normalizeDefinition({ providerKey: "IGDB", displayName: "IGDB" });
  assert.equal(result.providerKey, "igdb");
  assert.equal(result.displayName, "IGDB");
});

// ─── hasRequiredAuth ──────────────────────────────────────────────────────────

test("hasRequiredAuth returns false when definition is null", () => {
  assert.equal(hasRequiredAuth({}, null), false);
});

test("hasRequiredAuth returns true when no required fields", () => {
  const definition = { fieldsSchema: [{ key: "apiKey", required: false }] };
  assert.equal(hasRequiredAuth({}, definition), true);
});

test("hasRequiredAuth returns false when required field is missing", () => {
  const definition = { fieldsSchema: [{ key: "apiKey", required: true }] };
  const instance = { authFlags: { hasApiKey: false } };
  assert.equal(hasRequiredAuth(instance, definition), false);
});

test("hasRequiredAuth returns true when all required fields are present", () => {
  const definition = {
    fieldsSchema: [
      { key: "clientId", required: true },
      { key: "clientSecret", required: true },
    ],
  };
  const instance = { authFlags: { hasClientId: true, hasClientSecret: true } };
  assert.equal(hasRequiredAuth(instance, definition), true);
});

test("hasRequiredAuth returns false when only some required fields are present", () => {
  const definition = {
    fieldsSchema: [
      { key: "clientId", required: true },
      { key: "clientSecret", required: true },
    ],
  };
  const instance = { authFlags: { hasClientId: true, hasClientSecret: false } };
  assert.equal(hasRequiredAuth(instance, definition), false);
});

// ─── getMissingRequiredFields ─────────────────────────────────────────────────

test("getMissingRequiredFields returns empty array when definition is null", () => {
  assert.deepEqual(getMissingRequiredFields({}, null), []);
});

test("getMissingRequiredFields returns missing required fields", () => {
  const definition = {
    fieldsSchema: [
      { key: "apiKey", required: true },
      { key: "secret", required: true },
    ],
  };
  const missing = getMissingRequiredFields({ apiKey: "abc" }, definition);
  assert.deepEqual(missing, ["secret"]);
});

test("getMissingRequiredFields ignores optional fields", () => {
  const definition = {
    fieldsSchema: [
      { key: "apiKey", required: true },
      { key: "notes", required: false },
    ],
  };
  const missing = getMissingRequiredFields({}, definition);
  assert.deepEqual(missing, ["apiKey"]);
});

test("getMissingRequiredFields respects existing authFlags in edit mode", () => {
  const definition = {
    fieldsSchema: [{ key: "apiKey", required: true }],
  };
  // User didn't re-enter the key, but it's already stored (hasApiKey = true)
  const missing = getMissingRequiredFields({}, definition, { hasApiKey: true });
  assert.deepEqual(missing, []);
});

// ─── buildCreatePayload ───────────────────────────────────────────────────────

test("buildCreatePayload produces correct structure", () => {
  const payload = buildCreatePayload({
    providerKey: "tmdb",
    auth: { apiKey: "mykey" },
    enabled: true,
  });
  assert.equal(payload.providerKey, "tmdb");
  assert.equal(payload.enabled, true);
  assert.deepEqual(payload.auth, { apiKey: "mykey" });
  assert.equal(payload.displayName, null);
  assert.equal(payload.baseUrl, null);
  assert.deepEqual(payload.options, {});
});

test("buildCreatePayload defaults enabled to true", () => {
  const payload = buildCreatePayload({ providerKey: "fanart", auth: {} });
  assert.equal(payload.enabled, true);
});

// ─── buildUpdatePayload ───────────────────────────────────────────────────────

test("buildUpdatePayload omits auth when null or empty", () => {
  const payload = buildUpdatePayload({ enabled: true });
  assert.equal("auth" in payload, false);
});

test("buildUpdatePayload includes auth when non-empty", () => {
  const payload = buildUpdatePayload({ enabled: true, auth: { apiKey: "newkey" } });
  assert.deepEqual(payload.auth, { apiKey: "newkey" });
});

test("buildUpdatePayload preserves enabled false", () => {
  const payload = buildUpdatePayload({ enabled: false });
  assert.equal(payload.enabled, false);
});
