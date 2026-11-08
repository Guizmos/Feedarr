import test from "node:test";
import assert from "node:assert/strict";
import { discoveryFailureState, integrationSaveErrorMessage, isSsrfBlocked, shouldRetryDiscovery, ssrfBlockedMessage } from "./ssrfErrors.js";

test("SSRF decisions use the structured code, never a generic HTTP 403 or message", () => {
  for (const error of [{ code: "ssrf_blocked" }, { extensions: { code: "ssrf_blocked" } }, { payload: { code: "ssrf_blocked" } }]) {
    assert.equal(isSsrfBlocked(error), true);
    assert.equal(discoveryFailureState(error), "error");
    assert.equal(shouldRetryDiscovery({ ...error, message: "unexpected token <" }, 0), false);
  }
  assert.equal(discoveryFailureState({ status: 403, message: "ssrf_blocked" }), "manual");
  assert.equal(discoveryFailureState(new Error("provider unavailable")), "manual");
});

test("ordinary discovery JSON failures keep their single retry", () => {
  assert.equal(shouldRetryDiscovery(new Error("Unexpected token <"), 0), true);
  assert.equal(shouldRetryDiscovery(new Error("Unexpected token <"), 1), false);
  assert.equal(shouldRetryDiscovery(new Error("provider timeout"), 0), false);
});

test("SSRF guidance follows FR and EN translations", () => {
  assert.match(ssrfBlockedMessage("fr-FR"), /^Connexion bloquée/);
  assert.match(ssrfBlockedMessage("en-US"), /^Connection blocked/);
  for (const language of ["fr-FR", "en-US"]) {
    assert.match(ssrfBlockedMessage(language), /TrustedHosts.*TrustedNetworks/);
  }
});

test("failed writes distinguish saved configuration from validated CAPS and sync", () => {
  assert.match(integrationSaveErrorMessage({ code: "ssrf_blocked" }), /source peut déjà être enregistrée.*CAPS.*pas été validés/);
  assert.match(integrationSaveErrorMessage({ code: "ssrf_blocked" }, "application"), /application peut déjà être enregistrée/);
  assert.equal(integrationSaveErrorMessage(new Error("ordinary error")), "ordinary error");
});
