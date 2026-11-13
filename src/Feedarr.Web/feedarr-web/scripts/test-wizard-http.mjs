// Real React/transport -> HTTP -> ASP.NET middleware/controllers -> isolated SQLite.
// Requires the Release backend build; never uses Docker or an existing data directory.
import test from "node:test";
import assert from "node:assert/strict";
import { act } from "react";
import { spawn } from "node:child_process";
import { request } from "node:http";
import { createServer } from "node:net";
import { mkdtemp, rm, readFile } from "node:fs/promises";
import { createWriteStream } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { fixture, app } from "../src/test/wizardDomFixture.mjs";
const root = fileURLToPath(new URL("../../../../", import.meta.url));
const api = resolve(root, "src/Feedarr.Api");
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));

async function backend({ remote = true } = {}) {
  const directory = await mkdtemp(join(tmpdir(), "feedarr-wizard-http-"));
  const port = await new Promise((resolve, reject) => {
    const probe = createServer(); probe.once("error", reject); probe.listen(0, "127.0.0.1", () => { const p = probe.address().port; probe.close(() => resolve(p)); });
  });
  const output = createWriteStream(join(directory, "server.log"));
  const process = spawn("dotnet", [join(api, "bin/Release/net8.0/Feedarr.Api.dll")], {
    cwd: api, env: { ...globalThis.process.env, ASPNETCORE_ENVIRONMENT: "Development", ASPNETCORE_URLS: `http://127.0.0.1:${port}`, App__DataDir: directory, App__Updates__Enabled: "false", ...(remote ? { Security__KnownProxies__0: "127.0.0.1", Security__TrustedReverseProxyTls: "true" } : {}), App__Security__BootstrapSecret: "fixture-bootstrap" }, stdio: ["ignore", "pipe", "pipe"],
  });
  let startupError; process.once("error", error => { startupError = error; });
  process.stdout.pipe(output); process.stderr.pipe(output);
  const events = []; let cookie = "";
  const fetcher = (url, init = {}, { anonymous = false, scheme = "https" } = {}) => new Promise((resolve, reject) => {
    const path = new URL(url, "https://feedarr.test").pathname;
    const headers = Object.fromEntries(new Headers(init.headers));
    Object.assign(headers, { "Content-Type": "application/json", "X-Feedarr-Request": "1", "X-Feedarr-Web": "1" });
    if (remote) Object.assign(headers, { "X-Forwarded-For": "203.0.113.44", "X-Forwarded-Proto": scheme, "X-Forwarded-Host": "feedarr.test", Origin: `${scheme}://feedarr.test` });
    else headers.Origin = `http://127.0.0.1:${port}`;
    if (cookie && !anonymous) headers.Cookie = cookie;
    const req = request({ hostname: "127.0.0.1", port, path, method: init.method || "GET", headers, signal: init.signal }, res => {
      const chunks = []; res.on("data", chunk => chunks.push(chunk)); res.on("error", reject);
      res.on("end", () => {
        if (res.headers["set-cookie"]) cookie = res.headers["set-cookie"].map(value => value.split(";")[0]).join("; ");
        events.push({ path, method: init.method || "GET", status: res.statusCode });
        resolve(new Response(Buffer.concat(chunks), { status: res.statusCode, headers: Object.fromEntries(Object.entries(res.headers).map(([key, value]) => [key, Array.isArray(value) ? value.join(",") : value])) }));
      });
    });
    req.on("error", reject); if (init.body) req.write(init.body); req.end();
  });
  let stopped = false;
  const stop = async () => {
    if (stopped) return; stopped = true;
    if (!process.pid || process.exitCode !== null) { output.end(); await rm(directory, { recursive: true, force: true }); return; }
    process.kill("SIGTERM");
    await Promise.race([new Promise(resolve => process.once("exit", resolve)), delay(3000)]);
    if (process.exitCode === null) { process.kill("SIGKILL"); await new Promise(resolve => process.once("exit", resolve)); }
    output.end(); await rm(directory, { recursive: true, force: true });
  };
  try {
    let ready = false;
    for (let i = 0; i < 100; i++) {
      if (startupError) throw startupError;
      if (process.exitCode !== null) throw new Error(await readFile(join(directory, "server.log"), "utf8"));
      try { const response = await fetcher("/api/auth/session"); if (response.ok) { ready = true; break; } } catch { /* startup */ }
      await delay(50);
    }
    assert.ok(ready, "Isolated backend failed to start"); events.length = 0;
    return { fetcher, events, stop };
  } catch (error) { await stop(); throw error; }
}
const nextReady = () => [...document.querySelectorAll("button")].some(b => b.textContent.trim() === "Suivant" && !b.disabled);
const metadata = () => /Ajoute au moins un provider/.test(document.body.textContent);
const key = async f => { const input = document.querySelector("#setup-bootstrap"); if (input) await f.change(input, "fixture-bootstrap"); };

for (const remote of [false, true]) test(`real HTTP: fresh Open ${remote ? "behind trusted HTTPS proxy with bootstrap" : "local without bootstrap"}, then reload and Metadata`, async () => {
  const server = await backend({ remote }); let f;
  try {
    f = await fixture({ fetcher: server.fetcher }); await f.security();
    assert.equal(!!document.querySelector("#setup-bootstrap"), remote);
    const password = [...document.querySelectorAll('input[type="password"]')].find(i => i.id !== "setup-bootstrap");
    await f.change(password, "Autofilled-Password42!");
    await f.change(document.querySelector("select"), "open"); await key(f);
    const before = server.events.length; await f.click("Enregistrer"); await f.waitFor(nextReady);
    const writes = f.h.calls.filter(c => c.path === "/api/settings/security" && c.method === "PUT"); assert.equal(writes.length, 1);
    assert.deepEqual(JSON.parse(writes[0].body), { authMode: "open", publicBaseUrl: "" });
    assert.equal(server.events.slice(before).filter(c => c.path === "/api/setup/bootstrap-token").length, remote ? 1 : 0);
    assert.deepEqual(server.events.slice(before).filter(c => c.path === "/api/settings/security").map(c => [c.method,c.status]), [["PUT",200],["GET",200]]);
    assert.equal(app.getSessionSnapshot().state.authRequired, false);
    await f.click("Suivant"); await f.waitFor(metadata);
    await f.close(); f = null;
    f = await fixture({ fetcher: server.fetcher, storage: { "feedarr:setupPath": "install", "feedarr:setupStep": "3" } });
    await f.waitFor(nextReady); assert.equal(document.querySelector("#setup-bootstrap"), null);
    await f.click("Suivant"); await f.waitFor(metadata);
    console.log("HTTP Open trace", remote ? "proxy" : "local", JSON.stringify(server.events.filter(e => /auth\/session|settings\/security|bootstrap-token/.test(e.path))));
  } finally { if (f) await f.close(); await server.stop(); }
});

for (const mode of ["smart", "strict"]) test(`real HTTP: ${mode}, session/CSRF and refusal of unauthorized downgrade`, async () => {
  const server = await backend(); let f;
  try {
    f = await fixture({ fetcher: server.fetcher }); await f.security(); await key(f);
    await f.change(document.querySelector("select"), mode);
    await f.change([...document.querySelectorAll("input")].find(i => /username/i.test(i.placeholder)), "fixture-admin");
    for (const input of document.querySelectorAll('input[type="password"]')) if (input.id !== "setup-bootstrap") await f.change(input, "Fixture-Password42!");
    await f.click("Enregistrer"); await f.waitFor(nextReady);
    const state = app.getSessionSnapshot().state; assert.equal(state.authenticated, true); assert.ok(state.csrfToken);
    await f.click("Suivant"); await f.waitFor(metadata);
    const unauthorized = await server.fetcher("/api/settings/security", { method: "PUT", body: JSON.stringify({ authMode: "open" }) }, { anonymous: true }); assert.equal(unauthorized.status, 401);
    const csrf = await server.fetcher("/api/settings/security", { method: "PUT", body: JSON.stringify({ authMode: "open" }) }); assert.equal(csrf.status, 403);
    const downgrade = await server.fetcher("/api/settings/security", { method: "PUT", headers: { "X-Feedarr-CSRF": state.csrfToken }, body: JSON.stringify({ authMode: "open" }) });
    assert.equal(downgrade.status, 400); const downgradeBody = await downgrade.json(); assert.equal(downgradeBody.title, "downgrade_confirmation_required", JSON.stringify(downgradeBody));
    const persisted = await (await server.fetcher("/api/settings/security")).json(); assert.equal(persisted.authMode, mode);
  } finally { if (f) await f.close(); await server.stop(); }
});

test("real HTTP: invalid bootstrap exposes HTTP 403 and recovers without a login loop", async () => {
  const server = await backend(); let f;
  try {
    f = await fixture({ fetcher: server.fetcher }); await f.security(); await f.change(document.querySelector("select"), "open");
    await f.change(document.querySelector("#setup-bootstrap"), "invalid-bootstrap"); await f.click("Enregistrer");
    await f.waitFor(() => /HTTP 403/.test(document.body.textContent)); assert.equal(server.events.some(e => e.method === "PUT"), false);
    await key(f); await f.click("Enregistrer"); await f.waitFor(nextReady); await f.click("Suivant"); await f.waitFor(metadata);
    assert.equal(server.events.filter(e => e.path === "/api/auth/login").length, 0);
  } finally { if (f) await f.close(); await server.stop(); }
});

test("real HTTP: a pre-write session request cannot keep Open saving after an HTTP 200 PUT", async () => {
  const server = await backend(); let f, held, release;
  let hold = false;
  const gate = new Promise(resolve => { release = resolve; });
  const fetcher = async (url, init) => {
    const shouldHold = hold && url.endsWith("/api/auth/session"); if (shouldHold) hold = false;
    const response = await server.fetcher(url, init);
    if (shouldHold) { held = true; await gate; }
    return response;
  };
  try {
    f = await fixture({ fetcher }); await f.security(); await f.change(document.querySelector("select"), "open"); await key(f);
    hold = true;
    let old;
    await act(async () => { old = app.refreshSession({ force: true }).catch(error => error); });
    await f.waitFor(() => held);
    await f.visibility("hidden"); // anonymous wizard stays mounted
    await f.click("Enregistrer");
    try { await f.waitFor(nextReady); }
    catch (error) { error.message += `; HTTP trace: ${JSON.stringify(server.events.filter(event => /auth\/session|settings\/security|bootstrap-token/.test(event.path)))}`; throw error; }
    // Must finish before releasing the old response.
    assert.equal((await old).name, "AbortError");
    await f.visibility("visible"); await f.click("Suivant"); await f.waitFor(metadata);
    assert.equal(server.events.filter(e => e.path === "/api/setup/bootstrap-token").length, 1);
  } finally { release(); if (f) await f.close(); await server.stop(); }
});

test("real HTTP: tab change during the PUT response does not repeat bootstrap or lock Open", async () => {
  const server = await backend(); let f, putReceived, release;
  const gate = new Promise(resolve => { release = resolve; });
  const fetcher = async (url, init) => {
    const response = await server.fetcher(url, init);
    if (url.endsWith("/api/settings/security") && init.method === "PUT") { putReceived = true; await gate; }
    return response;
  };
  try {
    f = await fixture({ fetcher }); await f.security(); await f.change(document.querySelector("select"), "open"); await key(f);
    await f.click("Enregistrer"); await f.waitFor(() => putReceived);
    assert.equal([...document.querySelectorAll("button")].find(b => b.textContent === "Retour au choix").disabled, true);
    await f.visibility("hidden"); await f.visibility("visible");
    release(); await f.waitFor(nextReady); await f.click("Suivant"); await f.waitFor(metadata);
    assert.equal(server.events.filter(e => e.path === "/api/setup/bootstrap-token").length, 1);
    assert.equal(server.events.filter(e => e.path === "/api/auth/login").length, 0);
  } finally { release(); if (f) await f.close(); await server.stop(); }
});
