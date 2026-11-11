import test from "node:test";
import assert from "node:assert/strict";
import { build } from "esbuild";
import { fileURLToPath } from "node:url";

const result = await build({
  entryPoints: [fileURLToPath(new URL("../HistoryRetentionMaintenance.jsx", import.meta.url))],
  bundle: true, platform: "node", format: "esm", write: false,
  plugins: [{ name: "history-maintenance-harness", setup(builder) {
    builder.onResolve({ filter: /^react$/ }, () => ({ path: "react", namespace: "test" }));
    builder.onResolve({ filter: /api\/client\.js$/ }, () => ({ path: "api", namespace: "test" }));
    builder.onLoad({ filter: /.*/, namespace: "test" }, args => ({ contents: args.path === "api"
      ? "export const apiGet = (...args) => globalThis.__historyUi.get(...args); export const apiPost = (...args) => globalThis.__historyUi.post(...args);"
      : `const React={Fragment:'fragment',createElement:(type,props,...children)=>({type,props:props||{},children})};export default React;
         export function useState(initial){const s=globalThis.__historyUi,i=s.index++;if(!(i in s.values))s.values[i]=initial;return[s.values[i],v=>{s.values[i]=v;}];}
         export function useRef(initial){const s=globalThis.__historyUi,i=s.index++;if(!(i in s.values))s.values[i]={current:initial};return s.values[i];}` }));
  } }],
});
const { default: HistoryRetentionMaintenance } = await import(`data:text/javascript;base64,${Buffer.from(result.outputFiles[0].text).toString("base64")}`);
const render = () => { globalThis.__historyUi.index = 0; return HistoryRetentionMaintenance({}); };
function nodes(node) {
  if (!node || typeof node !== "object") return [];
  if (Array.isArray(node)) return node.flatMap(nodes);
  return [node, ...node.children.flatMap(nodes)];
}
const button = (tree, label) => nodes(tree).find(node => node.type === "button" && node.children.flat(Infinity).includes(label));
const report = { dryRun: true, entityCandidates: 1, entityBatchCandidates: 1, protectedEntities: 3, posterCandidates: 1,
  postersScanned: 10, estimatedRecoverableBytes: 4, protectedPosters: 9, protectionReasons: { manual: 3 }, errors: 0,
  missingStoreDirectories: 2, missingStoreReferenceRows: 5, missingPosterFiles: 1, windowAfterPoster: null, nextPoster: "cursor-1" };

test("maintenance starts with no request and the first action is GET-only simulation", async () => {
  const previous = globalThis.__historyUi;
  const calls = [];
  globalThis.__historyUi = { values: [], index: 0, get: async (...args) => { calls.push(args); return report; }, post: () => assert.fail("No POST in dry-run") };
  try {
    const tree = render();
    assert.equal(calls.length, 0);
    assert.equal(button(tree, "Analyser sans supprimer").props.type, "button");
    await button(tree, "Analyser sans supprimer").props.onClick();
    assert.equal(calls.length, 1);
    assert.match(calls[0][0], /^\/api\/maintenance\/history-cleanup\?/);
    assert.equal(button(render(), "Confirmer le nettoyage du lot").props.disabled, true);
  } finally { globalThis.__historyUi = previous; }
});

test("real cleanup needs checkbox plus confirmation and reuses the analyzed window, with no duplicate submission", async () => {
  const previous = globalThis.__historyUi, previousWindow = globalThis.window;
  const writes = []; let finish;
  globalThis.__historyUi = { values: [], index: 0,
    get: async path => path.includes("afterPoster") ? { ...report, windowAfterPoster: "cursor-1", nextPoster: null } : report,
    post: (path, body) => { writes.push([path, body]); return new Promise(resolve => { finish = resolve; }); } };
  globalThis.window = { confirm: () => false };
  try {
    await button(render(), "Analyser sans supprimer").props.onClick();
    await button(render(), "Analyser la fenêtre suivante").props.onClick();
    nodes(render()).find(node => node.type === "input").props.onChange({ target: { checked: true } });
    await button(render(), "Confirmer le nettoyage du lot").props.onClick();
    assert.equal(writes.length, 0);
    globalThis.window.confirm = () => true;
    const action = button(render(), "Confirmer le nettoyage du lot");
    const first = action.props.onClick();
    await action.props.onClick();
    assert.equal(writes.length, 1);
    assert.equal(writes[0][1].dryRun, false);
    assert.equal(writes[0][1].limit, 100);
    assert.equal(writes[0][1].afterPoster, "cursor-1");
    assert.equal(writes[0][1].confirmation, "RECLAIM_UNUSED_HISTORY");
    finish({ ...report, dryRun: false }); await first;
    assert.equal(button(render(), "Confirmer le nettoyage du lot").props.disabled, true);
  } finally { globalThis.__historyUi = previous; globalThis.window = previousWindow; }
});
