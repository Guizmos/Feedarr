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

test("auth gate keeps unknown and network failure states explicit", async () => {
  const server = await createServer({ server: { middlewareMode: true, hmr: false, watch: null }, logLevel: "silent" });
  const original = globalThis.fetch;
  try {
    const session = await server.ssrLoadModule("/src/auth/session.js");
    const { default: Gate } = await server.ssrLoadModule("/src/auth/AuthGate.jsx");
    session.lockSession();
    const render = () => renderToStaticMarkup(React.createElement(MemoryRouter, null, React.createElement(Gate)));
    assert.match(render(), /Vérification de la session/);
    globalThis.fetch = async () => new Response("offline", { status: 503 });
    await assert.rejects(session.refreshSession());
    assert.match(render(), /Réessayer/);
  } finally { globalThis.fetch = original; await server.close(); }
});

test("initial administrator reuses login design and requires no bootstrap secret", async () => {
  const server = await createServer({ server: { middlewareMode: true, hmr: false, watch: null }, logLevel: "silent" });
  const original = globalThis.fetch;
  try {
    const session = await server.ssrLoadModule("/src/auth/session.js");
    globalThis.fetch = async () => new Response(JSON.stringify({ authRequired: true, authConfigured: false, authenticated: false, initialAdminRequired: true, secureTransport: true }));
    await session.refreshSession();
    const { default: Login } = await server.ssrLoadModule("/src/pages/Login.jsx");
    const html = renderToStaticMarkup(React.createElement(Login, { createAdmin: true }));
    assert.match(html, /Créer votre compte administrateur/);
    assert.match(html, /action="\/api\/auth\/create-admin"/);
    assert.match(html, /name="confirmation"/);
    assert.doesNotMatch(html, /bootstrap|secret initial/i);
    assert.doesNotMatch(html, /name="rememberMe"/);
  } finally { globalThis.fetch = original; await server.close(); }
});
