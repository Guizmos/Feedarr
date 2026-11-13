import assert from "node:assert/strict";
import { createDomEnvironment } from "./domEnvironment.mjs";
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
  stdin: { contents: `export {default as Wizard} from './src/pages/SetupWizard.jsx'; export {default as Gate} from './src/auth/AuthGate.jsx'; export * from './src/auth/session.js';`, resolveDir: fileURLToPath(new URL("../../", import.meta.url)), loader: "jsx" },
  bundle: true, platform: "node", format: "esm", write: false,
  plugins: [{ name: "shared-react", setup(b) { b.onResolve({ filter: /^(?:react(?:$|[-/])|lucide-react$)/ }, args => ({ path: pathToFileURL(require.resolve(args.path)).href, external: true })); } }],
});
const bundleDir = await mkdtemp(join(tmpdir(), "feedarr-wizard-dom-"));
await writeFile(join(bundleDir, "wizard.mjs"), compiled.outputFiles[0].text);
export const app = await import(pathToFileURL(join(bundleDir, "wizard.mjs")));
await rm(bundleDir, { recursive: true });
const json = (body, status = 200) => new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
const flush = async () => { for (let i = 0; i < 35; i++) await Promise.resolve(); await new Promise(resolve => setImmediate(resolve)); };

export async function fixture({ fetcher, local = false, storage = {} } = {}) {
  const environment = createDomEnvironment({ url: "https://feedarr.test/setup" });
  const { dom } = environment;
  for (const [key, value] of Object.entries(storage)) dom.window.localStorage.setItem(key, value);
  const previous = { setTimeout: globalThis.setTimeout, clearTimeout: globalThis.clearTimeout };
  try {
  const style = dom.window.document.createElement("style");
  style.textContent = readFileSync(new URL("../styles/auth.css", import.meta.url), "utf8");
  dom.window.document.head.appendChild(style);
  const h = { calls: [], completed: false, mode: "smart", configured: false, authenticated: false, languageFailure: false, stallLanguage: false, putFailure: false, bootstrapFailure: false, sessionFailure: false, stallSession: false, stallLogin: false, stallPut: false };
  const state = () => ({ setupCompleted: h.completed, authMode: h.mode, authRequired: !local && h.mode !== "open", authConfigured: h.configured, authenticated: h.authenticated, csrfToken: h.authenticated ? "csrf-fixture" : undefined });
  const nativeFetch = async (url, init = {}) => {
    const path = new URL(url, "https://feedarr.test").pathname;
    h.calls.push({ path, method: init.method || "GET", headers: new Headers(init.headers), body: init.body });
    if (path === "/api/auth/session") {
      if (h.sessionStatus) return json({ title: "session_verification_denied", detail: "Vérification refusée par le serveur." }, h.sessionStatus);
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
        if (h.putFailure) return json({ title: "security_write_denied", detail: "Sauvegarde refusée.", status: h.failureStatus || 403 }, h.failureStatus || 403);
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
  dom.window.fetch = fetcher ? async (url, init = {}) => {
    h.calls.push({ path: new URL(url, "https://feedarr.test").pathname, method: init.method || "GET", headers: new Headers(init.headers), body: init.body });
    return fetcher(url, init);
  } : nativeFetch;
  app.lockSession(); app.installSessionTransport();
  const requests = new Set();
  globalThis.fetch = (...args) => {
    const pending = dom.window.fetch(...args);
    if (fetcher) {
      requests.add(pending);
      void pending.finally(() => requests.delete(pending)).catch(() => {});
    }
    return pending;
  };
  // Timers stay deterministic without disabling React's scheduler.
  const timers = new Map(); let next = 100000;
  globalThis.setTimeout = (fn, ms, ...args) => {
    if (ms === 15000 || ms === 30000) { const id = ++next; timers.set(id, { fn, ms }); return id; }
    return previous.setTimeout(fn, ms, ...args);
  };
  globalThis.clearTimeout = id => { if (!timers.delete(id)) previous.clearTimeout(id); };
  const { createRoot } = await import("react-dom/client");
  const root = createRoot(document.getElementById("root"));
  const waitFor = async predicate => {
    const deadline = Date.now() + 8000;
    while (!predicate() && Date.now() < deadline) await act(async () => { await new Promise(resolve => previous.setTimeout(resolve, 5)); await flush(); });
    assert.ok(predicate(), `UI did not settle: ${document.body.textContent}`);
  };
  const click = async label => { if (fetcher) await waitFor(() => [...document.querySelectorAll("button")].some(b => b.textContent.trim().startsWith(label) && !b.disabled)); const button = [...document.querySelectorAll("button")].find(b => b.textContent.trim().startsWith(label)); assert.ok(button, `Missing button: ${label}; ${document.body.textContent}`); assert.equal(button.disabled, false, label); await act(async () => { button.click(); await flush(); }); };
  const change = async (element, value) => {
    assert.ok(element, "Missing input");
    const proto = element.tagName === "SELECT" ? dom.window.HTMLSelectElement.prototype : dom.window.HTMLInputElement.prototype;
    await act(async () => { Object.getOwnPropertyDescriptor(proto, "value").set.call(element, value); element.dispatchEvent(new dom.window.Event(element.tagName === "SELECT" ? "change" : "input", { bubbles: true })); await flush(); });
  };
  const visibility = async value => { Object.defineProperty(document, "visibilityState", { configurable: true, value }); await act(async () => { document.dispatchEvent(new dom.window.Event("visibilitychange")); await flush(); }); };
  const expire = async ms => { await act(async () => { for (const [id, timer] of [...timers]) if (timer.ms === ms) { timers.delete(id); timer.fn(); } await flush(); }); };
  await act(async () => { root.render(React.createElement(React.StrictMode, null, React.createElement(MemoryRouter, { initialEntries: ["/setup"] }, React.createElement(Routes, null, React.createElement(Route, { element: React.createElement(app.Gate) }, React.createElement(Route, { path: "/setup", element: React.createElement(app.Wizard) })))))); await flush(); });
  if (fetcher) await waitFor(() => app.getSessionSnapshot().phase === "ready");
  return { h, state, click, change, visibility, expire, timers, dom, waitFor,
    security: async () => { await click("Nouvelle installation"); if (fetcher) await waitFor(() => document.querySelector("#setup-language")?.disabled === false); assert.match(document.body.textContent, /Choisis la langue/); assert.equal(document.querySelector("#setup-language").disabled, false); await click("Suivant"); await click("Suivant"); if (fetcher) await waitFor(() => document.querySelector(".setup-security")?.getAttribute("aria-busy") === "false"); assert.match(document.body.textContent, /Configure le mode/); },
    close: async () => { try { await act(async () => { if (fetcher) { for (let turn = 0; turn < 10 && requests.size; turn++) { await Promise.allSettled([...requests]); await flush(); } } root.unmount(); app.lockSession(); await flush(); }); } finally { timers.clear(); environment.restore(); } },
  };
  } catch (error) { environment.restore(); throw error; }
}

