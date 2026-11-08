import test from "node:test";
import assert from "node:assert/strict";
import { findPersistedIntegration, normalizeIntegrationUrl } from "./persistedIntegration.js";

for (const [input, persisted] of [
  ["10.50.0.15:5055", "http://10.50.0.15:5055"],
  ["  SEER:5055/Base///  ", "http://seer:5055/Base"],
  ["http://seer:80/", "http://seer"],
  ["https://seer:443/Base/", "https://SEER/Base"],
  ["[fd00::15]:5055/Base", "http://[fd00::15]:5055/Base"],
  ["http://seer/%7euser", "http://seer/~user"],
  ["http://seer/a%2fb", "http://seer/a%2Fb"],
]) {
  test(`comparison matches backend normalization: ${input}`, () => {
    assert.equal(normalizeIntegrationUrl(input), normalizeIntegrationUrl(persisted));
  });
}

for (const [left, right] of [
  ["http://seer:5055", "https://seer:5055"],
  ["http://seer:5055", "http://seer:5056"],
  ["http://seer/Base", "http://seer/base"],
  ["http://seer/Base", "http://seer/Other"],
  ["http://seer", "http://other"],
  ["http://seer", "http://sub.seer"],
  ["http://seer/a%2Fb", "http://seer/a/b"],
  ["http://seer/api?key=a", "http://seer/api?key=b"],
]) {
  test(`comparison preserves distinct destinations: ${left} versus ${right}`, () => {
    assert.notEqual(normalizeIntegrationUrl(left), normalizeIntegrationUrl(right));
  });
}

test("empty, invalid and non-HTTP values cannot match a persisted entry", () => {
  for (const input of ["", null, "http://", "ftp://seer", "http://user:pass@seer", "http://seer/#fragment"]) {
    assert.equal(normalizeIntegrationUrl(input), null);
    assert.equal(findPersistedIntegration([{ id: 1, baseUrl: input }], { url: input }), undefined);
  }
});

test("known IDs win over edited URLs, list order and ambiguous matches", () => {
  const first = { id: 1, type: "seer", baseUrl: "http://10.50.0.15:5055" };
  const edited = { id: 2, type: "seer", baseUrl: "http://old-host:5055" };
  assert.equal(findPersistedIntegration([first, edited], { id: "2", type: "seer", url: first.baseUrl }), edited);
  assert.equal(findPersistedIntegration([first, edited], { id: 9, type: "seer", url: first.baseUrl }), undefined);
  assert.equal(findPersistedIntegration([first], { id: 1, type: "radarr", url: first.baseUrl }), undefined);
});

test("fallback matching is unique, type-specific and supports source URLs", () => {
  const app = { id: 1, type: "seer", baseUrl: "http://10.50.0.15:5055" };
  const otherType = { ...app, id: 2, type: "sonarr" };
  assert.equal(findPersistedIntegration([otherType, app], { type: "seer", url: "10.50.0.15:5055" }), app);
  assert.equal(findPersistedIntegration([app, { ...app, id: 3 }], { type: "seer", url: app.baseUrl }), undefined);
  const source = { id: 7, torznabUrl: "http://prowlarr:9696/1/api" };
  assert.equal(findPersistedIntegration([source], { url: "prowlarr:9696/1/api/", urlField: "torznabUrl" }), source);
});
