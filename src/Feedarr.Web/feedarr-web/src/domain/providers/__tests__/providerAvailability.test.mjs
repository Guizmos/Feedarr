import test from "node:test";
import assert from "node:assert/strict";
import { describeProviderAvailability } from "../providerAvailability.js";
import { normalizeInstance } from "../metadataProviders.model.js";
const state = (status, reason, suspended = true) => normalizeInstance({ providerKey: "RAWG", instanceId: "rawg", authFlags: { hasApiKey: true }, availability: { status, reason, suspended } });
test("monthly quota is explicit, suspended, and never labelled an invalid key", () => {
  const view = describeProviderAvailability(state("quota_exhausted", "rawg_quota_exhausted"));
  assert.equal(view.fr, "Quota mensuel épuisé"); assert.equal(view.suspended, true);
  assert.match(view.detailFr, /Vérifier et réactiver/); assert.doesNotMatch(view.detailFr, /invalide/);
});
test("invalid authentication, unknown 401, and temporary failure stay distinct", () => {
  assert.equal(describeProviderAvailability(state("authentication_rejected", "rawg_invalid_api_key")).fr, "Authentification invalide");
  assert.equal(describeProviderAvailability(state("authentication_rejected", "rawg_http_unauthorized")).fr, "Accès non autorisé");
  const view = describeProviderAvailability(state("temporary_error", "rawg_http_server_error", false));
  assert.equal(view.fr, "Erreur temporaire"); assert.equal(view.suspended, false);
});
test("provider error payloads cannot become UI text and other providers are unchanged", () => {
  assert.doesNotMatch(JSON.stringify(describeProviderAvailability(state("temporary_error", "https://secret/?key=private-marker", false))), /private-marker/);
  assert.equal(describeProviderAvailability({ providerKey: "igdb", availability: { status: "quota_exhausted" } }), null);
  assert.equal(normalizeInstance({ providerKey: "igdb", availability: { status: "quota_exhausted" } }).availability, null);
});
test("successful recovery is shown as available", () => {
  const view = describeProviderAvailability(state("ready", null, false));
  assert.equal(view.fr, "Disponible"); assert.equal(view.warning, false); assert.equal(view.suspended, false);
});
