import test from "node:test";
import assert from "node:assert/strict";
import { configurationFileError, configurationUpload, configurationRequest, downloadConfiguration, MAX_CONFIGURATION_UPLOAD } from "./configurationBackupApi.js";

test("only the bounded current format is uploaded in multipart", () => {
  for (const file of [null, new File([], "a.feedarr"), new File(["old"], "a.zip"), { name: "a.feedarr", size: MAX_CONFIGURATION_UPLOAD + 1 }]) assert.ok(configurationFileError(file));
  const file = new File(["ciphertext"], "configuration.feedarr"); const form = configurationUpload(file, "password never in URL");
  assert.equal(form.get("password"), "password never in URL"); assert.equal(form.get("file").size, file.size);
});
test("export consumes its body, rejects proxy HTML and hides raw server errors", async () => {
  const blob = new Blob(["FEEDARR-CONFIG-3\nencrypted"]); let sent;
  const result = await configurationRequest("export", { body: { password: "local-only", confirmation: "local-only" }, binary: true, fetcher: async (url, options) => { sent = { url, ...options }; return new Response(blob); } });
  assert.equal(await result.text(), await blob.text()); assert.equal(sent.credentials, "include");
  assert.equal(sent.url, "/api/configuration-backups/export"); assert.equal(sent.headers["X-Feedarr-Request"], "1"); assert.ok(!sent.url.includes("local-only"));
  await assert.rejects(configurationRequest("export", { binary: true, fetcher: async () => new Response("<html>login</html>") }), /valide/);
  await assert.rejects(configurationRequest("preview", { fetcher: async () => Response.json({ code: "AuthenticationFailed", message: "PRIVATE" }, { status: 422 }) }), e => /Mot de passe incorrect/.test(e.message) && !e.message.includes("PRIVATE"));
});
test("multipart preserves browser boundary and download cleans object URL", async () => {
  const body = configurationUpload(new File(["encrypted"], "a.feedarr"), "long password");
  await configurationRequest("preview", { body, fetcher: async (_, init) => { assert.equal(init.body, body); assert.equal(init.headers["Content-Type"], undefined); return Response.json({}); } });
  let clicked = false, removed = false, revoked = false;
  const link = { click() { clicked = true; }, remove() { removed = true; } };
  downloadConfiguration(new Blob(["archive"]), { URL: { createObjectURL: () => "blob:local", revokeObjectURL: () => { revoked = true; } }, setTimeout: cb => cb() }, { createElement: () => link, body: { appendChild() {} } });
  assert.equal(link.download, "feedarr-configuration.feedarr"); assert.ok(clicked && removed && revoked);
});
