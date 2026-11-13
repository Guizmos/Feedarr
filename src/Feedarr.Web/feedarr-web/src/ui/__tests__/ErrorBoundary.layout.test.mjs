import test from "node:test";
import assert from "node:assert/strict";
import React, { act, Suspense } from "react";
import { JSDOM } from "jsdom";
import { build } from "esbuild";
import { createRequire } from "node:module";
import { readFileSync } from "node:fs";
import { mkdtemp, writeFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import { createMemoryRouter, RouterProvider } from "react-router-dom";

const require = createRequire(import.meta.url);
const bundle = await build({ stdin: { contents: "export {default as Boundary} from './ErrorBoundary.jsx'; export {default as RouteBoundary} from './RouteErrorBoundary.jsx';", resolveDir: fileURLToPath(new URL("../", import.meta.url)), loader: "jsx" }, bundle: true, format: "esm", platform: "node", write: false,
  plugins: [{ name: "shared-react", setup(b) { b.onResolve({ filter: /^react(?:$|[-/])/ }, args => ({ path: pathToFileURL(require.resolve(args.path)).href, external: true })); } }],
});
const directory = await mkdtemp(join(tmpdir(), "feedarr-error-boundary-"));
await writeFile(join(directory, "boundaries.mjs"), bundle.outputFiles[0].text);
const { Boundary, RouteBoundary } = await import(pathToFileURL(join(directory, "boundaries.mjs")));
await rm(directory, { recursive: true });
const css = readFileSync(new URL("../../styles/styles.css", import.meta.url), "utf8");
const flush = async () => { for (let i = 0; i < 12; i++) await Promise.resolve(); };

async function fixture() {
  const previous = Object.fromEntries(["window", "document", "IS_REACT_ACT_ENVIRONMENT"].map(key => [key, globalThis[key]]));
  const dom = new JSDOM('<!doctype html><html><body><div id="root"></div></body></html>', { url: "https://feedarr.test/library" });
  Object.assign(globalThis, { window: dom.window, document: dom.window.document, IS_REACT_ACT_ENVIRONMENT: true });
  const { createRoot } = await import("react-dom/client");
  const root = createRoot(document.getElementById("root"));
  const originalError = console.error; console.error = () => {}; // React reports the deliberately injected failures.
  return { dom, render: async element => { await act(async () => { root.render(element); await flush(); }); },
    close: async () => { await act(async () => root.unmount()); console.error = originalError; dom.window.close(); Object.assign(globalThis, previous); } };
}
const element = React.createElement;
const shell = child => element("div", null, element("nav", null, "Navigation"), element("aside", null, "Menu latéral"), element("main", { className: "content" }, child));

test("a page render failure preserves navigation, message and a working retry", async () => {
  const f = await fixture(); let fail = true;
  function Page() { if (fail) throw new Error("Erreur de composant"); return element("div", { className: "page" }, "Contenu rétabli"); }
  try {
    await f.render(shell(element(Boundary, { label: "Bibliothèque" }, element(Page))));
    assert.ok(document.querySelector("nav")); assert.ok(document.querySelector("aside"));
    assert.ok(document.querySelector(".content > .error-boundary > .errorbox"));
    assert.match(document.body.textContent, /Erreur de composant/);
    fail = false;
    await act(async () => { document.querySelector("button").click(); await flush(); });
    assert.match(document.body.textContent, /Contenu rétabli/); assert.equal(document.querySelector(".errorbox"), null);
  } finally { await f.close(); }
});

test("a rejected dynamic import uses the same page error container and retains retry", async () => {
  const f = await fixture();
  const Lazy = React.lazy(() => Promise.reject(new Error("Failed to fetch dynamically imported module")));
  try {
    await f.render(shell(element(Boundary, { label: "Page" }, element(Suspense, { fallback: "Chargement" }, element(Lazy)))));
    assert.ok(document.querySelector(".content > .error-boundary > .errorbox"));
    assert.match(document.body.textContent, /Failed to fetch dynamically imported module/);
    assert.equal(document.querySelector("button").textContent.trim(), "Reessayer");
  } finally { await f.close(); }
});

test("route errors retain page gutters, details, reload and library recovery", async () => {
  const f = await fixture();
  const router = createMemoryRouter([{ path: "/broken", loader: () => { throw new Error("Erreur de route"); }, element: element("div"), errorElement: element(RouteBoundary) }, { path: "/library", element: element("div", null, "Bibliothèque rétablie") }], { initialEntries: ["/broken"] });
  try {
    await f.render(element(RouterProvider, { router }));
    assert.ok(document.querySelector(".page.page--error")); assert.match(document.body.textContent, /Erreur de route/);
    const buttons = [...document.querySelectorAll("button")]; assert.ok(buttons.some(b => b.textContent === "Recharger"));
    await act(async () => { buttons.find(b => /Retour/.test(b.textContent)).click(); await flush(); });
    assert.match(document.body.textContent, /Bibliothèque rétablie/);
  } finally { router.dispose(); await f.close(); }
});

// jsdom has no viewport layout engine. Resolve width media queries explicitly,
// then compare computed gutter styles with the ordinary .page at each breakpoint.
for (const width of [1440, 900, 768, 375, 320]) test(`page and error gutters match at ${width}px without adding padding to nested errors`, () => {
  const dom = new JSDOM('<div id="root"><main class="content"><div id="normal" class="page"><div id="nested" class="error-boundary"><div class="errorbox">Nested</div></div></div><div id="failure" class="error-boundary"><div class="errorbox">Failure</div></div><div id="route" class="page page--error"><div class="errorbox">Route</div></div></main><div id="global" class="error-boundary"><div class="errorbox">Application</div></div></div>');
  try {
    const style = dom.window.document.createElement("style"); style.textContent = css; dom.window.document.head.appendChild(style);
    const resolved = [];
    function visit(rules) {
      for (const rule of rules) {
        if (rule.type === 4) {
          const condition = rule.conditionText;
          const bounds = [...condition.matchAll(/\((min|max)-width:\s*(\d+)px\)/g)];
          if (bounds.length && bounds.every(([, kind, limit]) => kind === "max" ? width <= Number(limit) : width >= Number(limit))) visit(rule.cssRules);
        } else if (rule.selectorText?.split(",").some(selector => [".page", ".content > .error-boundary", "#root > .error-boundary"].includes(selector.trim()))) resolved.push(rule.cssText);
      }
    }
    visit(style.sheet.cssRules);
    const mediaStyle = dom.window.document.createElement("style"); // Use a zero safe-area inset: jsdom does not resolve CSS custom properties.
    mediaStyle.textContent = resolved.join("\n").replaceAll("calc(24px + var(--safe-bottom))", "24px"); dom.window.document.head.appendChild(mediaStyle);
    const get = id => dom.window.getComputedStyle(dom.window.document.getElementById(id));
    for (const side of ["Top", "Left", "Right", "Bottom"]) {
      assert.equal(get("failure")[`padding${side}`], get("normal")[`padding${side}`]);
      assert.equal(get("global")[`padding${side}`], get("normal")[`padding${side}`]);
      assert.equal(get("route")[`padding${side}`], get("normal")[`padding${side}`]);
    }
    assert.ok(parseFloat(get("failure").paddingLeft) > 0, JSON.stringify({ width, resolved, padding: get("failure").padding, left: get("failure").paddingLeft }));
    assert.equal(parseFloat(get("nested").paddingLeft) || 0, 0);
    assert.equal(parseFloat(dom.window.getComputedStyle(dom.window.document.querySelector("#failure > .errorbox")).marginTop), 0);
    assert.equal(dom.window.getComputedStyle(dom.window.document.querySelector("#failure > .errorbox")).overflowWrap, "anywhere");
  } finally { dom.window.close(); }
});
