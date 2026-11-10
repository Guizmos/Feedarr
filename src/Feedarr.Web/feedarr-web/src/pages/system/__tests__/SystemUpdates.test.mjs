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
  entryPoints: [fileURLToPath(new URL("../SystemUpdates.jsx", import.meta.url))],
  bundle: true, platform: "node", format: "esm", write: false,
  banner: { js: `import { createRequire as createNodeRequire } from "node:module"; const require = createNodeRequire(${JSON.stringify(new URL("../../../../package.json", import.meta.url).href)});` },
  plugins: [{ name: "external-react", setup(builder) {
    builder.onResolve({ filter: /^react$/ }, () => ({ path: require.resolve("react"), external: true }));
  } }],
});
const temporary = mkdtempSync(join(tmpdir(), "feedarr-notice-test-"));
let SystemUpdates;
try {
  const bundle = join(temporary, "notice.mjs");
  writeFileSync(bundle, result.outputFiles[0].text);
  ({ default: SystemUpdates } = await import(pathToFileURL(bundle).href));
} finally { rmSync(temporary, { recursive: true, force: true }); }
const render = props => renderToStaticMarkup(React.createElement(SystemUpdates, { updatesEnabled: true, releases: [], ...props }));

test("release retrieval errors are explicit and keep cached changelog", () => {
  const html = render({ error: "Connexion GitHub impossible", latestRelease: { tagName: "v2.5.17" }, releases: [{ tagName: "v2.5.17", body: "Changelog conservé" }] });
  assert.match(html, /Erreur de récupération/);
  assert.match(html, /Connexion GitHub impossible/);
  assert.match(html, /Changelog conservé/);
  assert.doesNotMatch(html, /Inconnu/);
});

test("empty release list differs from a loading or error state", () => {
  assert.match(render({}), /Aucune release disponible/);
  assert.match(render({ loading: true }), /Chargement/);
});

test("successful release displays version, publication date and changelog", () => {
  const release = { tagName: "v2.5.17", publishedAt: "2026-10-27T18:12:38Z", body: "Correctifs release" };
  const html = render({ latestRelease: release, releases: [release] });
  assert.match(html, /v2.5.17/);
  assert.match(html, /2026/);
  assert.match(html, /Correctifs release/);
});
