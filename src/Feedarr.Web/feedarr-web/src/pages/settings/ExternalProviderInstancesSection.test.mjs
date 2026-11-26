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
  stdin: { contents: `export {default} from './src/pages/settings/ExternalProviderInstancesSection.jsx';`, resolveDir: fileURLToPath(new URL("../../../", import.meta.url)), loader: "jsx" },
  jsx: "automatic", bundle: true, platform: "node", format: "esm", write: false,
  define: { "import.meta.env": JSON.stringify({ BASE_URL: "/" }) },
  plugins: [{ name: "react", setup(b) { b.onResolve({ filter: /^(?:react(?:$|[-/])|lucide-react$)/ }, args => ({ path: pathToFileURL(require.resolve(args.path)).href, external: true })); } }],
});
const temp = await mkdtemp(join(tmpdir(), "feedarr-provider-ui-"));
await writeFile(join(temp, "providers.mjs"), compiled.outputFiles[0].text);
const { default: Providers } = await import(pathToFileURL(join(temp, "providers.mjs")));
await rm(temp, { recursive: true });

test("RAWG quota appears in existing settings and only an explicit test requests recovery", async () => {
  const env = createDomEnvironment(); let tests = 0;
  const { createRoot } = await import("react-dom/client");
  const container = document.createElement("div"); document.body.append(container);
  const root = createRoot(container);
  const controller = { definitions: [{ providerKey: "rawg", displayName: "RAWG", kind: "game" }], instances: [{ instanceId: "rawg", providerKey: "rawg", enabled: true, availability: { status: "quota_exhausted", reason: "rawg_quota_exhausted", suspended: true } }],
    testStatusByExternal: {}, isInstanceConfigured: () => true, testExternal: () => { tests++; } };
  try {
    await act(async () => root.render(React.createElement(Providers, { controller })));
    assert.match(container.textContent, /Quota mensuel épuisé/);
    assert.match(container.textContent, /Appels RAWG suspendus/);
    assert.doesNotMatch(container.textContent, /clé invalide/);
    assert.equal(tests, 0);
    const button = container.querySelector(".itemrow__action--science"); assert.ok(button);
    await act(async () => button.click()); assert.equal(tests, 1);
    controller.instances[0].availability = { status: "ready", suspended: false };
    await act(async () => root.render(React.createElement(Providers, { controller: { ...controller } })));
    assert.match(container.textContent, /Disponible/); assert.doesNotMatch(container.textContent, /Appels RAWG suspendus/);
    await act(async () => root.render(React.createElement(Providers, { controller: { ...controller, isInstanceConfigured: () => false } })));
    assert.match(container.textContent, /NO/); assert.doesNotMatch(container.textContent, /Disponible/);
  } finally { await act(async () => root.unmount()); env.restore(); }
});
