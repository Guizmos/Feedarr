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
    assert.equal(notice.previousElementSibling, path === "/settings/backup" ? null : outer.querySelector(".subbar"));
    assert.ok(!document.querySelector(".sidebar").contains(notice));
    if (path === "/settings/backup") {
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
