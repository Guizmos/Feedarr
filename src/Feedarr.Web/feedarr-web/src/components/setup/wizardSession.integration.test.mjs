import test from "node:test";
import assert from "node:assert/strict";
import { JSDOM } from "jsdom";
import { build } from "esbuild";
import { createRequire } from "node:module";
import { readFileSync } from "node:fs";
import { mkdtemp, writeFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import React, { act } from "react";
import { MemoryRouter, Routes, Route } from "react-router-dom";

const require = createRequire(import.meta.url);
const compiled = await build({
  stdin: { contents: `export {default as Wizard} from './src/pages/SetupWizard.jsx'; export {default as Gate} from './src/auth/AuthGate.jsx'; export * from './src/auth/session.js';`, resolveDir: fileURLToPath(new URL("../../../", import.meta.url)), loader: "jsx" },
  bundle: true, platform: "node", format: "esm", write: false,
  plugins: [{ name: "shared-react", setup(b) { b.onResolve({ filter: /^(?:react(?:$|[-/])|lucide-react$)/ }, args => ({ path: pathToFileURL(require.resolve(args.path)).href, external: true })); } }],
});
const bundleDir = await mkdtemp(join(tmpdir(), "feedarr-wizard-dom-"));
await writeFile(join(bundleDir, "wizard.mjs"), compiled.outputFiles[0].text);
const app = await import(pathToFileURL(join(bundleDir, "wizard.mjs")));
await rm(bundleDir, { recursive: true });
const json = (body, status = 200) => new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
const flush = async () => { for (let i = 0; i < 35; i++) await Promise.resolve(); await new Promise(resolve => setImmediate(resolve)); };

async function fixture() {
  const dom = new JSDOM('<!doctype html><html><body><div id="root"></div></body></html>', { url: "https://feedarr.test/setup", pretendToBeVisual: true });
  const storageDescriptor = Object.getOwnPropertyDescriptor(globalThis, "localStorage");
  const originalCustomEvent = globalThis.CustomEvent;
  Object.defineProperty(globalThis, "localStorage", { configurable: true, value: dom.window.localStorage });
  globalThis.CustomEvent = dom.window.CustomEvent;
  const style = dom.window.document.createElement("style");
  style.textContent = readFileSync(new URL("../../styles/auth.css", import.meta.url), "utf8");
  dom.window.document.head.appendChild(style);
  const previous = Object.fromEntries(["window", "document", "HTMLElement", "HTMLInputElement", "IS_REACT_ACT_ENVIRONMENT", "fetch", "setTimeout", "clearTimeout"].map(key => [key, globalThis[key]]));
  Object.assign(globalThis, { window: dom.window, document: dom.window.document, HTMLElement: dom.window.HTMLElement, HTMLInputElement: dom.window.HTMLInputElement, IS_REACT_ACT_ENVIRONMENT: true });
  const h = { calls: [], completed: false, mode: "smart", configured: false, authenticated: false, languageFailure: false, stallLanguage: false, putFailure: false, bootstrapFailure: false, sessionFailure: false, stallSession: false, stallLogin: false, stallPut: false };
  const state = () => ({ setupCompleted: h.completed, authMode: h.mode, authRequired: h.mode !== "open", authConfigured: h.configured, authenticated: h.authenticated, csrfToken: h.authenticated ? "csrf-fixture" : undefined });
  const nativeFetch = async (url, init = {}) => {
    const path = new URL(url, "https://feedarr.test").pathname;
    h.calls.push({ path, method: init.method || "GET", headers: new Headers(init.headers) });
    if (path === "/api/auth/session") {
      if (h.sessionFailure) throw new TypeError("Network unavailable");
      if (h.stallSession) return { ok: true, json: () => new Promise(() => {}) };
      return json(state());
    }
    if (path === "/api/auth/login") { if (h.loginFailure) return json({ code: "invalid_credentials" }, 401); if (h.loginNetwork) throw new TypeError("offline"); if (h.stallLoginBody) return { ok: true, json: () => new Promise(() => {}) }; if (h.stallLogin) return new Promise(() => {}); h.authenticated = true; return json(state()); }
    if (path === "/api/setup/bootstrap-token") return h.bootstrapFailure ? json({ error: "bootstrap_secret_required", message: "Secret bootstrap absent ou incorrect." }, 403) : json({ token: "one-use" });
    if (path === "/api/settings/security" && h.securityReadFailure && init.method !== "PUT") return json({ message: "Accès sécurité refusé." }, 403);
    if (path === "/api/settings/security") {
      if (init.method === "PUT") {
        if (h.stallPut) return new Promise(() => {});
        if (h.putFailure) return json({ message: "Sauvegarde refusée." }, 403);
        h.mode = JSON.parse(init.body).authMode; h.configured = h.mode !== "open";
      }
      return json({ ...state(), hasPassword: h.configured, username: h.configured ? "admin" : "" });
    }
    if (path === "/api/settings/ui" && h.stallLanguage) return { status: 200, ok: true, headers: new Headers({ "content-type": "application/json" }), json: () => new Promise(() => {}) };
    if (path === "/api/settings/ui") return h.languageFailure ? json({ message: "Langue indisponible." }, 503) : json({ uiLanguage: "fr", mediaInfoLanguage: "fr" });
    if (path === "/api/setup/state") return json({ onboardingDone: false, hasSources: false, authRequired: state().authRequired, authConfigured: h.configured });
    if (path === "/api/system/onboarding") return json({ onboardingDone: false });
    if (path === "/api/providers/external") return json({ definitions: [], instances: [] });
    return json({});
  };
  dom.window.fetch = nativeFetch;
  app.lockSession(); app.installSessionTransport(); globalThis.fetch = dom.window.fetch;
  // Timers stay deterministic without disabling React's scheduler.
  const timers = new Map(); let next = 100000;
  globalThis.setTimeout = (fn, ms, ...args) => {
    if (ms === 15000 || ms === 30000) { const id = ++next; timers.set(id, { fn, ms }); return id; }
    return previous.setTimeout(fn, ms, ...args);
  };
  globalThis.clearTimeout = id => { if (!timers.delete(id)) previous.clearTimeout(id); };
  const { createRoot } = await import("react-dom/client");
  const root = createRoot(document.getElementById("root"));
  const click = async label => { const button = [...document.querySelectorAll("button")].find(b => b.textContent.trim().startsWith(label)); assert.ok(button, `Missing button: ${label}; ${document.body.textContent}`); assert.equal(button.disabled, false, label); await act(async () => { button.click(); await flush(); }); };
  const change = async (element, value) => {
    assert.ok(element, "Missing input");
    const proto = element.tagName === "SELECT" ? dom.window.HTMLSelectElement.prototype : dom.window.HTMLInputElement.prototype;
    await act(async () => { Object.getOwnPropertyDescriptor(proto, "value").set.call(element, value); element.dispatchEvent(new dom.window.Event(element.tagName === "SELECT" ? "change" : "input", { bubbles: true })); await flush(); });
  };
  const visibility = async value => { Object.defineProperty(document, "visibilityState", { configurable: true, value }); await act(async () => { document.dispatchEvent(new dom.window.Event("visibilitychange")); await flush(); }); };
  const expire = async ms => { await act(async () => { for (const [id, timer] of [...timers]) if (timer.ms === ms) { timers.delete(id); timer.fn(); } await flush(); }); };
  await act(async () => { root.render(React.createElement(React.StrictMode, null, React.createElement(MemoryRouter, { initialEntries: ["/setup"] }, React.createElement(Routes, null, React.createElement(Route, { element: React.createElement(app.Gate) }, React.createElement(Route, { path: "/setup", element: React.createElement(app.Wizard) })))))); await flush(); });
  return { h, state, click, change, visibility, expire, timers, dom,
    security: async () => { await click("Nouvelle installation"); assert.match(document.body.textContent, /Choisis la langue/); assert.equal(document.querySelector("#setup-language").disabled, false); await click("Suivant"); await click("Suivant"); assert.match(document.body.textContent, /Configure le mode/); },
    close: async () => { await act(async () => { root.unmount(); app.lockSession(); await flush(); }); for (const id of timers.keys()) timers.delete(id); dom.window.close(); Object.assign(globalThis, previous); globalThis.CustomEvent = originalCustomEvent; if (storageDescriptor) Object.defineProperty(globalThis, "localStorage", storageDescriptor); else delete globalThis.localStorage; },
  };
}

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
    assert.ok([...document.querySelectorAll("button")].some(b => /Enregistrer|Réessayer/.test(b.textContent) && !b.disabled));
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
