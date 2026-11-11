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
import { backupRoot, configurationRequest, configurationUpload, downloadConfiguration, resumeSetupSteps, MAX_CONFIGURATION_UPLOAD } from "./configurationBackupApi.js";
const require = createRequire(import.meta.url);
const compiled = await build({ entryPoints: [fileURLToPath(new URL("./ConfigurationBackupPanel.jsx", import.meta.url))], banner: { js: `import { createRequire } from "node:module"; const require = createRequire(${JSON.stringify(new URL("../../../package.json", import.meta.url).href)});` }, bundle: true, platform: "node", format: "esm", write: false,
  plugins: [{ name: "react", setup(b) { b.onResolve({ filter: /^react$/ }, () => ({ path: require.resolve("react"), external: true })); } }], });
const directory = mkdtempSync(join(tmpdir(), "feedarr-config-ui-"));
let Panel, Preview;
try { const path = join(directory, "view.mjs"); writeFileSync(path, compiled.outputFiles[0].text); ({ default: Panel, ConfigurationPreview: Preview } = await import(pathToFileURL(path).href)); }
finally { rmSync(directory, { recursive: true, force: true }); }
const render = (component, props) => renderToStaticMarkup(React.createElement(component, props));
test("setup offers import only; settings offer encrypted export and legacy import", () => {
  const initial = render(Panel, { initialSetup: true }); assert.match(initial, /Restaurer une configuration/); assert.match(initial, /bootstrap/); assert.doesNotMatch(initial, /Sauvegarder la configuration/);
  const settings = render(Panel, {}); assert.match(settings, /Sauvegarder la configuration/); assert.match(settings, /Confirmer la phrase secrète/); assert.match(settings, /ancienne sauvegarde compatible/);
  assert.match(settings, /<form noValidate=""/);
  assert.match(settings, /De 12 à 1 024 caractères/);
  assert.doesNotMatch(settings, /settings-card--full/);
  assert.doesNotMatch(settings, /<button[^>]*type="submit"/);
  assert.equal(backupRoot(true), "/api/setup/configuration-restore"); assert.equal(backupRoot(false), "/api/configuration-backups");
});
test("preview warns about history, missing secrets and purge without rendering private fields", () => {
  const html = render(Preview, { preview: { configuration: { parameters: 8, sources: 2, missingSecrets: 1, apiKey: "PRIVATE" }, disabledHistoricalSources: 3, stricterRetention: true, hasLibrary: true, canCompleteSetup: false, url: "PRIVATE" } });
  for (const pattern of [/3 source/, /1 secret/, /rétention/, /purge/, /confirmation explicite/, /étapes nécessaires/, /bibliothèque/]) assert.match(html, pattern);
  assert.doesNotMatch(html, /PRIVATE/); assert.equal(render(Preview, { preview: null }), "");
});
test("wizard resumes only valid missing steps in order", () => {
  assert.deepEqual(resumeSetupSteps([8, 3, 3, 0, "4", 6, 99]), [3, 6, 8]); assert.deepEqual(resumeSetupSteps([8]), [8]); assert.equal(resumeSetupSteps(null), null);
});
test("upload is bounded and preserves archive and phrase in multipart only", () => {
  for (const file of [null, { size: 0 }, { size: MAX_CONFIGURATION_UPLOAD + 1 }]) assert.throws(() => configurationUpload(file, "secret"));
  const file = new Blob(["archive"]); const form = configurationUpload(file, "one-time phrase"); assert.equal(form.get("passphrase"), "one-time phrase"); assert.equal(form.get("archive").size, file.size);
});
test("requests send auth and CSRF headers; server errors never expose private messages", async () => {
  let sent; const fetcher = async (url, options) => { sent = { url, ...options }; return { ok: true, status: 200, json: async () => ({ confirmed: true }) }; };
  assert.deepEqual(await configurationRequest("/api/setup/configuration-restore/complete", { method: "POST", bootstrap: "bootstrap-secret", body: { confirm: true }, fetcher }), { confirmed: true });
  assert.equal(sent.credentials, "include"); assert.equal(sent.headers["X-Feedarr-Request"], "1"); assert.equal(sent.headers["X-Bootstrap-Secret"], "bootstrap-secret"); assert.equal(sent.body, '{"confirm":true}');
  for (const [status, code, pattern] of [[422, "AuthenticationFailed", /Phrase secrète incorrecte/], [413, "anything", /volumineux/], [429, "anything", /Trop de demandes/], [403, "anything", /Autorisation/], [500, "anything", /échoué/]])
    await assert.rejects(configurationRequest("/api/test", { fetcher: async () => ({ ok: false, status, json: async () => ({ code, message: "PRIVATE-TOKEN-URL" }) }) }), e => pattern.test(e.message) && !e.message.includes("PRIVATE"));
});
test("multipart leaves boundary to browser and encrypted export consumes binary body", async () => {
  const body = configurationUpload(new Blob(["archive"]), "phrase"), blob = new Blob([new Uint8Array([0x50,0x4b,3,4]), "encrypted"]);
  assert.equal(await configurationRequest("/api/test", { method: "POST", body, binary: true, fetcher: async (_, options) => { assert.equal(options.body, body); assert.equal(options.headers["Content-Type"], undefined); return { ok: true, status: 200, blob: async () => blob }; } }), blob);
});
test("external abort propagates and listeners are removed on success, invalid JSON and cancellation", async () => {
  const controller = new AbortController(); let attached = 0;
  const add = controller.signal.addEventListener.bind(controller.signal), remove = controller.signal.removeEventListener.bind(controller.signal);
  controller.signal.addEventListener = (...args) => { attached++; add(...args); }; controller.signal.removeEventListener = (...args) => { attached--; remove(...args); };
  for (const fail of [false, true]) { const pending = configurationRequest("/api/test", { signal: controller.signal, fetcher: async () => ({ ok: true, status: 200, json: async () => { if (fail) throw new SyntaxError(); return {}; } }) }); if (fail) await assert.rejects(pending, SyntaxError); else await pending; assert.equal(attached, 0); }
  const pending = configurationRequest("/api/test", { signal: controller.signal, fetcher: async (_, { signal }) => new Promise((_, reject) => signal.addEventListener("abort", () => reject(signal.reason), { once: true })) }); controller.abort(); await assert.rejects(pending, e => e.name === "AbortError"); assert.equal(attached, 0);
});
test("download has safe fixed name and cleans its object URL", () => {
  let clicked = 0, removed = 0, revoked; const link = { click: () => clicked++, remove: () => removed++ };
  downloadConfiguration(new Blob(["zip"]), { URL: { createObjectURL: () => "blob:test", revokeObjectURL: u => { revoked = u; } }, setTimeout: action => action() }, { createElement: () => link, body: { appendChild: () => {} } });
  assert.equal(link.download, "feedarr-configuration.zip"); assert.equal(clicked, 1); assert.equal(removed, 1); assert.equal(revoked, "blob:test");
});
test("export refuses HTTP 200 HTML or empty bodies and reports network failure",async()=>{
  for(const blob of [new Blob(),new Blob(['<html>Login</html>'])])await assert.rejects(configurationRequest('/api/test',{binary:true,fetcher:async()=>({ok:true,status:200,blob:async()=>blob})}),/archive ZIP valide/);
  await assert.rejects(configurationRequest('/api/test',{fetcher:async()=>{throw new TypeError('Failed to fetch');}}),/Connexion au serveur impossible/);
  assert.throws(()=>downloadConfiguration(new Blob(), {}, {}),/archive reçue est vide/);
});
test("failed download clicks still remove the anchor and release its URL",()=>{
  let revoked=false,removed=false;
  assert.throws(()=>downloadConfiguration(new Blob(['zip']),{URL:{createObjectURL:()=>"blob:test",revokeObjectURL:()=>{revoked=true;}},setTimeout:fn=>fn()},{createElement:()=>({click(){throw new Error('Download blocked');},remove(){removed=true;}}),body:{appendChild(){}}}),/Download blocked/);
  assert.equal(revoked,true);assert.equal(removed,true);
});
