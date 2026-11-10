import test from "node:test";
import assert from "node:assert/strict";
import { prepareRestore } from "../restorePreparation.js";
const id = "a".repeat(32);
const other = "b".repeat(32);
const pending = { pending: true, state: "pending", id, missingPosters: 2 };
const job = (state, extra = {}) => ({ preparation: { id, state, ...extra } });
function fixture(states, extra = {}) {
  let ticks = 0;
  const calls = [];
  return { calls, options: {
    get: async path => { calls.push(["get", path]); const next = states.shift(); if (next instanceof Error) throw next; return next; },
    post: async path => { calls.push(["post", path]); return { accepted: true, preparationId: id }; },
    confirmReplacement: () => true, sleep: async ms => { ticks += ms; }, now: () => ticks, ...extra,
  } };
}
test("long preparation is polled and only a matching persisted manifest confirms success", async () => {
  const f = fixture([{}, job("running"), job("running"), { ...job("completed", { result: { clearedUndecryptableCredentials: 1 } }), restorePending: pending }]);
  const result = await prepareRestore("backup.zip", f.options);
  assert.equal(result.preparationId, id);
  assert.equal(result.needsRestart, true);
  assert.equal(result.missingPosters, 2);
  assert.equal(result.clearedUndecryptableCredentials, 1);
  assert.equal(f.calls.filter(x => x[0] === "post").length, 1);
});
test("a completed React-independent operation without its manifest is never success", async () => {
  const f = fixture([{}, job("completed")]);
  await assert.rejects(prepareRestore("backup.zip", f.options), /manifeste/);
});
test("an unrelated previous pending manifest cannot confirm the new preparation", async () => {
  const f = fixture([{}, { ...job("completed"), restorePending: { ...pending, id: other } }]);
  await assert.rejects(prepareRestore("backup.zip", f.options), /manifeste/);
});
test("server errors, tracking loss and timeouts always settle with explicit errors", async () => {
  const cases = [
    [[{}, job("failed", { error: "Disque indisponible" })], {}, /Disque indisponible/],
    [[{}, new Error(), new Error(), new Error()], {}, /Suivi de préparation indisponible/],
    [[{}, job("running"), job("running")], { timeoutMs: 3000 }, /Délai de suivi dépassé/],
    [[{}, {}], {}, /interrompue ou remplacée/],
  ];
  for (const [states, options, message] of cases) {
    await assert.rejects(prepareRestore("backup.zip", fixture(states, options).options), message);
  }
});
test("temporary polling error recovers and durable pending survives loss of in-memory job", async () => {
  const f = fixture([{}, new Error(), { restorePending: pending }]);
  assert.equal((await prepareRestore("backup.zip", f.options)).preparationId, id);
});
test("preflight rereads server state and requires explicit current pending replacement", async () => {
  const f = fixture([{ restorePending: { ...pending, id: other } }, { ...job("completed"), restorePending: pending }]);
  await prepareRestore("backup.zip", f.options);
  assert.match(f.calls.find(x => x[0] === "post")[1], new RegExp(`replacePendingId=${other}`));
  const refused = fixture([{ restorePending: pending }], { confirmReplacement: () => false });
  assert.equal(await prepareRestore("backup.zip", refused.options), null);
  assert.equal(refused.calls.length, 1);
});
test("busy and unreadable preflight states reject before any POST", async () => {
  for (const before of [{ isBusy: true }, { restorePending: { pending: true, state: "failed" } }]) {
    const f = fixture([before]);
    await assert.rejects(prepareRestore("backup.zip", f.options));
    assert.equal(f.calls.length, 1);
  }
});
test("missing or invalid acceptance is an error rather than a restore success", async () => {
  const f = fixture([{}], { post: async () => ({ ok: true, needsRestart: true }) });
  await assert.rejects(prepareRestore("backup.zip", f.options), /prise en charge/);
});
