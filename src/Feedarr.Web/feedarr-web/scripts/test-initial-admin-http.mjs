// Production ASP.NET + temporary SQLite, real HTTP and ephemeral Kestrel TLS.
import test from "node:test";
import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { request as httpRequest } from "node:http";
import { request as httpsRequest } from "node:https";
import { createServer } from "node:net";
import { mkdtemp, rm, readFile } from "node:fs/promises";
import { createWriteStream } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { CookieJar } from "jsdom";
import { fixture, app } from "../src/test/authDomFixture.mjs";
const api = resolve(fileURLToPath(new URL("../../../../", import.meta.url)), "src/Feedarr.Api");
const credentials = { username: "fixture-admin", password: "Fixture-Password42!", passwordConfirmation: "Fixture-Password42!" };
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
const run = (name, args) => new Promise((resolve, reject) => {
  const child = spawn(name, args); let error = "";
  child.stderr.on("data", data => { error += data; });
  child.once("error", reject); child.once("exit", code => code === 0 ? resolve() : reject(new Error(`${name} failed: ${error}`)));
});
const freePort = () => new Promise((resolve, reject) => {
  const probe = createServer(); probe.once("error", reject);
  probe.listen(0, "127.0.0.1", () => { const port = probe.address().port; probe.close(() => resolve(port)); });
});
async function backend({ tls = false, legacy = null } = {}) {
  const directory = await mkdtemp(join(tmpdir(), "feedarr-transport-http-"));
  const port = await freePort(), tlsPort = await freePort();
  const origin = `http://127.0.0.1:${port}`, secureOrigin = `https://127.0.0.1:${tlsPort}`;
  const jar = new CookieJar(); let recovery = false, disableTls = false, process, startupError;
  const output = createWriteStream(join(directory, "server.log"));
  try {
    if (tls) {
      await run("openssl", ["req", "-x509", "-newkey", "rsa:2048", "-nodes", "-keyout", join(directory, "tls.key"), "-out", join(directory, "tls.crt"), "-days", "1", "-subj", "/CN=127.0.0.1"]);
      await run("openssl", ["pkcs12", "-export", "-out", join(directory, "tls.pfx"), "-inkey", join(directory, "tls.key"), "-in", join(directory, "tls.crt"), "-passout", "pass:fixture-pfx"]);
    }
    if (legacy) {
      await run("python3", ["-c", `import sqlite3,glob,os,sys,json,time,hashlib,base64
c=sqlite3.connect(sys.argv[1]);c.execute('CREATE TABLE schema_migrations(id TEXT PRIMARY KEY,applied_at_ts INTEGER NOT NULL)')
for f in sorted(glob.glob(sys.argv[2]+'/*.sql')):
 if os.path.basename(f).startswith('0061'):continue
 c.executescript(open(f).read() if not os.path.basename(f).startswith('0047') else 'SELECT 1;');c.execute('INSERT INTO schema_migrations VALUES(?,0)',(os.path.basename(f),));c.commit()
salt=b'0123456789abcdef';mode=sys.argv[3];security={'authMode':mode,'authentication':'none' if mode=='open' else 'basic','authenticationRequired':'all' if mode=='strict' else 'local'}
if mode!='open':security.update(username='fixture-admin',passwordHash=base64.b64encode(hashlib.pbkdf2_hmac('sha256',b'Fixture-Password42!',salt,100000)).decode(),passwordSalt=base64.b64encode(salt).decode())
c.execute('INSERT INTO app_settings VALUES(?,?,?)',('security',json.dumps(security),int(time.time())))
if mode!='open':c.execute('INSERT INTO web_sessions VALUES(?,?,?,?,?,?,?)',(hashlib.sha256(b'A'*64).hexdigest().upper(),c.execute('SELECT generation FROM web_security_generation WHERE id=1').fetchone()[0],int(time.time()),int(time.time()),int(time.time())+3600,1800,0))
c.commit();c.close()`, join(directory, "feedarr.db"), join(api, "Data/Migrations"), legacy]);
      if (legacy !== "open") jar.setCookieSync(`__Host-FeedarrSession=${"A".repeat(64)}; Secure; HttpOnly; SameSite=Lax; Path=/`, secureOrigin);
    }
    const launch = () => {
      const env = Object.fromEntries(Object.entries(globalThis.process.env).filter(([key]) => !key.startsWith("Security__Known") && key !== "App__Security__EnforceHttps" && key !== "FEEDARR_BOOTSTRAP_SECRET" && key !== "App__Security__BootstrapSecret"));
      Object.assign(env, { ASPNETCORE_ENVIRONMENT: "Production", ASPNETCORE_URLS: origin + (tls && !disableTls ? `;${secureOrigin}` : ""), App__DataDir: directory,
        App__Updates__Enabled: "false", Security__AllowHttpRecovery: String(recovery) });
      if (tls) Object.assign(env, { Kestrel__Certificates__Default__Path: join(directory, "tls.pfx"), Kestrel__Certificates__Default__Password: "fixture-pfx" });
      process = spawn("dotnet", [join(api, "bin/Release/net8.0/Feedarr.Api.dll")], { cwd: api, env, stdio: ["ignore", "pipe", "pipe"] });
      process.once("error", error => { startupError = error; }); process.stdout.pipe(output, { end: false }); process.stderr.pipe(output, { end: false });
    };
    const fetcher = (url, init = {}, { anonymous = false, https = false } = {}) => new Promise((resolve, reject) => {
      const target = new URL(url, https ? secureOrigin : origin);
      const headers = Object.fromEntries(new Headers(init.headers));
      Object.assign(headers, { "Content-Type": "application/json", "X-Feedarr-Request": "1", "X-Feedarr-Web": "1", Origin: target.origin });
      if (!anonymous) headers.Cookie = jar.getCookieStringSync(target.href);
      const request = (target.protocol === "https:" ? httpsRequest : httpRequest)(target, { method: init.method || "GET", headers, signal: init.signal, rejectUnauthorized: false }, response => {
        const chunks = []; response.on("data", chunk => chunks.push(chunk)); response.on("error", reject);
        response.on("end", () => {
          for (const cookie of response.headers["set-cookie"] || []) jar.setCookieSync(cookie, target.href);
          resolve(new Response(Buffer.concat(chunks), { status: response.statusCode, headers: Object.fromEntries(Object.entries(response.headers).map(([key, value]) => [key, Array.isArray(value) ? value.join(",") : value])) }));
        });
      });
      request.on("error", reject); if (init.body) request.write(init.body); request.end();
    });
    const ready = async () => {
      for (let i = 0; i < 150; i++) {
        if (startupError || process.exitCode !== null) throw startupError || new Error(await readFile(join(directory, "server.log"), "utf8"));
        try { if ((await fetcher("/health")).ok) return; } catch { /* starting */ }
        await delay(40);
      }
      throw new Error("Backend did not start");
    };
    const stopProcess = async () => { if (!process || process.exitCode !== null) return; process.kill("SIGTERM"); await new Promise(resolve => process.once("exit", resolve)); };
    const restart = async (options = {}) => { await stopProcess(); recovery = options.recovery ?? recovery; disableTls = options.disableTls ?? disableTls; startupError = null; launch(); await ready(); };
    launch(); await ready();
    return { origin, secureOrigin, fetcher, restart, jar,
      logs: () => readFile(join(directory, "server.log"), "utf8"),
      stop: async () => { await stopProcess(); output.end(); await rm(directory, { recursive: true, force: true }); },
    };
  } catch (error) { process?.kill("SIGTERM"); output.end(); await rm(directory, { recursive: true, force: true }); throw error; }
}
const create = (server, options = {}) => server.fetcher("/api/auth/create-admin", { method: "POST", body: JSON.stringify(credentials) }, options);
const state = async (server, options = {}) => (await server.fetcher("/api/auth/session", {}, options)).json();
const login = (server, options = {}) => server.fetcher("/api/auth/login", { method: "POST", body: JSON.stringify(credentials) }, options);
const policy = async (server, requireHttps, options = {}) => server.fetcher("/api/settings/security", { method: "PUT", headers: { "X-Feedarr-CSRF": (await state(server, options)).csrfToken }, body: JSON.stringify({ requireHttps }) }, options);

test("direct IP HTTP: owner creates administrator, auto-login, actual pages, logout/login and restart", async () => {
  const server = await backend(); let f;
  try {
    f = await fixture(server.fetcher, "/library", server.origin);
    await f.waitFor(() => document.getElementById("admin-confirmation"));
    await f.change("login-username", credentials.username); await f.change("login-password", credentials.password);
    await f.change("admin-confirmation", credentials.password);
    const password = document.getElementById("login-password"); await f.visibility("hidden"); await f.visibility("visible");
    assert.equal(document.getElementById("login-password"), password); assert.equal(password.value, credentials.password);
    assert.equal(document.querySelector("[name=bootstrap]"), null);
    await f.click("Créer le compte"); await f.waitFor(() => document.body.textContent.includes("Bienvenue dans Feedarr"));
    assert.equal(app.getSessionSnapshot().state.authenticated, true);
    assert.match(document.querySelector(".http-transport-notice").textContent, /non chiffrée/);
    const cookie = server.jar.getCookiesSync(server.origin).find(cookie => cookie.key === "FeedarrSession");
    assert.ok(cookie.httpOnly); assert.equal(cookie.sameSite, "lax"); assert.equal(cookie.secure, false);
    await f.click("Restaurer une sauvegarde"); await f.waitFor(() => document.body.textContent.includes("Restaurer une configuration"));
    assert.equal((await server.fetcher("/api/configuration-backups/state")).status, 200);
    await f.close(); f = null;
    const csrf = (await state(server)).csrfToken;
    assert.equal((await server.fetcher("/api/auth/logout", { method: "POST", headers: { "X-Feedarr-CSRF": csrf }, body: "{}" })).status, 200);
    assert.equal((await server.fetcher("/api/sources")).status, 401);
    assert.equal((await login(server)).status, 200);
    await server.restart(); assert.equal((await state(server)).authenticated, true);
    assert.equal((await server.fetcher("/api/sources")).status, 200);
    assert.equal((await create(server)).status, 409);
    assert.equal((await server.fetcher("/api/settings/security", { method: "PUT", body: JSON.stringify({ requireHttps: true }) })).status, 403);
    assert.equal((await policy(server, true)).status, 400); // HTTPS not yet configured/used.
    const logs = await server.logs(); assert.equal(logs.includes(credentials.password), false);
  } finally { if (f) await f.close(); await server.stop(); }
});

test("real TLS activation, refusal of all private HTTP, and owner recovery when TLS disappears", async () => {
  const server = await backend({ tls: true });
  try {
    assert.equal((await create(server, { https: true })).status, 200);
    assert.equal((await policy(server, true, { https: true })).status, 200);
    assert.equal((await server.fetcher("/api/sources")).status, 403);
    assert.equal((await login(server)).status, 403);
    assert.equal((await server.fetcher("/api/sources", { headers: { "X-Forwarded-Proto": "https" } })).status, 403);
    assert.equal((await login(server, { https: true })).status, 200);
    assert.equal((await server.fetcher("/api/sources", {}, { https: true })).status, 200);
    const cookie = server.jar.getCookiesSync(server.secureOrigin).find(cookie => cookie.key === "__Host-FeedarrSession"); assert.ok(cookie.secure && cookie.httpOnly);
    await server.restart({ recovery: true, disableTls: true });
    assert.equal((await server.fetcher("/api/sources", {}, { anonymous: true })).status, 401);
    assert.equal((await login(server)).status, 200); assert.equal((await state(server)).httpRecovery, true);
    assert.equal((await policy(server, false)).status, 200); assert.equal((await login(server)).status, 200);
    await server.restart({ recovery: false });
    assert.equal((await state(server)).requireHttps, false); assert.equal((await server.fetcher("/api/sources")).status, 200);
  } finally { await server.stop(); }
});

test("explicit legacy migration preserves protected accounts and Secure sessions, Open gains an administrator account", async () => {
  for (const legacy of ["smart", "strict", "open"]) {
    const server = await backend({ tls: legacy !== "open", legacy });
    try {
      const migrated = await state(server);
      assert.equal(migrated.authRequired, true);
      if (legacy === "open") {
        assert.equal(migrated.initialAdminRequired, true); assert.equal(migrated.requireHttps, false);
        assert.equal((await create(server)).status, 200);
      } else {
        assert.equal(migrated.requireHttps, true); assert.equal(migrated.initialAdminRequired, false);
        assert.equal((await state(server, { https: true })).authenticated, true); // The pre-migration session survived.
        assert.equal((await server.fetcher("/api/sources")).status, 403);
        assert.equal((await login(server, { https: true })).status, 200);
        const security = await (await server.fetcher("/api/settings/security", {}, { https: true })).json();
        assert.equal(security.transportMigration, legacy); assert.equal("authMode" in security, false);
      }
    } finally { await server.stop(); }
  }
});

test("no bootstrap: concurrent initial creation is atomic and private access still requires authentication", async () => {
  const server = await backend();
  try {
    const results = await Promise.all([create(server), create(server)]); assert.deepEqual(results.map(r => r.status).sort(), [200, 409]);
    assert.equal((await server.fetcher("/api/sources", {}, { anonymous: true })).status, 401);
    const response = await server.fetcher("/api/settings/security", { method: "PUT", headers: { "X-Feedarr-CSRF": (await state(server)).csrfToken }, body: JSON.stringify({ authMode: "open" }) });
    assert.equal(response.status, 400); assert.equal((await response.json()).title, "legacy_auth_modes_removed");
  } finally { await server.stop(); }
});
