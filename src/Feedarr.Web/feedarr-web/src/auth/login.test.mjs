import test from "node:test";
import assert from "node:assert/strict";
import React from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { MemoryRouter } from "react-router-dom";
import { createServer } from "vite";

test("rendered login supports password managers, remember-me and accessible responsive controls", async () => {
  const server = await createServer({ server: { middlewareMode: true, hmr: false, watch: null }, logLevel: "silent" });
  const original = globalThis.fetch;
  try {
    const session = await server.ssrLoadModule("/src/auth/session.js");
    globalThis.fetch = async () => new Response(JSON.stringify({ authRequired: true, authConfigured: true, authenticated: false, setupCompleted: true, webSessionAvailable: true }));
    await session.refreshSession();
    const { default: Login } = await server.ssrLoadModule("/src/pages/Login.jsx");
    const html = renderToStaticMarkup(React.createElement(MemoryRouter, { initialEntries: ["/login?returnTo=%2Flibrary"] }, React.createElement(Login)));
    assert.match(html, /method="post"/);
    assert.match(html, /action="\/api\/auth\/login"/);
    const inputs = [...html.matchAll(/<input[^>]*>/g)].map((match) => match[0]);
    assert.match(inputs.find((input) => input.includes('name="username"')), /autoComplete="username"/);
    assert.match(inputs.find((input) => input.includes('name="password"')), /autoComplete="current-password"/);
    assert.match(html, /type="password"/);
    assert.match(inputs.find((input) => input.includes('name="rememberMe"')), /type="checkbox"/);
    assert.match(html, /aria-pressed="false"/);
    assert.match(html, /icon-192.png\?v=9d1d5be2bd5c/);
    assert.match(html, /<button[^>]*type="submit"/);
    assert.doesNotMatch(html, /Authorization|localStorage|sessionStorage/);
  } finally { globalThis.fetch = original; await server.close(); }
});

test("logout renders a touch-sized icon action with translated tooltip and accessible name", async () => {
  const server = await createServer({ server: { middlewareMode: true, hmr: false, watch: null }, logLevel: "silent" });
  const originalFetch = globalThis.fetch, originalDocument = globalThis.document;
  try {
    const session = await server.ssrLoadModule("/src/auth/session.js");
    globalThis.fetch = async () => new Response(JSON.stringify({ authRequired: true, authConfigured: true, authenticated: true, setupCompleted: true }));
    await session.refreshSession();
    const { default: LogoutButton } = await server.ssrLoadModule("/src/auth/LogoutButton.jsx");
    for (const [language, label] of [["fr-FR", "Déconnexion"], ["en-US", "Log out"]]) {
      globalThis.document = { documentElement: { getAttribute: () => language } };
      const html = renderToStaticMarkup(React.createElement(LogoutButton));
      assert.ok(html.includes(`title="${label}"`));
      assert.ok(html.includes(`aria-label="${label}"`));
      assert.match(html, /class="subaction auth-logout-action"/);
      assert.match(html, /lucide-log-out/);
      assert.match(html, /aria-hidden="true"/);
      assert.doesNotMatch(html, />Déconnexion<|>Log out<|class="btn"/);
    }
  } finally { globalThis.fetch = originalFetch; globalThis.document = originalDocument; await server.close(); }
});

test("rendered auth gate leaves incomplete setup mounted and unknown/error states stable", async () => {
  const server = await createServer({ server: { middlewareMode: true, hmr: false, watch: null }, logLevel: "silent" });
  const originalFetch = globalThis.fetch;
  try {
    const session = await server.ssrLoadModule("/src/auth/session.js");
    const { default: AuthGate } = await server.ssrLoadModule("/src/auth/AuthGate.jsx");
    const { Routes, Route } = await import("react-router-dom");
    const render = () => renderToStaticMarkup(React.createElement(MemoryRouter, { initialEntries: ["/setup"] },
      React.createElement(Routes, null, React.createElement(Route, { element: React.createElement(AuthGate) },
        React.createElement(Route, { path: "/setup", element: React.createElement("div", null, "wizard-content") })))));
    session.lockSession();
    assert.match(render(), /Vérification de la session/);
    assert.doesNotMatch(render(), /wizard-content/);
    for (const authMode of ["strict", "smart"]) for (const authConfigured of [false, true]) {
      globalThis.fetch = async () => new Response(JSON.stringify({ authRequired: true, authConfigured, authenticated: false, setupCompleted: false, authMode }));
      await session.refreshSession();
      assert.match(render(), /wizard-content/);
    }
    session.lockSession();
    globalThis.fetch = async () => new Response("unavailable", { status: 500 });
    await assert.rejects(session.refreshSession());
    assert.match(render(), /Réessayer/);
    assert.doesNotMatch(render(), /wizard-content/);
  } finally { globalThis.fetch = originalFetch; await server.close(); }
});

test("configured incomplete setup shows wizard and reused login form together without leaving setup", async () => {
  const server = await createServer({ server: { middlewareMode: true, hmr: false, watch: null }, logLevel: "silent" });
  const original = globalThis.fetch;
  try {
    const session = await server.ssrLoadModule("/src/auth/session.js");
    globalThis.fetch = async () => new Response(JSON.stringify({ authRequired: true, authConfigured: true, authenticated: false, setupCompleted: false, webSessionAvailable: true }));
    await session.refreshSession();
    const { default: AuthGate } = await server.ssrLoadModule("/src/auth/AuthGate.jsx");
    const { default: SetupShell } = await server.ssrLoadModule("/src/layout/SetupShell.jsx");
    const { Routes, Route } = await import("react-router-dom");
    const html = renderToStaticMarkup(React.createElement(MemoryRouter, { initialEntries: ["/setup"] }, React.createElement(Routes, null,
      React.createElement(Route, { element: React.createElement(AuthGate) }, React.createElement(Route, { path: "/setup", element: React.createElement(SetupShell) },
        React.createElement(Route, { index: true, element: React.createElement("div", null, "wizard-content") }))))));
    assert.match(html, /wizard-content/);
    assert.match(html, /auth-setup-login/);
    assert.match(html, /action="\/api\/auth\/login"/);
    assert.match(html, /autoComplete="current-password"/);
  } finally { globalThis.fetch = original; await server.close(); }
});
