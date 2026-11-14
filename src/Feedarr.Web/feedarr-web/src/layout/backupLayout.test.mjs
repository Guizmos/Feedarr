import test from "node:test";
import assert from "node:assert/strict";
import { act } from "react";
import { fixture } from "../test/authDomFixture.mjs";

const json = body => Response.json(body);
function fetcher(url) {
  const path = new URL(url, "http://feedarr.test").pathname;
  if (path === "/api/auth/session") return Promise.resolve(json({ authenticated: true, authRequired: true, authConfigured: true, csrfToken: "fixture-csrf", initialAdminRequired: false, setupCompleted: true }));
  if (path === "/api/settings/ui") return Promise.resolve(json({ theme: "light", uiLanguage: "fr-FR", defaultView: "grid" }));
  if (path === "/api/feed/top") return Promise.resolve(json({ globalTop: [], categories: [], window: { sinceUtc: "2026-11-12T20:04:00Z" } }));
  if (path === "/api/releases") return Promise.resolve(json({ items: [], total: 0 }));
  if (["/api/sources", "/api/apps"].includes(path)) return Promise.resolve(json([]));
  return Promise.resolve(json({}));
}

for (const path of ["/library", "/library/top", "/settings/ui", "/settings/backup"]) test(`HTTP notice follows page controls inside content: ${path}`, async () => {
  const f = await fixture(fetcher, path, "http://feedarr.test");
  try {
    await f.waitFor(() => document.querySelector(".page"));
    const outer = document.querySelector(".content-outer");
    const notice = document.querySelector(".http-transport-notice");
    assert.equal(document.querySelectorAll(".http-transport-notice").length, 1);
    assert.equal(notice.parentElement, outer);
    assert.equal(notice.nextElementSibling, outer.querySelector(".content"));
    assert.equal(notice.previousElementSibling, outer.querySelector(".subbar"));
    assert.ok(!document.querySelector(".sidebar").contains(notice));
    if (path === "/settings/backup") {
      assert.equal(document.querySelectorAll(".subbar .subaction").length, 1);
      assert.equal(document.querySelector(".subbar .subaction").getAttribute("aria-label"), "Rafraîchir");
      assert.equal(document.querySelectorAll(".settings-grid > .settings-card").length, 2);
      assert.equal(document.querySelectorAll(".configuration-backup-fields .indexer-row--settings").length, 4);
      assert.equal(document.querySelectorAll(".settings-card--full").length, 0);
      assert.equal(document.getElementById("restore-file").accept, ".feedarr");
      for (const id of ["backup-password", "backup-confirmation", "restore-password"]) assert.equal(document.getElementById(id).type, "password");
    }
  } finally { await f.close(); }
});

test("HTTPS does not render the HTTP notice", async () => {
  const f = await fixture(fetcher, "/settings/backup", "https://feedarr.test");
  try { await f.waitFor(() => document.getElementById("backup-password")); assert.equal(document.querySelector(".http-transport-notice"), null); }
  finally { await f.close(); }
});

test("restore preview, disabled state and result stay in the existing settings cards", async () => {
  let resolvePreview;
  const f = await fixture((url, init) => {
    if (url.endsWith("/preview")) return new Promise(resolve => { resolvePreview = resolve; });
    if (url.endsWith("/restore")) { assert.equal(init.body.get("confirm"), "true"); return Promise.resolve(json({ applied: true, restartRequired: false })); }
    return fetcher(url, init);
  }, "/settings/backup", "http://feedarr.test");
  try {
    await f.waitFor(() => document.getElementById("restore-file"));
    const input = document.getElementById("restore-file"); input.required = false; // jsdom cannot set a native FileList/value.
    Object.defineProperty(input, "files", { configurable: true, value: [new window.File(["encrypted fixture"], "fixture.feedarr")] });
    await act(async () => input.dispatchEvent(new window.Event("change", { bubbles: true })));
    await f.change("restore-password", "Temporary fixture password!"); await f.click("Analyser");
    await f.waitFor(() => !!resolvePreview);
    assert.ok([...document.querySelectorAll("fieldset")].every(field => field.disabled));
    assert.ok(document.querySelector('.subbar [aria-label="Rafraîchir"]').disabled);
    await act(async () => resolvePreview(json({ expectedState: "fixture-state", matchedSources: 1, newSources: 1, disabledSources: 0,
      configuration: { settings: 4, sources: 2, indexerProviders: 1, externalProviders: 2, applications: 1, missingSecrets: 0 } })));
    await f.waitFor(() => document.querySelector(".configuration-backup-summary"));
    const summary = document.querySelector(".configuration-backup-summary");
    assert.equal(summary.closest(".settings-card"), document.getElementById("restore-file").closest(".settings-card"));
    assert.match(summary.textContent, /4 sections de paramètres/);
    await f.click("Confirmer la restauration"); await f.waitFor(() => document.body.textContent.includes("Configuration restaurée."));
    assert.ok([...document.querySelectorAll("fieldset")].every(field => !field.disabled));
    assert.equal(document.getElementById("restore-password").value, "");
  } finally { await f.close(); }
});

test("backup refresh clears local passwords, selected file, preview and notices without an API call", async () => {
  const calls = [];
  const f = await fixture((url, init = {}) => {
    if (url.includes("/api/configuration-backups/")) {
      calls.push({ url, init });
      return Promise.resolve(json({ expectedState: "fixture", configuration: { settings: 4, sources: 1, indexerProviders: 1, externalProviders: 0, applications: 0 }, matchedSources: 0, newSources: 1, disabledSources: 0 }));
    }
    return fetcher(url, init);
  }, "/settings/backup", "http://feedarr.test");
  try {
    await f.waitFor(() => document.getElementById("backup-password"));
    await f.change("backup-password", "Temporary password 1!"); await f.change("backup-confirmation", "Temporary password 2!");
    await f.click("Télécharger la sauvegarde");
    await f.waitFor(() => document.body.textContent.includes("ne correspondent pas"));
    await f.click("Rafraîchir");
    assert.equal(document.querySelector(".inline-notice--error"), null);
    assert.equal(document.getElementById("backup-password").value, ""); assert.equal(document.getElementById("backup-confirmation").value, "");
    const input = document.getElementById("restore-file"); input.required = false;
    Object.defineProperty(input, "files", { configurable: true, value: [new window.File(["fixture"], "fixture.feedarr")] });
    await act(async () => input.dispatchEvent(new window.Event("change", { bubbles: true })));
    await f.change("restore-password", "Temporary fixture password!"); await f.click("Analyser");
    await f.waitFor(() => document.querySelector(".configuration-backup-summary"));
    assert.equal(calls.length, 1);
    await f.click("Rafraîchir");
    assert.equal(document.querySelector(".configuration-backup-summary"), null);
    assert.equal(document.getElementById("restore-password").value, ""); assert.equal(input.value, "");
    assert.equal(document.querySelector(".inline-notice"), null); assert.equal(calls.length, 1);
    // The FileList is simulated, so verify React's selected-file state was cleared too.
    await f.change("restore-password", "Temporary fixture password!"); await f.click("Analyser");
    await f.waitFor(() => document.body.textContent.includes("Sélectionnez une sauvegarde"));
    assert.equal(calls.length, 1); await f.click("Rafraîchir");
    assert.equal(document.querySelector(".inline-notice"), null);
  } finally { await f.close(); }
});

test("maintenance advanced visibility preserves analysis state and never analyses on toggle", async () => {
  let resolveSettings, resolveAnalysis;
  const calls = [];
  const f = await fixture((url, init = {}) => {
    if (url.endsWith("/api/settings/maintenance")) return new Promise(resolve => { resolveSettings = resolve; });
    if (url.includes("/api/maintenance/history-cleanup")) {
      calls.push({ url, init }); return new Promise(resolve => { resolveAnalysis = resolve; });
    }
    return fetcher(url, init);
  }, "/settings/maintenance", "http://feedarr.test");
  try {
    const findCard = () => [...document.querySelectorAll(".settings-card")].find(card => card.textContent.includes("Historique des entities et posters"));
    await f.waitFor(() => !!findCard());
    const card = findCard();
    assert.equal(card.hidden, true); assert.equal(window.getComputedStyle(card).display, "none"); assert.equal(calls.length, 0);
    await act(async () => resolveSettings(json({ maintenanceAdvancedOptionsEnabled: false })));
    assert.equal(card.hidden, true); assert.equal(calls.length, 0);
    await f.click("Options avancées"); await f.waitFor(() => !card.hidden);
    assert.equal(findCard(), card); assert.equal(card.parentElement.firstElementChild, card); assert.equal(calls.length, 0);
    const action = [...card.querySelectorAll("button")].find(button => button.textContent === "Analyser sans supprimer");
    assert.ok(action.closest(".indexer-actions")); assert.ok(action.closest(".indexer-row--settings"));
    await f.click("Analyser sans supprimer"); await f.waitFor(() => !!resolveAnalysis);
    assert.equal(calls.length, 1); assert.equal(calls[0].init.method || "GET", "GET");
    await f.click("Options avancées"); assert.equal(card.hidden, true); assert.equal(calls.length, 1);
    await act(async () => resolveAnalysis(json({ entityCandidates: 7, entityBatchCandidates: 2, dryRun: true, errors: 0, protectionReasons: {} })));
    await f.click("Options avancées"); await f.waitFor(() => !card.hidden && !!card.querySelector("dl"));
    assert.equal(findCard(), card); assert.match(card.querySelector("dd").textContent, /7 \/ 2/); assert.equal(calls.length, 1);
    const cleanup = [...card.querySelectorAll("button")].find(button => button.textContent === "Confirmer le nettoyage du lot");
    assert.ok(cleanup.disabled);
  } finally {
    resolveSettings?.(json({ maintenanceAdvancedOptionsEnabled: false }));
    resolveAnalysis?.(json({ entityCandidates: 0, entityBatchCandidates: 0, dryRun: true, errors: 0, protectionReasons: {} }));
    await f.close();
  }
});
