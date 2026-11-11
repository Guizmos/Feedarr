import test from "node:test";
import assert from "node:assert/strict";
import { build } from "esbuild";
import { fileURLToPath } from "node:url";

// Exercise the actual hook with a small hook scheduler and controllable HTTP promises.
const bundle = await build({
  entryPoints: [fileURLToPath(new URL("../useMaintenanceActions.js", import.meta.url))], bundle: true,
  platform: "node", format: "esm", write: false,
  plugins: [{ name: "controlled-hooks-http", setup(builder) {
    builder.onResolve({ filter: /^react$/ }, () => ({ path: "react", namespace: "test" }));
    builder.onResolve({ filter: /api\/client\.js$/ }, () => ({ path: "api", namespace: "test" }));
    builder.onLoad({ filter: /.*/, namespace: "test" }, args => ({ contents: args.path === "react" ? `
      export const useCallback = fn => fn;
      export const useEffect = () => {};
      export function useState(initial) {
        const store = globalThis.__backupHookStore; const index = store.cursor++;
        if (!(index in store.values)) store.values[index] = initial;
        return [store.values[index], value => { store.values[index] = typeof value === "function" ? value(store.values[index]) : value; }];
      }
      export function useRef(initial) {
        const store = globalThis.__backupHookStore; const index = store.cursor++;
        if (!(index in store.values)) store.values[index] = { current: initial };
        return store.values[index];
      }
    ` : `
      export const apiGet = (...args) => globalThis.__backupApi.get(...args);
      export const apiPost = (...args) => globalThis.__backupApi.post(...args);
      export const apiDelete = (...args) => globalThis.__backupApi.delete(...args);
      export const resolveApiUrl = path => path;
    ` }));
  } }],
});
const { default: useMaintenanceActions } = await import(`data:text/javascript;base64,${Buffer.from(bundle.outputFiles[0].text).toString("base64")}`);
const render = () => { globalThis.__backupHookStore.cursor = 0; return useMaintenanceActions(); };
const tick = () => new Promise(resolve => setImmediate(resolve));

test("backup creation blocks immediate double click and close; success releases loading and closes", async () => {
  const oldStore = globalThis.__backupHookStore, oldApi = globalThis.__backupApi;
  globalThis.__backupHookStore = { values: [], cursor: 0 };
  let finish, calls = 0;
  globalThis.__backupApi = { get: async () => [], post: () => { calls++; return new Promise(resolve => { finish = resolve; }); } };
  try {
    render().openBackupCreate();
    const hook = render();
    const first = hook.handleBackupCreate();
    await hook.handleBackupCreate();
    hook.closeBackupCreate();
    assert.equal(calls, 1);
    assert.equal(render().backupCreateOpen, true);
    assert.equal(render().backupCreateLoading, true);
    finish({ name: "created.zip" }); await first;
    assert.equal(render().backupCreateOpen, false);
    assert.equal(render().backupCreateLoading, false);
  } finally { globalThis.__backupHookStore = oldStore; globalThis.__backupApi = oldApi; }
});

test("restore submission blocks duplicate and closing, then exposes error and releases loading", async () => {
  const oldStore = globalThis.__backupHookStore, oldApi = globalThis.__backupApi;
  globalThis.__backupHookStore = { values: [], cursor: 0 };
  let rejectRequest, submissions = 0;
  globalThis.__backupApi = {
    get: async () => ({}), post: path => {
      if (!path.includes("confirm=true")) return Promise.resolve({ dryRun: true });
      submissions++; return new Promise((_, reject) => { rejectRequest = reject; });
    },
  };
  try {
    render().openBackupRestore({ name: "backup.zip" }); await tick();
    const hook = render();
    const first = hook.handleBackupRestore(); await tick();
    await hook.handleBackupRestore(); hook.closeBackupRestore();
    assert.equal(submissions, 1);
    assert.equal(render().backupRestoreOpen, true);
    rejectRequest(new DOMException("Suivi expiré", "TimeoutError")); await first;
    assert.equal(render().backupRestoreLoading, false);
    assert.equal(render().backupRestoreOpen, true);
    assert.match(render().backupError, /Suivi expiré/);
  } finally { globalThis.__backupHookStore = oldStore; globalThis.__backupApi = oldApi; }
});

test("restore modal closes only after server confirms the matching persistent manifest", async () => {
  const oldStore = globalThis.__backupHookStore, oldApi = globalThis.__backupApi, oldWindow = globalThis.window;
  globalThis.__backupHookStore = { values: [], cursor: 0 };
  const id = "a".repeat(32);
  let accepted = false;
  globalThis.window = { dispatchEvent: () => {} };
  globalThis.__backupApi = {
    get: async path => path.endsWith("/state") ? (accepted ? {
      preparation: { id, state: "completed", result: {} }, restorePending: { id, pending: true, state: "pending" },
    } : {}) : [],
    post: async path => {
      if (!path.includes("confirm=true")) return { dryRun: true };
      accepted = true; return { accepted: true, preparationId: id };
    },
  };
  try {
    render().openBackupRestore({ name: "backup.zip" }); await tick();
    await render().handleBackupRestore();
    assert.equal(render().backupRestoreOpen, false);
    assert.equal(render().backupRestoreLoading, false);
    assert.match(render().backupNotice, /Restauration préparée/);
    assert.equal(render().backupError, "");
  } finally { globalThis.__backupHookStore = oldStore; globalThis.__backupApi = oldApi; globalThis.window = oldWindow; }
});

test("creation timeout keeps modal open, displays error and permits a later retry", async () => {
  const oldStore = globalThis.__backupHookStore, oldApi = globalThis.__backupApi;
  globalThis.__backupHookStore = { values: [], cursor: 0 };
  globalThis.__backupApi = { get: async () => [], post: async () => { throw new DOMException("Délai dépassé", "TimeoutError"); } };
  try {
    render().openBackupCreate(); await render().handleBackupCreate();
    assert.equal(render().backupCreateOpen, true);
    assert.equal(render().backupCreateLoading, false);
    assert.match(render().backupError, /Délai dépassé/);
    globalThis.__backupApi.post = async () => ({ name: "retry.zip" });
    await render().handleBackupCreate();
    assert.equal(render().backupCreateOpen, false);
  } finally { globalThis.__backupHookStore = oldStore; globalThis.__backupApi = oldApi; }
});

test("archive deletion requires explicit confirmation, blocks duplicate submission and refreshes after success", async () => {
  const oldStore = globalThis.__backupHookStore, oldApi = globalThis.__backupApi;
  globalThis.__backupHookStore = { values: [], cursor: 0 };
  let finish, calls = 0, refreshes = 0;
  globalThis.__backupApi = {
    get: async path => { if (path.endsWith("/backups")) refreshes++; return path.endsWith("/backups") ? [] : {}; },
    delete: path => { assert.equal(path, "/api/system/backups/local%20archive.zip"); calls++; return new Promise(resolve => { finish = resolve; }); },
  };
  try {
    render().openBackupDelete({ name: "local archive.zip" });
    assert.equal(calls, 0);
    assert.equal(render().backupDeleteTarget.name, "local archive.zip");
    const hook = render(), first = hook.handleBackupDelete();
    await hook.handleBackupDelete(); hook.closeBackupDelete();
    assert.equal(calls, 1); assert.equal(render().backupDeleteOpen, true); assert.equal(render().backupDeleteLoading, true);
    finish(); await first;
    assert.equal(refreshes, 1); assert.equal(render().backupDeleteOpen, false);
    assert.equal(render().backupDeleteLoading, false); assert.deepEqual(render().backups, []);
    assert.match(render().backupNotice, /supprimée/);
  } finally { globalThis.__backupHookStore = oldStore; globalThis.__backupApi = oldApi; }
});

test("archive deletion failure keeps confirmation and archive, exposes error and allows retry", async () => {
  const oldStore = globalThis.__backupHookStore, oldApi = globalThis.__backupApi;
  globalThis.__backupHookStore = { values: [], cursor: 0 };
  let calls = 0;
  globalThis.__backupApi = { get: async () => [{ name: "local.zip" }], delete: async () => { calls++; throw new Error("Suppression refusée"); } };
  try {
    await render().loadBackups(); render().openBackupDelete({ name: "local.zip" });
    await render().handleBackupDelete();
    assert.equal(render().backupDeleteOpen, true); assert.equal(render().backupDeleteLoading, false);
    assert.equal(render().backups.length, 1); assert.match(render().backupError, /Suppression refusée/);
    await render().handleBackupDelete(); assert.equal(calls, 2);
  } finally { globalThis.__backupHookStore = oldStore; globalThis.__backupApi = oldApi; }
});

test("archive list read failure preserves displayed archives and never requests deletion", async () => {
  const oldStore = globalThis.__backupHookStore, oldApi = globalThis.__backupApi;
  globalThis.__backupHookStore = { values: [], cursor: 0 };
  let fail = false, deletes = 0;
  globalThis.__backupApi = {
    get: async () => { if (fail) throw new Error("Lecture impossible"); return [{ name: "local.zip" }]; },
    delete: async () => { deletes++; },
  };
  try {
    await render().loadBackups(); fail = true; await render().loadBackups();
    assert.deepEqual(render().backups, [{ name: "local.zip" }]);
    assert.match(render().backupError, /Lecture impossible/); assert.equal(deletes, 0);
  } finally { globalThis.__backupHookStore = oldStore; globalThis.__backupApi = oldApi; }
});
