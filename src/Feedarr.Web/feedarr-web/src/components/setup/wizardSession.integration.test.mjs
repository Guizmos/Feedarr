import test from "node:test";
import assert from "node:assert/strict";
import { act } from "react";
import { fixture, app } from "../../test/wizardDomFixture.mjs";
const flush = async () => { for (let i = 0; i < 35; i++) await Promise.resolve(); await new Promise(resolve => setImmediate(resolve)); };

test("real React + session transport: language, Open, tab return and Metadata without remount or duplicate refresh", async () => {
  const f = await fixture();
  try {
    await f.security(); await f.change(document.querySelector("#setup-bootstrap"), "target-bootstrap");
    await f.change(document.querySelector("select"), "open");
    const callsBefore = f.h.calls.length;
    await f.click("Enregistrer");
    assert.doesNotMatch(document.body.textContent, /Enregistrement\.\.\./);
    assert.equal(f.h.calls.slice(callsBefore).filter(c => c.path === "/api/auth/session").length, 1);
    assert.equal(f.h.calls.slice(callsBefore).filter(c => c.path === "/api/auth/login").length, 0);
    const wizard = document.querySelector(".setup-container");
    await f.visibility("hidden"); assert.equal(app.getSessionSnapshot().state.setupCompleted, false); assert.equal(document.documentElement.classList.contains("auth-locked"), false);
    await f.visibility("visible"); assert.equal(document.querySelector(".setup-container"), wizard);
    await f.click("Suivant"); assert.match(document.body.textContent, /Ajoute au moins un provider/);
  } finally { await f.close(); }
});

test("real React + transport: Smart logs in once without a redundant session GET and keeps CSRF", async () => {
  const f = await fixture();
  try {
    await f.security(); await f.change(document.querySelector("#setup-bootstrap"), "target-bootstrap");
    const fields = [...document.querySelectorAll("input")];
    await f.change(fields.find(i => /username/i.test(i.placeholder)), "admin");
    for (const input of fields.filter(i => i.type === "password" && i.id !== "setup-bootstrap")) await f.change(input, "fixture-password");
    const callsBefore = f.h.calls.length; await f.click("Enregistrer");
    assert.equal(f.h.calls.slice(callsBefore).filter(c => c.path === "/api/auth/session").length, 0);
    assert.equal(f.h.calls.slice(callsBefore).filter(c => c.path === "/api/auth/login").length, 1);
    assert.equal(app.getSessionSnapshot().state.csrfToken, "csrf-fixture");
    await f.click("Suivant"); assert.match(document.body.textContent, /Ajoute au moins un provider/);
    const beforeReturn = f.h.calls.length;
    await f.visibility("hidden"); assert.equal(app.getSessionSnapshot().state, null); assert.ok(document.documentElement.classList.contains("auth-locked"));
    await f.visibility("visible");
    assert.equal(app.getSessionSnapshot().state.authenticated, true);
    assert.equal(document.documentElement.classList.contains("auth-locked"), false);
    assert.equal(f.h.calls.slice(beforeReturn).filter(c => c.path === "/api/auth/session").length, 1);
  } finally { await f.close(); }
});

for (const failure of ["bootstrapFailure", "putFailure", "stallPut", "stallSession", "sessionFailure"]) test(`real Open save: ${failure} ends saving and exposes recovery`, async () => {
  const f = await fixture();
  try {
    await f.security(); await f.change(document.querySelector("#setup-bootstrap"), "target-bootstrap"); await f.change(document.querySelector("select"), "open");
    f.h[failure] = true; await f.click("Enregistrer");
    if (failure === "stallPut") await f.expire(30000);
    if (failure === "stallSession") await f.expire(15000);
    assert.doesNotMatch(document.body.textContent, /Enregistrement\.\.\./);
    assert.match(document.body.textContent, /refus|bootstrap|Connexion au serveur impossible|timed out/i);
    assert.equal(document.documentElement.classList.contains("auth-locked"), false);
    assert.ok([...document.querySelectorAll("button")].some(b => /Enregistrer|Réessayer|Vérifier l’accès/.test(b.textContent) && !b.disabled));
  } finally { await f.close(); }
});

test("real language loading times out visibly, then retry loads it", async () => {
  const f = await fixture();
  try {
    f.h.languageFailure = true; await f.click("Nouvelle installation"); assert.match(document.body.textContent, /Langue indisponible/);
    assert.equal(document.querySelector("#setup-language").disabled, true);
    f.h.languageFailure = false; await f.click("Reessayer"); assert.equal(document.querySelector("#setup-language").disabled, false);
  } finally { await f.close(); }
});

test("tab return offline: error and retry remain visible; retry recovers the incomplete wizard", async () => {
  const f = await fixture();
  try {
    await f.security(); f.h.sessionFailure = true;
    await f.visibility("hidden"); await f.visibility("visible");
    assert.match(document.body.textContent, /Connexion au serveur impossible/);
    assert.equal(document.documentElement.classList.contains("auth-locked"), false);
    f.h.sessionFailure = false; await f.click("Réessayer"); assert.match(document.body.textContent, /Configure le mode/);
  } finally { await f.close(); }
});


test("anonymous wizard stays mounted across pagehide, BFCache and visibility while private gate errors remain visible", async () => {
  const f = await fixture();
  try {
    await f.security(); const wizard = document.querySelector(".setup-container");
    await act(async () => { window.dispatchEvent(new window.PageTransitionEvent("pagehide", { persisted: true })); await flush(); });
    assert.equal(document.querySelector(".setup-container"), wizard);
    const before = f.h.calls.length;
    await act(async () => {
      window.dispatchEvent(new window.PageTransitionEvent("pageshow", { persisted: true }));
      document.dispatchEvent(new window.Event("visibilitychange"));
      await flush();
    });
    assert.equal(f.h.calls.slice(before).filter(c => c.path === "/api/auth/session").length, 1);
    assert.equal(document.querySelector(".setup-container"), wizard);
    f.h.authenticated = true; f.h.configured = true;
    await act(async () => { await app.refreshSession(); await flush(); });
    const portal = document.createElement("div"); portal.className = "modal-overlay"; document.body.appendChild(portal);
    await act(async () => {
      Object.defineProperty(document, "visibilityState", { configurable: true, value: "hidden" });
      document.dispatchEvent(new window.Event("visibilitychange"));
      const privateContent = document.querySelector('[data-auth-private="true"]');
      assert.ok(privateContent); assert.equal(window.getComputedStyle(privateContent).visibility, "hidden");
      assert.equal(window.getComputedStyle(portal).visibility, "hidden");
      await flush();
    });
    f.h.stallSession = true; await f.visibility("visible"); await f.expire(15000);
    const retryPage = document.querySelector(".auth-page"); assert.ok(retryPage);
    assert.equal(window.getComputedStyle(retryPage).visibility, "visible");
    assert.ok([...document.querySelectorAll("button")].some(b => b.textContent === "Réessayer"));
  } finally { await f.close(); }
});


for (const failure of ["loginFailure", "loginNetwork", "stallLogin", "stallLoginBody"]) test(`real Smart save: ${failure} ends saving and offers authentication recovery without another PUT`, async () => {
  const f = await fixture();
  try {
    await f.security(); await f.change(document.querySelector("#setup-bootstrap"), "target-bootstrap");
    const fields = [...document.querySelectorAll("input")];
    await f.change(fields.find(i => /username/i.test(i.placeholder)), "admin");
    for (const input of fields.filter(i => i.type === "password" && i.id !== "setup-bootstrap")) await f.change(input, "fixture-password");
    f.h[failure] = true; await f.click("Enregistrer");
    if (failure === "stallLogin" || failure === "stallLoginBody") await f.expire(30000);
    assert.match(document.body.textContent, /Paramètres enregistrés/);
    assert.doesNotMatch(document.body.textContent, /Enregistrement\.\.\./);
    assert.equal(f.h.calls.filter(c => c.path === "/api/settings/security" && c.method === "PUT").length, 1);
    f.h[failure] = false; await f.click("Vérifier l’accès");
    assert.ok(document.querySelector("#setup-login"));
    assert.equal(f.h.calls.filter(c => c.path === "/api/settings/security" && c.method === "PUT").length, 1);
  } finally { await f.close(); }
});

test("language response-body deadline releases loading and retry succeeds", async () => {
  const f = await fixture();
  try {
    f.h.stallLanguage = true; await f.click("Nouvelle installation"); assert.equal(document.querySelector("#setup-language").disabled, true);
    await f.expire(30000); assert.match(document.body.textContent, /timed out/);
    assert.equal(document.querySelector(".setup-language").getAttribute("aria-busy"), "false");
    f.h.stallLanguage = false; await f.click("Reessayer"); assert.equal(document.querySelector("#setup-language").disabled, false);
  } finally { await f.close(); }
});

test("private tab return with a stalled session body never leaves a blank recovery page", async () => {
  const f = await fixture();
  try {
    f.h.authenticated = true; f.h.configured = true;
    await act(async () => { await app.refreshSession(); await flush(); });
    await f.visibility("hidden"); f.h.stallSession = true; await f.visibility("visible");
    await f.expire(15000);
    const page = document.querySelector(".auth-page"); assert.ok(page);
    assert.equal(window.getComputedStyle(page).visibility, "visible");
    assert.match(page.textContent, /Connexion au serveur impossible/);
    assert.ok([...page.querySelectorAll("button")].some(b => b.textContent === "Réessayer"));
  } finally { await f.close(); }
});


test("retry after a private tab-return error releases the mask only after successful verification", async () => {
  const f = await fixture();
  try {
    f.h.authenticated = true; f.h.configured = true;
    await act(async () => { await app.refreshSession(); await flush(); });
    await f.visibility("hidden"); f.h.sessionFailure = true; await f.visibility("visible");
    assert.equal(document.documentElement.classList.contains("auth-locked"), true);
    assert.equal(window.getComputedStyle(document.querySelector(".auth-page")).visibility, "visible");
    f.h.sessionFailure = false; await f.click("Réessayer");
    assert.equal(document.documentElement.classList.contains("auth-locked"), false);
    assert.equal(window.getComputedStyle(document.querySelector('[data-auth-private="true"]')).visibility, "visible");
  } finally { await f.close(); }
});


test("security loading failure exposes retry and never enables saving unknown settings", async () => {
  const f = await fixture();
  try {
    f.h.securityReadFailure = true; await f.click("Nouvelle installation"); await f.click("Suivant"); await f.click("Suivant");
    assert.match(document.body.textContent, /Accès sécurité refusé/);
    assert.equal([...document.querySelectorAll("button")].find(b => b.textContent === "Enregistrer").disabled, true);
    f.h.securityReadFailure = false; await f.click("Réessayer le chargement");
    assert.ok(document.querySelector("#setup-bootstrap"));
    assert.equal([...document.querySelectorAll("button")].find(b => b.textContent === "Enregistrer").disabled, false);
  } finally { await f.close(); }
});

test("Open ignores hidden credentials left by Smart/autofill and reaches Metadata", async () => {
  const f = await fixture();
  try {
    await f.security();
    const password = [...document.querySelectorAll('input[type="password"]')].find(i => i.id !== "setup-bootstrap");
    await f.change(password, "Autofilled-Password42!"); // confirmation intentionally empty
    await f.change(document.querySelector("select"), "open");
    await f.change(document.querySelector("#setup-bootstrap"), "target-bootstrap");
    await f.click("Enregistrer");
    assert.equal(f.h.calls.filter(c => c.method === "PUT" && c.path === "/api/settings/security").length, 1);
    await f.click("Suivant"); assert.match(document.body.textContent, /Ajoute au moins un provider/);
  } finally { await f.close(); }
});

for (const status of [400, 401, 403]) test(`Open API HTTP ${status} shows the real cause and status with a usable retry`, async () => {
  const f = await fixture();
  try {
    await f.security(); await f.change(document.querySelector("select"), "open"); await f.change(document.querySelector("#setup-bootstrap"), "target-bootstrap");
    f.h.putFailure = true; f.h.failureStatus = status; await f.click("Enregistrer");
    assert.match(document.body.textContent, new RegExp(`HTTP ${status}`)); assert.match(document.body.textContent, /Sauvegarde refusée/);
    assert.equal(document.querySelector(".setup-security").getAttribute("aria-busy"), "false");
    f.h.putFailure = false; await f.click("Enregistrer"); await f.click("Suivant"); assert.match(document.body.textContent, /Ajoute au moins un provider/);
  } finally { await f.close(); }
});


for (const status of [400, 401, 403]) test(`session verification HTTP ${status} remains visible with its cause and retry`, async () => {
  const f = await fixture();
  try {
    await f.security(); await f.change(document.querySelector("select"), "open"); await f.change(document.querySelector("#setup-bootstrap"), "target-bootstrap");
    f.h.sessionStatus = status; await f.click("Enregistrer");
    assert.match(document.body.textContent, new RegExp(`HTTP ${status}`)); assert.match(document.body.textContent, /Vérification refusée par le serveur/);
    f.h.sessionStatus = 0; await f.click("Réessayer"); await f.click("Suivant"); assert.match(document.body.textContent, /Ajoute au moins un provider/);
  } finally { await f.close(); }
});
