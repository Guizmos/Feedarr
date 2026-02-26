/**
 * useMetadataProviders.js
 * Shared domain hook for metadata provider management (TMDB, TVmaze, Fanart, IGDB, …).
 *
 * Wraps /api/providers/external CRUD + test operations with normalized React state.
 * Used by:
 *   - OnboardingWizard.jsx  (wizard step for providers)
 *   - useExternalProviderInstances.js  (settings page controller)
 *
 * Each returned action resolves to { ok, data?, errorMessage? } — never throws.
 */

import { useCallback, useMemo, useState } from "react";
import {
  createProviderInstance,
  deleteProviderInstance,
  fetchProvidersConfig,
  testProviderInstance,
  toggleProviderInstance,
  updateProviderInstance,
} from "./metadataProviders.api.js";
import { buildCreatePayload, buildUpdatePayload, hasRequiredAuth } from "./metadataProviders.model.js";

export default function useMetadataProviders() {
  const [definitions, setDefinitions] = useState([]);
  const [instances, setInstances] = useState([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState("");

  // ─── Load ────────────────────────────────────────────────────────────────

  /**
   * Load all provider definitions + instances.
   * Updates state and returns the result.
   * @returns {Promise<{ ok: boolean, errorMessage?: string }>}
   */
  const loadAll = useCallback(async () => {
    setLoading(true);
    setError("");
    const result = await fetchProvidersConfig();
    if (result.ok) {
      setDefinitions(result.data.definitions);
      setInstances(result.data.instances);
    } else {
      setError(result.errorMessage);
      setDefinitions([]);
      setInstances([]);
    }
    setLoading(false);
    return result;
  }, []);

  // ─── CRUD ─────────────────────────────────────────────────────────────────

  /**
   * Create a new instance and reload state.
   * @param {object} payload — use buildCreatePayload() helper
   * @returns {Promise<{ ok: boolean, data?: any, errorMessage?: string }>}
   */
  const create = useCallback(async (payload) => {
    const result = await createProviderInstance(payload);
    if (result.ok) await loadAll();
    return result;
  }, [loadAll]);

  /**
   * Update an existing instance and reload state.
   * @param {string} instanceId
   * @param {object} payload — use buildUpdatePayload() helper
   * @returns {Promise<{ ok: boolean, data?: any, errorMessage?: string }>}
   */
  const update = useCallback(async (instanceId, payload) => {
    const result = await updateProviderInstance(instanceId, payload);
    if (result.ok) await loadAll();
    return result;
  }, [loadAll]);

  /**
   * Delete an instance and reload state.
   * @param {string} instanceId
   * @returns {Promise<{ ok: boolean, errorMessage?: string }>}
   */
  const remove = useCallback(async (instanceId) => {
    const result = await deleteProviderInstance(instanceId);
    if (result.ok) await loadAll();
    return result;
  }, [loadAll]);

  /**
   * Test an instance (diagnostic only — does not reload state).
   * @param {string} instanceId
   * @returns {Promise<{ ok: boolean, errorMessage?: string }>}
   */
  const test = useCallback(async (instanceId) => {
    return testProviderInstance(instanceId);
  }, []);

  /**
   * Toggle enabled state for an instance and reload state.
   * @param {string} instanceId
   * @param {boolean} enabled
   * @returns {Promise<{ ok: boolean, errorMessage?: string }>}
   */
  const toggle = useCallback(async (instanceId, enabled) => {
    const result = await toggleProviderInstance(instanceId, enabled);
    if (result.ok) await loadAll();
    return result;
  }, [loadAll]);

  // ─── Compound operation for wizard flow ──────────────────────────────────

  /**
   * Create or update an instance as DISABLED (test-before-activate flow).
   * If an instance already exists for providerKey, updates it.
   * Otherwise creates a new one.
   * Does NOT reload state (caller decides when to reload).
   *
   * @param {string} providerKey
   * @param {object} auth - { fieldKey: value } — pass only non-empty fields
   * @param {{ displayName?: string, baseUrl?: string }} [opts]
   * @returns {Promise<{ ok: boolean, instanceId?: string, errorMessage?: string }>}
   */
  const upsertDisabled = useCallback(async (providerKey, auth, opts = {}) => {
    const key = String(providerKey || "").toLowerCase();
    const existing = instances.find(
      (i) => String(i?.providerKey || "").toLowerCase() === key
    );

    if (existing?.instanceId) {
      const result = await updateProviderInstance(
        existing.instanceId,
        buildUpdatePayload({
          displayName: opts.displayName || existing.displayName || null,
          enabled: false,
          baseUrl: opts.baseUrl || existing.baseUrl || null,
          auth: Object.keys(auth).length > 0 ? auth : null,
        })
      );
      if (result.ok) return { ok: true, instanceId: existing.instanceId };
      return result;
    }

    const result = await createProviderInstance(
      buildCreatePayload({
        providerKey: key,
        displayName: opts.displayName || null,
        enabled: false,
        baseUrl: opts.baseUrl || null,
        auth,
      })
    );
    if (result.ok) {
      const instanceId = String(result.data?.instanceId || result.data?.id || "");
      return { ok: true, instanceId };
    }
    return result;
  }, [instances]);

  // ─── Derived state ────────────────────────────────────────────────────────

  /** Map of providerKey → definition for O(1) lookups */
  const definitionByKey = useMemo(() => {
    const map = new Map();
    (definitions || []).forEach((def) => {
      map.set(String(def?.providerKey || "").toLowerCase(), def);
    });
    return map;
  }, [definitions]);

  /** Map of providerKey → instance for O(1) lookups */
  const instanceByKey = useMemo(() => {
    const map = new Map();
    (instances || []).forEach((inst) => {
      map.set(String(inst?.providerKey || "").toLowerCase(), inst);
    });
    return map;
  }, [instances]);

  /** Returns true if the instance has all required auth fields stored. */
  const isInstanceConfigured = useCallback((instance) => {
    const def = definitionByKey.get(String(instance?.providerKey || "").toLowerCase());
    return hasRequiredAuth(instance, def);
  }, [definitionByKey]);

  return {
    // State
    definitions,
    instances,
    loading,
    error,
    // Derived
    definitionByKey,
    instanceByKey,
    isInstanceConfigured,
    // Actions
    loadAll,
    create,
    update,
    remove,
    test,
    toggle,
    upsertDisabled,
  };
}
