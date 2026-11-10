import test from "node:test";
import assert from "node:assert/strict";
import React from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { build } from "esbuild";
import { createRequire } from "node:module";
import { fileURLToPath, pathToFileURL } from "node:url";
import { mkdtempSync, writeFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

// Compile the actual JSX view using Vite's existing esbuild toolchain; no DOM dependency.
const require = createRequire(import.meta.url);
const result = await build({
  entryPoints: [fileURLToPath(new URL("../RestorePendingBanner.jsx", import.meta.url))],
  bundle: true, platform: "node", format: "esm", write: false,
  banner: { js: `import { createRequire as createNodeRequire } from "node:module"; const require = createNodeRequire(${JSON.stringify(new URL("../../../package.json", import.meta.url).href)});` },
  plugins: [{ name: "external-react", setup(builder) {
    builder.onResolve({ filter: /^react$/ }, () => ({ path: require.resolve("react"), external: true }));
  } }],
});
const temporary = mkdtempSync(join(tmpdir(), "feedarr-notice-test-"));
let RestorePendingNotice;
try {
  const bundle = join(temporary, "notice.mjs");
  writeFileSync(bundle, result.outputFiles[0].text);
  ({ RestorePendingNotice } = await import(pathToFileURL(bundle).href));
} finally { rmSync(temporary, { recursive: true, force: true }); }
const render = props => renderToStaticMarkup(React.createElement(RestorePendingNotice, props));

test("pending notice is absent by default and after application/cancellation", () => {
  assert.equal(render({}), "");
  assert.equal(render({ status: { pending: false, state: "applied" } }), "");
  assert.equal(render({ status: { pending: false, state: "cancelled" } }), "");
});

test("pending notice renders restart warning, data-loss explanation and cancel action", () => {
  const html = render({ status: { pending: true, id: "operation", missingPosters: 2 } });
  assert.match(html, /role="status"/);
  assert.match(html, /Restauration en attente/);
  assert.match(html, /Redémarrez le conteneur/);
  assert.match(html, /modifications effectuées depuis sa préparation/);
  assert.match(html, /2 référence/);
  assert.match(html, />Annuler<\/button>/);
  assert.doesNotMatch(html, /disabled/);
});

test("pending notice exposes cancellation errors and disables unsafe repeated actions", () => {
  const html = render({ status: { pending: true, id: "operation" }, error: "Annulation refusée", busy: true });
  assert.match(html, /role="alert"/);
  assert.match(html, /Annulation refusée/);
  assert.match(html, /disabled/);
  assert.match(render({ status: { pending: true } }), /disabled/);
  assert.match(render({ status: { pending: true, error: "restore_state_unreadable" } }), /Consultez les journaux/);
});
