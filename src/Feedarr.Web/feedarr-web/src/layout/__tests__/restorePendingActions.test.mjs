import test from "node:test";
import assert from "node:assert/strict";
import { cancelPreparedRestore, restoreStatePath } from "../restorePendingActions.js";

test("restore cancellation requires a pending identity and explicit confirmation", async () => {
  let calls = 0;
  const actions = { confirm: () => false, cancel: () => { calls++; }, refresh: () => { calls++; } };
  assert.equal(await cancelPreparedRestore(null, actions), false);
  assert.equal(await cancelPreparedRestore({ pending: false, id: "id" }, actions), false);
  assert.equal(await cancelPreparedRestore({ pending: true }, actions), false);
  assert.equal(await cancelPreparedRestore({ pending: true, id: "id" }, actions), false);
  assert.equal(calls, 0);
});

test("confirmed cancellation targets the displayed operation then refreshes without page reload", async () => {
  const calls = [];
  const ok = await cancelPreparedRestore({ pending: true, id: "operation id" }, {
    confirm: () => true,
    cancel: async path => calls.push(path),
    refresh: async () => calls.push("refresh"),
  });
  assert.equal(ok, true);
  assert.deepEqual(calls, ["/api/system/backups/pending-restore?id=operation%20id", "refresh"]);
  assert.equal(restoreStatePath, "/api/system/backups/restore-state");
});

test("failed cancellation does not falsely refresh to a cancelled state", async () => {
  let refreshed = false;
  await assert.rejects(cancelPreparedRestore({ pending: true, id: "id" }, {
    confirm: () => true,
    cancel: async () => { throw new Error("conflict"); },
    refresh: async () => { refreshed = true; },
  }), /conflict/);
  assert.equal(refreshed, false);
});
