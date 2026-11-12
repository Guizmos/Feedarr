import { test } from "node:test";
import assert from "node:assert/strict";
import { shadowIdentity, shadowCounts } from "../matchingShadowModel.js";

test("TMDB identities always include explicit type and numeric ID", () => {
  assert.equal(shadowIdentity({ provider: "tmdb", type: "movie", id: "123" }), "tmdb:movie:123");
  assert.equal(shadowIdentity({ provider: "tmdb", type: "tv", id: "123" }), "tmdb:tv:123");
  for (const identity of [null, { provider: "tmdb", id: "123" }, { provider: "tmdb", type: "movie", id: "https://secret" }]) assert.equal(shadowIdentity(identity), "—");
});
test("counts distinguish not_evaluable from provider rejection", () => {
  assert.equal(shadowCounts({ accept: 2, ambiguous: 1, reject: 0, notEvaluable: 4 }), "accept: 2 · ambiguous: 1 · reject: 0 · not_evaluable: 4");
});

test("entry renders untrusted titles as escaped text and preserves hypothesis evidence", async () => {
  const { buildSync } = await import("esbuild");
  const { createRequire } = await import("node:module");
  const require = createRequire(import.meta.url);
  const React = require("react");
  const { renderToStaticMarkup } = require("react-dom/server");
  const compiled = buildSync({ entryPoints: [new URL("../MatchingShadowEntry.jsx", import.meta.url).pathname], bundle: true,
    platform: "node", format: "cjs", external: ["react"], write: false });
  const module = { exports: {} };
  new Function("require", "module", "exports", compiled.outputFiles[0].text)(require, module, module.exports);
  const html = renderToStaticMarkup(React.createElement(module.exports.default, { entry: {
    releaseId: 1, rawTitle: '<img src=x onerror="alert(1)">', historicalDecision: "Accept",
    historicalIdentity: { provider: "tmdb", type: "movie", id: "123" }, historicalScore: 1,
    selectedIdentity: null, decision: "ambiguous", divergence: true, durationMs: 1, preserveExisting: false,
    reasons: ["close-competing-identities"], queries: [{ title: "Blade Runner", year: 2049, yearEvidence: "Hypothesis" }],
    candidates: [{ title: "<script>alert(1)</script>", year: 1982, identity: { provider: "tmdb", type: "tv", id: "123" },
      decision: "reject", confidence: 0.1, reasons: [], contradictions: ["media-type-mismatch"] }],
  } }));
  assert.ok(!html.includes("<img"));
  assert.ok(!html.includes("<script>"));
  assert.ok(html.includes("&lt;img"));
  assert.ok(html.includes("tmdb:movie:123"));
  assert.ok(html.includes("tmdb:tv:123"));
  assert.ok(html.includes("Hypothesis"));
  assert.ok(html.includes("media-type-mismatch"));
});
