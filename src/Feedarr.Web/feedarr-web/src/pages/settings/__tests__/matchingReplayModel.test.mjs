import test from "node:test";
import assert from "node:assert/strict";
import { replayPayload, replayDifference } from "../matchingReplayModel.js";

test("replay requests are bounded and use an explicit mode", () => {
  assert.deepEqual(replayPayload(10, "offline", 30, 0), { count: 10, mode: "offline", budget: 30, generation: 0 });
  assert.deepEqual(replayPayload(200, "tmdb", 1, 7), { count: 200, mode: "tmdb", budget: 1, generation: 7 });
  for (const [count, mode, budget, generation] of [[201, "tmdb", 30, 0], [10, "tmdb", 31, 0], [10, "tmdb", 0, 0], [10, "unknown", 30, 0], [10, "offline", 30, -1]])
    assert.throws(() => replayPayload(count, mode, budget, generation), /replay_request_invalid/);
});

test("unknown historical decision is distinct from agreement", () => {
  assert.equal(replayDifference(null), "inconnue");
  assert.equal(replayDifference(undefined), "inconnue");
  assert.equal(replayDifference(false), "non");
  assert.equal(replayDifference(true), "oui");
});

test("replay panel renders collapsed and offline with stop reset and export controls", async () => {
  const { buildSync } = await import("esbuild");
  const { createRequire } = await import("node:module");
  const require = createRequire(import.meta.url);
  const React = require("react");
  const { renderToStaticMarkup } = require("react-dom/server");
  const compiled = buildSync({ entryPoints: [new URL("../MatchingReplayPanel.jsx", import.meta.url).pathname], bundle: true,
    platform: "node", format: "cjs", external: ["react"], define: { "import.meta.env": "{}" }, write: false });
  const module = { exports: {} };
  new Function("require", "module", "exports", compiled.outputFiles[0].text)(require, module, module.exports);
  const html = renderToStaticMarkup(React.createElement(module.exports.default));
  assert.ok(html.startsWith("<details>"));
  assert.ok(html.includes('value="offline" selected=""'));
  assert.ok(html.includes("Rejouer le matching"));
  assert.ok(html.includes("Arrêter le replay"));
  assert.ok(html.includes("Effacer la session"));
  assert.ok(html.includes("Exporter JSON"));
  assert.ok(html.includes("aucun appel provider"));
});
