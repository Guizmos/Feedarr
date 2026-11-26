import test from "node:test";
import assert from "node:assert/strict";
import React, { act } from "react";
import { build } from "esbuild";
import { createRequire } from "node:module";
import { mkdtemp, writeFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import { createDomEnvironment } from "../../test/domEnvironment.mjs";

const require = createRequire(import.meta.url);
const compiled = await build({
  stdin: { contents: `export {default} from './src/components/indexers/DirectRssForm.jsx';`, resolveDir: fileURLToPath(new URL("../../../", import.meta.url)), loader: "jsx" },
  jsx: "automatic", bundle: true, platform: "node", format: "esm", write: false,
  define: { "import.meta.env": JSON.stringify({ BASE_URL: "/" }) },
  plugins: [{ name: "react", setup(b) { b.onResolve({ filter: /^react(?:$|[-/])/ }, args => ({ path: pathToFileURL(require.resolve(args.path)).href, external: true })); } }],
});
const temp = await mkdtemp(join(tmpdir(), "feedarr-rss-ui-"));
await writeFile(join(temp, "form.mjs"), compiled.outputFiles[0].text);
const { default: Form } = await import(pathToFileURL(join(temp, "form.mjs")));
await rm(temp, { recursive: true });

async function click(text) {
  const button = [...document.querySelectorAll("button")].find(b => b.textContent === text);
  assert.ok(button, text);
  await act(async () => button.click());
}

test("RSS editing preserves masked URL, previews without insertion, disables and removes feeds", async () => {
  const env = createDomEnvironment();
  const calls = [];
  let saved = 0;
  globalThis.fetch = async (url, options) => {
    calls.push({ url, body: options?.body ? JSON.parse(options.body) : null });
    if (url.endsWith("/feeds")) return Response.json([{ id: 7, name: "Films", categoryKey: "films", enabled: true, hasUrl: true, potentiallySaturated: true }]);
    if (url.endsWith("/test")) return Response.json({ ok: true, itemsCount: 2, sample: [{ title: "Release preview" }] });
    return Response.json({ id: 1 });
  };
  const { createRoot } = await import("react-dom/client");
  const root = createRoot(document.getElementById("root"));
  try {
    await act(async () => root.render(React.createElement(Form, { source: { id: 1, name: "Direct", color: "#123456" }, onSaved: () => saved++, onCancel() {} })));
    assert.equal(document.querySelector('input[type="password"]').value, "");
    assert.match(document.body.textContent, /potentiellement saturée/);
    await click("Tester le flux");
    assert.match(document.body.textContent, /Release preview/);
    assert.equal(calls.filter(c => c.body).length, 1);
    assert.equal(calls.at(-1).body.sourceId, 1);
    await act(async () => document.querySelector('input[type="checkbox"]').click());
    await act(async () => document.querySelector("form").dispatchEvent(new window.Event("submit", { bubbles: true, cancelable: true })));
    assert.equal(saved, 1);
    assert.equal(calls.at(-1).body.feeds[0].url, "");
    assert.equal(calls.at(-1).body.feeds[0].enabled, false);
    await click("Ajouter un flux");
    assert.equal(document.querySelectorAll("fieldset").length, 2);
    assert.equal(document.querySelectorAll("select")[1].required, true);
    await click("Retirer le flux");
    assert.equal(document.querySelectorAll("fieldset").length, 1);
    assert.ok(calls.every(c => !c.url.includes("/sync")));
  } finally { await act(async () => root.unmount()); env.restore(); }
});

test("RSS test failure is displayed and no automatic feed is created", async () => {
  const env = createDomEnvironment();
  const { createRoot } = await import("react-dom/client");
  const root = createRoot(document.getElementById("root"));
  globalThis.fetch = async url => url.endsWith("/feeds")
    ? Response.json([{ id: 7, categoryKey: "films", enabled: false, hasUrl: true }])
    : Response.json({ ok: false, error: "RSS HTTP 503" });
  try {
    await act(async () => root.render(React.createElement(Form, { source: { id: 1, name: "Direct" }, onSaved() {}, onCancel() {} })));
    assert.equal(document.querySelectorAll("fieldset").length, 1);
    assert.equal(document.querySelector('input[type="checkbox"]').checked, false);
    await click("Tester le flux");
    assert.match(document.body.textContent, /RSS HTTP 503/);
  } finally { await act(async () => root.unmount()); env.restore(); }
});

test("RSS form exposes stale backend route failures for testing and saving without secrets", async () => {
  const env = createDomEnvironment();
  const { createRoot } = await import("react-dom/client");
  const root = createRoot(document.getElementById("root"));
  globalThis.fetch = async (url) => {
    if (url.endsWith("/feeds")) return Response.json([{ id: 7, categoryKey: "films", enabled: true, hasUrl: true }]);
    return Response.json({ title: "Unexpected https://tracker.test/rss?passkey=DO-NOT-SHOW" }, { status: url.endsWith("/test") ? 404 : 405 });
  };
  try {
    await act(async () => root.render(React.createElement(Form, { source: { id: 1, name: "Direct" }, onSaved() { assert.fail("Failed save cannot complete"); }, onCancel() {} })));
    await click("Tester le flux");
    assert.match(document.body.textContent, /HTTP 404/);
    assert.match(document.body.textContent, /Recompile puis redémarre/);
    await act(async () => document.querySelector("form").dispatchEvent(new window.Event("submit", { bubbles: true, cancelable: true })));
    assert.match(document.querySelector('[role="alert"]').textContent, /HTTP 405/);
    assert.doesNotMatch(document.body.textContent, /DO-NOT-SHOW|tracker\.test/);
  } finally { await act(async () => root.unmount()); env.restore(); }
});
