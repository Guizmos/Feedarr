/**
 * metadataProviders.api.js
 * Pure API wrappers for /api/providers/external.
 * Every function returns { ok: true, data? } or { ok: false, errorMessage }.
 * Nothing is thrown — callers check result.ok.
 */

import { apiDelete, apiGet, apiPost, apiPut } from "../../api/client.js";

/**
 * Load all provider definitions + instances.
 * @returns {{ ok: true, data: { definitions: any[], instances: any[] } } | { ok: false, errorMessage: string }}
 */
export async function fetchProvidersConfig() {
  try {
    const data = await apiGet("/api/providers/external");
    return {
      ok: true,
      data: {
        definitions: Array.isArray(data?.definitions) ? data.definitions : [],
        instances: Array.isArray(data?.instances) ? data.instances : [],
      },
    };
  } catch (e) {
    console.error("[providers.api] fetchProvidersConfig failed", e);
    return { ok: false, errorMessage: e?.message || "Chargement impossible." };
  }
}

/**
 * Create a new provider instance.
 * @param {{ providerKey, displayName?, enabled, baseUrl?, auth, options? }} payload
 * @returns {{ ok: true, data: any } | { ok: false, errorMessage: string }}
 */
export async function createProviderInstance(payload) {
  try {
    const data = await apiPost("/api/providers/external", payload);
    return { ok: true, data };
  } catch (e) {
    console.error("[providers.api] createProviderInstance failed", e);
    return { ok: false, errorMessage: e?.message || "Sauvegarde impossible." };
  }
}

/**
 * Update an existing provider instance.
 * @param {string} instanceId
 * @param {{ displayName?, enabled, baseUrl?, auth?, options? }} payload
 * @returns {{ ok: true, data: any } | { ok: false, errorMessage: string }}
 */
export async function updateProviderInstance(instanceId, payload) {
  try {
    const data = await apiPut(`/api/providers/external/${instanceId}`, payload);
    return { ok: true, data };
  } catch (e) {
    console.error("[providers.api] updateProviderInstance failed", e);
    return { ok: false, errorMessage: e?.message || "Sauvegarde impossible." };
  }
}

/**
 * Delete a provider instance.
 * @param {string} instanceId
 * @returns {{ ok: true } | { ok: false, errorMessage: string }}
 */
export async function deleteProviderInstance(instanceId) {
  try {
    await apiDelete(`/api/providers/external/${instanceId}`);
    return { ok: true };
  } catch (e) {
    console.error("[providers.api] deleteProviderInstance failed", e);
    return { ok: false, errorMessage: e?.message || "Suppression impossible." };
  }
}

/**
 * Test a provider instance connectivity.
 * @param {string} instanceId
 * @returns {{ ok: true } | { ok: false, errorMessage: string }}
 */
export async function testProviderInstance(instanceId) {
  try {
    const res = await apiPost(`/api/providers/external/${instanceId}/test`);
    if (res?.ok === false) {
      return { ok: false, errorMessage: res?.error || "Test échoué." };
    }
    return { ok: true };
  } catch (e) {
    console.error("[providers.api] testProviderInstance failed", e);
    return { ok: false, errorMessage: e?.message || "Test échoué." };
  }
}

/**
 * Toggle enabled state of a provider instance.
 * @param {string} instanceId
 * @param {boolean} enabled
 * @returns {{ ok: true } | { ok: false, errorMessage: string }}
 */
export async function toggleProviderInstance(instanceId, enabled) {
  try {
    await apiPut(`/api/providers/external/${instanceId}`, { enabled });
    return { ok: true };
  } catch (e) {
    console.error("[providers.api] toggleProviderInstance failed", e);
    return { ok: false, errorMessage: e?.message || "Activation impossible." };
  }
}
