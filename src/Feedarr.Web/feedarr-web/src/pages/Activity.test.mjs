import test from "node:test";
import assert from "node:assert/strict";
import React, { act } from "react";
import { build } from "esbuild";
import { createRequire } from "node:module";
import { mkdtemp, writeFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import { createDomEnvironment } from "../test/domEnvironment.mjs";
const require = createRequire(import.meta.url);
const compiled = await build({
  stdin: { contents: `export {default as Logs} from './src/pages/Activity.jsx'; export {default as History} from './src/pages/History.jsx'; export {SubbarProvider} from './src/layout/SubbarContext.jsx'; export {useSubbar} from './src/layout/useSubbar.js';`, resolveDir: fileURLToPath(new URL("../../", import.meta.url)), loader: "jsx" },
  jsx: "automatic", bundle: true, platform: "node", format: "esm", write: false, define: { "import.meta.env": JSON.stringify({ BASE_URL: "/" }) },
  plugins: [{ name: "react", setup(b) { b.onResolve({ filter: /^(?:react(?:$|[-/])|lucide-react$)/ }, args => ({ path: pathToFileURL(require.resolve(args.path)).href, external: true })); } }],
});
const temp = await mkdtemp(join(tmpdir(), "feedarr-activity-dom-"));
await writeFile(join(temp, "pages.mjs"), compiled.outputFiles[0].text);
const pages = await import(pathToFileURL(join(temp, "pages.mjs")));
await rm(temp, { recursive: true });
function Actions() { return pages.useSubbar().content; }

for (const scope of ["logs", "history"]) test(`${scope}: confirmation, scoped purge and page refresh`, async () => {
  const env = createDomEnvironment();
  let rows = [
    { id: 1, sourceId: 1, eventType: "sync", level: "info", message: "sync sentinel", createdAt: 1700000001, dataJson: '{"items":4}' },
    { id: 2, sourceId: 2, eventType: "poster_fetch", level: "warn", message: "log sentinel", createdAt: 1700000002, dataJson: '{"logFile":"retro-fixture.csv"}' },
  ];
  const calls = [];
  globalThis.fetch = async (url, init = {}) => {
    const target = new URL(url, "https://feedarr.test"); calls.push({ target, init });
    if (target.pathname === "/api/sources") return Response.json([{ id: 1, name: "History source" }, { id: 2, name: "Log source" }]);
    if (target.pathname === "/api/activity/purge") {
      assert.equal(target.searchParams.get("scope"), scope);
      rows = rows.filter(row => scope === "logs" ? row.eventType === "sync" : row.eventType !== "sync"); return Response.json({ ok: true });
    }
    assert.equal(target.pathname, "/api/activity");
    if (scope === "logs") assert.equal(target.searchParams.get("scope"), "logs");
    let visible = rows.filter(row => target.searchParams.get("scope") === "logs" ? row.eventType !== "sync" : true);
    for (const field of ["eventType", "level"]) if (target.searchParams.get(field)) visible = visible.filter(row => row[field] === target.searchParams.get(field));
    return Response.json(visible);
  };
  const { createRoot } = await import("react-dom/client");
  const root = createRoot(document.getElementById("root"));
  const wait = async predicate => {
    for (let i = 0; i < 100 && !predicate(); i++) await act(async () => { await new Promise(resolve => setTimeout(resolve, 5)); });
    assert.ok(predicate(), document.body.textContent);
  };
  const click = async text => {
    const button = [...document.querySelectorAll("button")].find(b => b.textContent.trim() === text && !b.disabled);
    assert.ok(button, text); await act(async () => button.click());
  };
  try {
    await act(async () => root.render(React.createElement(pages.SubbarProvider, null, React.createElement(Actions), React.createElement(scope === "logs" ? pages.Logs : pages.History))));
    await wait(() => document.body.textContent.includes(scope === "logs" ? "log sentinel" : "History source"));
    if (scope === "logs") {
      assert.ok(!document.body.textContent.includes("sync sentinel")); assert.equal(document.querySelector('option[value="sync"]'), null);
      for (const [label, value, field] of [["Type", "poster_fetch", "eventType"], ["Niveau", "warn", "level"]]) {
        const select = document.querySelector(`select[aria-label="${label}"]`);
        await act(async () => { select.value = value; select.dispatchEvent(new window.Event("change", { bubbles: true })); });
        await wait(() => calls.some(c => c.target.searchParams.get(field) === value));
      }
      await act(async () => document.querySelector('[aria-label="Afficher détails"]').click());
      assert.equal(document.querySelector('a[download]').getAttribute("href"), "/api/posters/retro-fetch/log/retro-fixture.csv");
    }
    await click("Effacer"); assert.equal(rows.length, 2); await click("Confirmer");
    await wait(() => scope === "logs" ? !document.body.textContent.includes("log sentinel") : document.body.textContent.includes("Aucune entrée d'historique"));
    assert.equal(rows.length, 1); assert.equal(rows[0].eventType, scope === "logs" ? "sync" : "poster_fetch");
    assert.equal(calls.filter(c => c.target.pathname === "/api/activity/purge").length, 1);
  } finally { await act(async () => root.unmount()); env.restore(); }
});
