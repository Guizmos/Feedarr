// Real React pages and session transport; only the browser layout engine is simulated.
import assert from "node:assert/strict";
import { build } from "esbuild";
import { createRequire } from "node:module";
import { mkdtemp, writeFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import React, { act } from "react";
import { MemoryRouter, Routes, Route } from "react-router-dom";
import { createDomEnvironment } from "./domEnvironment.mjs";
const require = createRequire(import.meta.url);
const source = fileURLToPath(new URL("../", import.meta.url));
const compiled = await build({
  stdin: { contents: `export {default as Gate} from './src/auth/AuthGate.jsx'; export {default as Login} from './src/pages/Login.jsx'; export {default as Shell} from './src/layout/Shell.jsx'; export {default as Library} from './src/pages/library/Library.jsx'; export {default as Indexers} from './src/pages/Indexers.jsx'; export {default as Settings} from './src/pages/Settings.jsx'; export * from './src/auth/session.js';`, resolveDir: fileURLToPath(new URL("../../", import.meta.url)), loader: "jsx" },
  alias: Object.fromEntries([["@", ""], ["@api", "api"], ["@utils", "utils"], ["@pages", "pages"], ["@ui", "ui"], ["@hooks", "hooks"], ["@layout", "layout"]].map(([key, value]) => [key, join(source, value)])),
  define: { "import.meta.env": JSON.stringify({ BASE_URL: "/", DEV: false }) },
  jsx: "automatic",
  bundle: true, platform: "node", format: "esm", write: false,
  plugins: [{ name: "shared-react", setup(b) { b.onResolve({ filter: /^(?:react(?:$|[-/])|lucide-react$)/ }, args => ({ path: pathToFileURL(require.resolve(args.path)).href, external: true })); } }],
});
const temporary = await mkdtemp(join(tmpdir(), "feedarr-auth-dom-"));
await writeFile(join(temporary, "app.mjs"), compiled.outputFiles[0].text);
export const app = await import(pathToFileURL(join(temporary, "app.mjs")));
await rm(temporary, { recursive: true });
export async function fixture(fetcher, path = "/library", origin = "https://feedarr.test") {
  const environment = createDomEnvironment({ url: `${origin}${path}` });
  const { dom } = environment;
  dom.window.HTMLElement.prototype.scrollTo = function () {};
  const requests = new Set();
  dom.window.fetch = fetcher;
  app.lockSession(); app.installSessionTransport();
  globalThis.fetch = (...args) => {
    const request = dom.window.fetch(...args); requests.add(request);
    void request.finally(() => requests.delete(request)).catch(() => {});
    return request;
  };
  const { createRoot } = await import("react-dom/client");
  const root = createRoot(document.getElementById("root"));
  const waitFor = async predicate => {
    const deadline = Date.now() + 8000;
    while (!predicate() && Date.now() < deadline) await act(async () => { await new Promise(resolve => setTimeout(resolve, 10)); });
    assert.ok(predicate(), `UI did not settle: ${document.body.textContent}`);
  };
  await act(async () => root.render(React.createElement(MemoryRouter, { initialEntries: [path] },
    React.createElement(Routes, null, React.createElement(Route, { element: React.createElement(app.Gate) },
      React.createElement(Route, { path: "/create-admin", element: React.createElement(app.Login, { createAdmin: true }) }),
      React.createElement(Route, { path: "/login", element: React.createElement(app.Login) }),
      React.createElement(Route, { element: React.createElement(app.Shell) },
        React.createElement(Route, { path: "/library", element: React.createElement(app.Library) }),
        React.createElement(Route, { path: "/indexers", element: React.createElement(app.Indexers) }),
        React.createElement(Route, { path: "/settings/backup", element: React.createElement(app.Settings) }),
        React.createElement(Route, { path: "/settings/users", element: React.createElement(app.Settings) })))))));
  const change = async (id, value) => {
    const input = document.getElementById(id); assert.ok(input, id);
    await act(async () => { Object.getOwnPropertyDescriptor(dom.window.HTMLInputElement.prototype, "value").set.call(input, value); input.dispatchEvent(new dom.window.Event("input", { bubbles: true })); });
  };
  return { waitFor, change,
    click: async text => { await waitFor(() => [...document.querySelectorAll("button,a")].some(e => e.textContent.trim() === text && !e.disabled)); await act(async () => [...document.querySelectorAll("button,a")].find(e => e.textContent.trim() === text).click()); },
    visibility: async value => { Object.defineProperty(document, "visibilityState", { configurable: true, value }); await act(async () => document.dispatchEvent(new dom.window.Event("visibilitychange"))); },
    close: async () => { await act(async () => { root.unmount(); app.lockSession(); await Promise.allSettled([...requests]); }); environment.restore(); },
  };
}
