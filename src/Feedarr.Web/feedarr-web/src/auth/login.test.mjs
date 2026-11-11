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
