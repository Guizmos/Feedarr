import test from "node:test";
import assert from "node:assert/strict";
import React, { act } from "react";
import { MemoryRouter } from "react-router-dom";
import { build } from "esbuild";
import { createRequire } from "node:module";
import { mkdtemp, writeFile, rm, readFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import { createDomEnvironment } from "../test/domEnvironment.mjs";
const require = createRequire(import.meta.url);
const compiled = await build({
  stdin: { contents: `export {default as Top} from './src/pages/TopReleases.jsx'; export {SubbarProvider} from './src/layout/SubbarContext.jsx';`, resolveDir: fileURLToPath(new URL("../../", import.meta.url)), loader: "jsx" },
  jsx: "automatic", bundle: true, platform: "node", format: "esm", write: false, define: { "import.meta.env": JSON.stringify({ BASE_URL: "/" }) },
  plugins: [{ name: "react", setup(b) { b.onResolve({ filter: /^(?:react(?:$|[-/])|lucide-react$)/ }, args => ({ path: pathToFileURL(require.resolve(args.path)).href, external: true })); } }],
});
const temp = await mkdtemp(join(tmpdir(), "feedarr-top-header-"));
await writeFile(join(temp, "page.mjs"), compiled.outputFiles[0].text);
const { Top, SubbarProvider } = await import(pathToFileURL(join(temp, "page.mjs")));
await rm(temp, { recursive: true });

for (const populated of [false, true]) test(`French Top 24h subtitle: ${populated ? "dynamic totals and ranking" : "empty results"}`, async () => {
  const env = createDomEnvironment();
  const since = new Date(2026, 10, 12, 21, 4).toISOString();
  const categories = populated ? [{ key: "films", label: "Films", count: 7, top: [] }, { key: "series", label: "Séries", count: 3, top: [] }] : [];
  const requests = [];
  globalThis.fetch = async url => {
    const target = new URL(url, "https://feedarr.test"); requests.push(target);
    if (target.pathname === "/api/feed/top") return Response.json({ window: { sinceUtc: since, hours: 24, field: "published_at_ts" }, sortUsed: populated ? "seeders" : "score", dedupeUsed: "entity", categories, globalTop: [] });
    if (target.pathname === "/api/settings/ui") return Response.json({ defaultView: "grid" });
    if (target.pathname === "/api/settings/general") return Response.json({ requestIntegrationMode: "arr" });
    return Response.json([]);
  };
  const { createRoot } = await import("react-dom/client");
  const root = createRoot(document.getElementById("root"));
  try {
    await act(async () => root.render(React.createElement(MemoryRouter, null, React.createElement(SubbarProvider, null, React.createElement(Top)))));
    for (let i = 0; i < 100 && !document.querySelector(".pagehead h1 + .muted"); i++) await act(async () => { await new Promise(resolve => setTimeout(resolve, 5)); });
    assert.equal(document.querySelector(".pagehead h1 + .muted")?.textContent, populated ? "Classement Seeders · Depuis 12/11/2026 21:04 · 10 items · 2 catégories" : "Classement Score · Depuis 12/11/2026 21:04 · 0 items · 0 catégories");
    const request = requests.find(url => url.pathname === "/api/feed/top");
    assert.equal(request.searchParams.get("hours"), "24"); assert.equal(request.searchParams.get("take"), "5"); assert.equal(request.searchParams.get("dedupe"), "entity");
    const css = await readFile(new URL("../styles/styles.css", import.meta.url), "utf8");
    const rule = css.match(/\.page--top24h \.pagehead h1 \+ \.muted\s*\{[^}]+\}/)?.[0];
    assert.ok(rule); const style = document.createElement("style"); style.textContent = rule; document.head.appendChild(style);
    assert.equal(window.getComputedStyle(document.querySelector(".pagehead h1 + .muted")).marginTop, "5px");
  } finally { await act(async () => root.unmount()); env.restore(); }
});
