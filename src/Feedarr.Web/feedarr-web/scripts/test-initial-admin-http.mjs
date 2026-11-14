// Production ASP.NET + temporary SQLite, real HTTP and ephemeral Kestrel TLS.
import test from "node:test";
import assert from "node:assert/strict";
import { act } from "react";
import { pbkdf2Sync, createDecipheriv } from "node:crypto";
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
const NativeFormData = globalThis.FormData;
const credentials = { username: "fixture-admin", password: "Fixture-Password42!", passwordConfirmation: "Fixture-Password42!" };
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
const run = (name, args) => new Promise((resolve, reject) => {
  const child = spawn(name, args); let error = "", output = "";
  child.stdout.on("data", data => { output += data; });
  child.stderr.on("data", data => { error += data; });
  child.once("error", reject); child.once("exit", code => code === 0 ? resolve(output) : reject(new Error(`${name} failed: ${error}`)));
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
    const fetcher = async (url, init = {}, { anonymous = false, https = false } = {}) => {
      const headers = Object.fromEntries(new Headers(init.headers));
      let body = init.body;
      if (body && typeof body.entries === "function") {
        const form = new NativeFormData();
        for (const [key, value] of body.entries()) {
          if (typeof value === "string") form.append(key, value);
          else {
            const bytes = value.arrayBuffer ? await value.arrayBuffer() : await new Promise((resolve, reject) => {
              const reader = new window.FileReader(); reader.onload = () => resolve(reader.result); reader.onerror = reject; reader.readAsArrayBuffer(value);
            });
            form.append(key, new Blob([bytes]), value.name);
          }
        }
        const encoded = new Response(form); headers["content-type"] = encoded.headers.get("content-type"); body = Buffer.from(await encoded.arrayBuffer());
      }
      headers["content-type"] ||= "application/json";
      return new Promise((resolve, reject) => {
      const target = new URL(url, https ? secureOrigin : origin);
      Object.assign(headers, { "X-Feedarr-Request": "1", "X-Feedarr-Web": "1", Origin: target.origin });
      if (!anonymous) headers.Cookie = jar.getCookieStringSync(target.href);
      const request = (target.protocol === "https:" ? httpsRequest : httpRequest)(target, { method: init.method || "GET", headers, signal: init.signal, rejectUnauthorized: false }, response => {
        const chunks = []; response.on("data", chunk => chunks.push(chunk)); response.on("error", reject);
        response.on("end", () => {
          for (const cookie of response.headers["set-cookie"] || []) jar.setCookieSync(cookie, target.href);
          resolve(new Response(Buffer.concat(chunks), { status: response.statusCode, headers: Object.fromEntries(Object.entries(response.headers).map(([key, value]) => [key, Array.isArray(value) ? value.join(",") : value])) }));
        });
      });
      request.on("error", reject); if (body) request.write(body); request.end();
    });
    };
    const ready = async () => {
      for (let i = 0; i < 150; i++) {
        if (startupError || process.exitCode !== null) throw startupError || new Error(await readFile(join(directory, "server.log"), "utf8"));
        try { if ((await fetcher("/health")).ok) return; } catch { /* starting */ }
        await delay(40);
      }
      throw new Error("Backend did not start");
    };
    const stopProcess = async () => { if (!process || process.exitCode !== null || process.signalCode !== null) return; process.kill("SIGTERM"); await new Promise(resolve => process.once("exit", resolve)); };
    const restart = async (options = {}) => { await stopProcess(); recovery = options.recovery ?? recovery; disableTls = options.disableTls ?? disableTls; startupError = null; launch(); await ready(); };
    launch(); await ready();
    const sql = async (query, values = []) => JSON.parse(await run("python3", ["-c", `import sqlite3,sys,json
c=sqlite3.connect(sys.argv[1]);c.row_factory=sqlite3.Row
try:
 r=c.execute(sys.argv[2],json.loads(sys.argv[3]));rows=[dict(x) for x in r] if r.description else [];c.commit();print(json.dumps(rows))
finally:c.close()`, join(directory,"feedarr.db"), query, JSON.stringify(values)]));
    const locked = async () => (await run("python3", ["-c", `import sqlite3,sys
c=sqlite3.connect(sys.argv[1],timeout=0)
try:c.execute('BEGIN IMMEDIATE');print('no');c.rollback()
except sqlite3.OperationalError:print('yes')
finally:c.close()`, join(directory,"feedarr.db")])).trim() === "yes";
    const crash = async () => { if (process.exitCode === null && process.signalCode === null) { process.kill("SIGKILL"); await new Promise(resolve => process.once("exit", resolve)); } };
    return { origin, secureOrigin, fetcher, restart, jar, sql, locked, crash, directory,
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
    await f.click("Restaurer une sauvegarde"); await f.waitFor(() => document.body.textContent.includes("Analyser"));
    assert.equal((await server.fetcher("/api/configuration-backups/state")).status, 404);
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

const backupPassword = "Temporary backup password 2026!";
const authorized = async (server, options = {}) => ({ "X-Feedarr-CSRF": (await state(server, options)).csrfToken });
const upload = (bytes, password = backupPassword, preview = null) => {
  const body = new NativeFormData(); body.append("file", new Blob([bytes]), "configuration.feedarr"); body.append("password", password);
  if (preview) { body.append("expectedState", preview.expectedState); body.append("confirm", "true"); } return body;
};
const previewBackup = async (server, bytes, options = {}) => {
  const response = await server.fetcher("/api/configuration-backups/preview", { method: "POST", headers: await authorized(server, options), body: upload(bytes) }, options);
  assert.equal(response.status, 200, await response.clone().text()); return response.json();
};
const restoreBackup = async (server, bytes, preview, options = {}) => server.fetcher("/api/configuration-backups/restore", { method: "POST", headers: await authorized(server, options), body: upload(bytes, backupPassword, preview) }, options);
async function seedConfiguration(server) {
  await server.sql("INSERT INTO providers(id,type,name,base_url,api_key_encrypted,enabled,created_at_ts,updated_at_ts) VALUES(98,'jackett','origin','https://indexer.example','provider-fixture-secret',1,1,1)");
  await server.sql("INSERT INTO sources(id,name,torznab_url,api_key,auth_mode,provider_id,created_at_ts,updated_at_ts) VALUES(135,'origin','https://indexer.example/api','source-fixture-secret','query',98,1,1)");
  await server.sql("INSERT INTO arr_applications(id,type,base_url,api_key_encrypted,is_default,created_at_ts,updated_at_ts) VALUES(89,'sonarr','https://sonarr.example','app-fixture-secret',1,1,1)");
  await server.sql("INSERT INTO source_categories(source_id,cat_id,name) VALUES(135,2000,'Films')");
  await server.sql("INSERT INTO source_selected_categories VALUES(135,2000)");
  await server.sql("INSERT INTO source_category_mappings(source_id,cat_id,group_key,group_label,created_at_ts,updated_at_ts) VALUES(135,2000,'films','Films',1,1)");
  await server.sql("INSERT OR REPLACE INTO app_settings VALUES('general',?,1)", [JSON.stringify({ autoSyncEnabled: false, arrAutoSyncEnabled: false, syncIntervalMinutes: 127 })]);
  await server.sql("INSERT OR REPLACE INTO app_settings VALUES('arr',?,1)", [JSON.stringify({ arrAutoSyncEnabled: false })]);
  await server.sql("INSERT OR REPLACE INTO app_settings VALUES('maintenance',?,1)", [JSON.stringify({ posterWorkers: 2 })]);
  await server.sql("INSERT INTO external_provider_instances(instance_id,provider_key,display_name,enabled,base_url,auth_json,options_json,created_at_ts,updated_at_ts) VALUES('fixture-tmdb','tmdb','fixture',1,'https://api.themoviedb.org','{\"apiKey\":\"metadata-fixture-secret\"}','{}',1,9999999999)");
}
async function exportBackup(server, options = {}) {
  const response = await server.fetcher("/api/configuration-backups/export", { method: "POST", headers: await authorized(server, options), body: JSON.stringify({ password: backupPassword, confirmation: backupPassword }) }, options);
  assert.equal(response.status, 200, await response.clone().text()); assert.match(response.headers.get("content-disposition"), /\.feedarr/);
  return Buffer.from(await response.arrayBuffer());
}
function decryptBackup(bytes) {
  const header = Buffer.from("FEEDARR-CONFIG-3\nPBKDF2-SHA256:600000\nAES-256-GCM\n"), prefix = header.length + 28;
  assert.ok(bytes.subarray(0, header.length).equals(header));
  const key = pbkdf2Sync(backupPassword, bytes.subarray(header.length, header.length + 16), 600000, 32, "sha256");
  const decipher = createDecipheriv("aes-256-gcm", key, bytes.subarray(header.length + 16, prefix));
  decipher.setAAD(bytes.subarray(0, prefix)); decipher.setAuthTag(bytes.subarray(-16));
  try { return JSON.parse(Buffer.concat([decipher.update(bytes.subarray(prefix, -16)), decipher.final()]).toString()); }
  finally { key.fill(0); }
}
const configurationSnapshot = async server => JSON.stringify(await Promise.all(["providers", "sources", "arr_applications", "external_provider_instances", "app_settings", "source_selected_categories", "source_category_mappings"].map(table => server.sql(`SELECT * FROM ${table} ORDER BY rowid`))));

test("backup UI downloads encrypted config, analyses and restores between real independent instances", async () => {
  const origin = await backend(), target = await backend({ tls: true }); let f;
  try {
    assert.equal((await create(origin)).status, 200); await seedConfiguration(origin);
    f = await fixture(origin.fetcher, "/settings/backup", origin.origin);
    await f.waitFor(() => document.getElementById("backup-password"));
    let downloaded;
    window.URL.createObjectURL = blob => { downloaded = blob; return "blob:fixture"; }; window.URL.revokeObjectURL = () => {};
    window.HTMLAnchorElement.prototype.click = () => {};
    await f.change("backup-password", backupPassword); await f.change("backup-confirmation", backupPassword);
    await f.click("Télécharger la sauvegarde"); await f.waitFor(() => !!downloaded);
    const bytes = Buffer.from(await downloaded.arrayBuffer()); await f.close(); f = null;
    const model = decryptBackup(bytes); assert.equal(model.schemaVersion, 3); assert.equal(model.settings.security, undefined);
    assert.equal(model.settings.ui.onboardingDone, undefined); assert.equal(model.sources[0].api_key, "source-fixture-secret");
    assert.equal((await target.fetcher("/api/auth/create-admin", { method: "POST", body: JSON.stringify({ ...credentials, username: "target-owner" }) }, { https: true })).status, 200);
    assert.equal((await policy(target, true, { https: true })).status, 200);
    assert.equal((await target.fetcher("/api/auth/login", { method: "POST", body: JSON.stringify({ ...credentials, username: "target-owner" }) }, { https: true })).status, 200);
    // Use the real API to protect target secrets before taking the preservation snapshot.
    assert.equal((await target.fetcher("/api/providers", { method: "POST", headers: await authorized(target, { https: true }), body: JSON.stringify({ type: "jackett", name: "history", baseUrl: "https://history.example", apiKey: "target-secret" }) }, { https: true })).status, 200);
    await target.sql("INSERT INTO sources(id,name,torznab_url,api_key,created_at_ts,updated_at_ts) SELECT 222,'history','https://history.example/api',api_key_encrypted,1,1 FROM providers LIMIT 1");
    await target.sql("INSERT INTO media_entities(id,unified_category,title_clean,created_at_ts,updated_at_ts,poster_file) VALUES(7,'Film','kept',1,1,'keep.jpg')");
    await target.sql("INSERT INTO releases(source_id,guid,title,created_at_ts,entity_id,poster_file) VALUES(222,'kept','kept',1,7,'keep.jpg')");
    await target.sql("INSERT INTO activity_log(level,event_type,message,created_at_ts) VALUES('info','sync','history sentinel',1)");
    await target.sql("INSERT INTO activity_log(level,event_type,message,created_at_ts) VALUES('error','system','log sentinel',1)");
    const { mkdir, writeFile } = await import("node:fs/promises"); await mkdir(join(target.directory, "posters"), { recursive: true }); await writeFile(join(target.directory, "posters/keep.jpg"), "poster-sentinel");
    const preserved = async () => JSON.stringify(await Promise.all(["releases", "media_entities", "poster_store_refs", "poster_matches"].map(table => target.sql(`SELECT * FROM ${table} ORDER BY rowid`))));
    const runtime = await preserved(); const activity = await target.sql("SELECT * FROM activity_log WHERE message IN ('history sentinel','log sentinel') ORDER BY id");
    const security = await target.sql("SELECT * FROM app_settings WHERE key='security'");
    f = await fixture((url, init) => target.fetcher(url, init, { https: true }), "/settings/backup", target.secureOrigin);
    await f.waitFor(() => document.getElementById("restore-file"));
    const input = document.getElementById("restore-file"); input.required = false; // jsdom cannot populate native FileList/value.
    Object.defineProperty(input, "files", { configurable: true, value: [new window.File([bytes], "configuration.feedarr")] });
    await act(async () => input.dispatchEvent(new window.Event("change", { bubbles: true })));
    await f.change("restore-password", backupPassword); await f.click("Analyser");
    await f.waitFor(() => document.body.textContent.includes("Confirmer la restauration"));
    assert.equal(await preserved(), runtime); assert.equal((await target.sql("SELECT COUNT(*) AS n FROM sources"))[0].n, 1);
    await f.click("Confirmer la restauration"); await f.waitFor(() => document.body.textContent.includes("Configuration restaurée."));
    assert.match(document.body.textContent, /Redémarrez Feedarr/); await f.close(); f = null;
    assert.equal(await preserved(), runtime); assert.deepEqual(await target.sql("SELECT * FROM app_settings WHERE key='security'"), security);
    assert.deepEqual(await target.sql("SELECT * FROM activity_log WHERE message IN ('history sentinel','log sentinel') ORDER BY id"), activity);
    assert.equal(await readFile(join(target.directory, "posters/keep.jpg"), "utf8"), "poster-sentinel");
    assert.equal((await target.fetcher("/api/sources")).status, 403);
    await target.restart(); assert.equal((await state(target, { https: true })).authenticated, true);
    const again = decryptBackup(await exportBackup(target, { https: true }));
    assert.equal(again.settings.general.syncIntervalMinutes, 127); assert.equal(again.settings.maintenance.posterWorkers, 2);
    assert.equal(again.sources.find(x => x.name === "origin").api_key, "source-fixture-secret");
    assert.equal(again.providers.find(x => x.name === "origin").api_key, "provider-fixture-secret"); assert.equal(again.applications[0].api_key_encrypted, "app-fixture-secret");
    assert.equal(again.externalProviders.find(x => x.display_name === "fixture").auth_json.apiKey, "metadata-fixture-secret");
    assert.equal(again.selections.length, 1); assert.equal(again.mappings.length, 1);
    for (const server of [origin, target]) { const logs = await server.logs(); for (const secret of [backupPassword, "source-fixture-secret", "provider-fixture-secret", "metadata-fixture-secret"]) assert.ok(!logs.includes(secret)); }
  } finally { if (f) await f.close(); await origin.stop(); await target.stop(); }
});

test("real API rejects invalid archives, wrong passwords, anonymous and CSRF-free mutations without writes", async () => {
  const server = await backend();
  try {
    await create(server); const bytes = await exportBackup(server); const before = await configurationSnapshot(server);
    for (const body of [upload(bytes, "Incorrect password 2026!"), upload(Buffer.from("PK old zip")), upload(Buffer.concat([bytes.subarray(0, -1), Buffer.from([bytes.at(-1) ^ 1])]))]) {
      const response = await server.fetcher("/api/configuration-backups/preview", { method: "POST", body, headers: await authorized(server) }); assert.equal(response.status, 422);
    }
    assert.equal((await server.fetcher("/api/configuration-backups/preview", { method: "POST", body: upload(bytes) })).status, 403);
    assert.equal((await server.fetcher("/api/configuration-backups/export", { method: "POST", body: "{}" }, { anonymous: true })).status, 401);
    for (const path of ["/api/system/backups", "/api/setup/state", "/api/configuration-backups/state"]) assert.equal((await server.fetcher(path)).status, 404);
    assert.equal(await configurationSnapshot(server), before);
  } finally { await server.stop(); }
});

test("real SQL failure and SIGKILL inside restore roll back on restart, then a fresh restore succeeds", async () => {
  const origin = await backend(), target = await backend();
  try {
    await create(origin); await seedConfiguration(origin); const bytes = await exportBackup(origin); await create(target);
    const before = await configurationSnapshot(target); let preview = await previewBackup(target, bytes);
    await target.sql("CREATE TRIGGER fail_restore BEFORE INSERT ON sources BEGIN SELECT RAISE(ABORT,'injected SQL failure'); END");
    assert.equal((await restoreBackup(target, bytes, preview)).status, 500); assert.equal(await configurationSnapshot(target), before);
    await target.sql("DROP TRIGGER fail_restore");
    await target.sql("CREATE TRIGGER slow_restore BEFORE INSERT ON sources BEGIN SELECT SUM(n) FROM (WITH RECURSIVE seq(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM seq WHERE n<100000000) SELECT n FROM seq); END");
    preview = await previewBackup(target, bytes);
    const pending = restoreBackup(target, bytes, preview).then(response => ({ response }), error => ({ error }));
    const deadline = Date.now() + 5000; let locked = false;
    while (Date.now() < deadline && !(locked = await target.locked())) await delay(20);
    assert.ok(locked, "restore must hold a SQLite write transaction before SIGKILL"); await delay(150); assert.ok(await target.locked());
    await target.crash(); const interrupted = await pending; assert.ok(interrupted.error, "request interrupted by process kill");
    await target.restart(); assert.equal((await state(target)).authenticated, true); assert.equal(await configurationSnapshot(target), before);
    await target.sql("DROP TRIGGER slow_restore"); preview = await previewBackup(target, bytes);
    assert.equal((await restoreBackup(target, bytes, preview)).status, 200);
    assert.equal((await target.sql("SELECT COUNT(*) AS n FROM sources WHERE enabled=1"))[0].n, 1);
  } finally { await origin.stop(); await target.stop(); }
});
