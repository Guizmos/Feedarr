/**
 * metadataProviders.model.js
 * Normalization helpers and validation utilities for metadata providers.
 * Pure functions — no side effects, no imports.
 */

/**
 * Build the authFlags property name for a given field key.
 * e.g. "apiKey" → "hasApiKey"
 * @param {string} fieldKey
 * @returns {string}
 */
export function toHasFlagName(fieldKey) {
  if (!fieldKey) return "";
  const trimmed = String(fieldKey).trim();
  if (!trimmed) return "";
  return `has${trimmed.charAt(0).toUpperCase()}${trimmed.slice(1)}`;
}

/**
 * Normalize a raw provider instance from the API into a predictable shape.
 * @param {*} raw
 * @returns {{ instanceId: string, providerKey: string, displayName: string, enabled: boolean, baseUrl: string, authFlags: object }}
 */
export function normalizeInstance(raw) {
  return {
    instanceId: String(raw?.instanceId || ""),
    providerKey: String(raw?.providerKey || "").toLowerCase(),
    displayName: raw?.displayName || "",
    enabled: raw?.enabled !== false,
    baseUrl: raw?.baseUrl || "",
    authFlags: raw?.authFlags && typeof raw.authFlags === "object" ? raw.authFlags : {},
  };
}

/**
 * Normalize a raw provider definition from the API into a predictable shape.
 * @param {*} raw
 * @returns {{ providerKey: string, displayName: string, defaultBaseUrl: string, kind: string, fieldsSchema: Array }}
 */
export function normalizeDefinition(raw) {
  return {
    providerKey: String(raw?.providerKey || "").toLowerCase(),
    displayName: raw?.displayName || "",
    defaultBaseUrl: raw?.defaultBaseUrl || "",
    kind: raw?.kind || "",
    fieldsSchema: Array.isArray(raw?.fieldsSchema) ? raw.fieldsSchema : [],
  };
}

/**
 * Check if an instance has all required authentication fields filled.
 * Uses the authFlags reported by the API (never exposes actual key values).
 * @param {{ authFlags?: object } | null} instance
 * @param {{ fieldsSchema?: Array<{ key: string, required?: boolean }> } | null} definition
 * @returns {boolean}
 */
export function hasRequiredAuth(instance, definition) {
  if (!definition) return false;
  const requiredFields = (definition.fieldsSchema || []).filter((f) => !!f.required);
  if (requiredFields.length === 0) return true;
  const authFlags = instance?.authFlags || {};
  return requiredFields.every((f) => !!authFlags[toHasFlagName(f.key)]);
}

/**
 * Return the list of missing required field keys for a given definition and auth payload.
 * In edit mode, a field already stored on the server (authFlags) counts as filled.
 * @param {object} auth - Auth values entered by the user { fieldKey: value }
 * @param {{ fieldsSchema?: Array } | null} definition
 * @param {object} [existingAuthFlags] - authFlags from the existing instance (edit mode)
 * @returns {string[]} Missing field keys
 */
export function getMissingRequiredFields(auth, definition, existingAuthFlags = {}) {
  if (!definition) return [];
  return (definition.fieldsSchema || [])
    .filter((field) => {
      if (!field.required) return false;
      const entered = String((auth || {})[field.key] || "").trim();
      if (entered) return false;
      return !existingAuthFlags[toHasFlagName(field.key)];
    })
    .map((field) => field.key);
}

/**
 * Build a CREATE payload for a new provider instance.
 * @param {{ providerKey: string, displayName?: string|null, enabled?: boolean, baseUrl?: string|null, auth?: object, options?: object }} params
 * @returns {object}
 */
export function buildCreatePayload({
  providerKey,
  displayName = null,
  enabled = true,
  baseUrl = null,
  auth = {},
  options = {},
}) {
  return {
    providerKey: String(providerKey || ""),
    displayName: displayName || null,
    enabled: enabled !== false,
    baseUrl: baseUrl || null,
    auth: auth && typeof auth === "object" ? auth : {},
    options: options && typeof options === "object" ? options : {},
  };
}

/**
 * Build an UPDATE payload for an existing provider instance.
 * Pass `auth` only if any field was changed — omitting it preserves the stored credentials.
 * @param {{ displayName?: string|null, enabled?: boolean, baseUrl?: string|null, auth?: object|null, options?: object }} params
 * @returns {object}
 */
export function buildUpdatePayload({
  displayName = null,
  enabled = true,
  baseUrl = null,
  auth = null,
  options = {},
}) {
  const payload = {
    displayName: displayName || null,
    enabled: enabled !== false,
    baseUrl: baseUrl || null,
    options: options && typeof options === "object" ? options : {},
  };
  if (auth && typeof auth === "object" && Object.keys(auth).length > 0) {
    payload.auth = auth;
  }
  return payload;
}
