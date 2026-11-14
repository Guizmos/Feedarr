import test from "node:test";
import assert from "node:assert/strict";
import { build } from "esbuild";
import { fileURLToPath } from "node:url";

const built = await build({
  entryPoints: [fileURLToPath(new URL("../MatchingReplayPanel.jsx", import.meta.url))],
  bundle: true, platform: "node", format: "esm", write: false,
  plugins: [{ name: "matching-panel", setup(b) {
    b.onResolve({ filter: /^react$/ }, () => ({ path: "react", namespace: "test" }));
    b.onResolve({ filter: /api\/client\.js$/ }, () => ({ path: "api", namespace: "test" }));
    b.onLoad({ filter: /.*/, namespace: "test" }, a => ({ contents: a.path === "api"
      ? "export const apiGet=(...a)=>globalThis.__matchingUi.get(...a);export const apiPost=(...a)=>globalThis.__matchingUi.post(...a);"
      : `const React={createElement:(type,props,...children)=>({type,props:props||{},children})};export default React;
         export function useState(v){const s=globalThis.__matchingUi,i=s.index++;if(!(i in s.values))s.values[i]=v;return[s.values[i],v=>{s.values[i]=v;}];}
         export function useRef(v){const s=globalThis.__matchingUi,i=s.index++;if(!(i in s.values))s.values[i]={current:v};return s.values[i];}
         export const useCallback=f=>f;export const useEffect=f=>globalThis.__matchingUi.effects.push(f);` }));
  } }],
});
const { default: Panel } = await import(`data:text/javascript;base64,${Buffer.from(built.outputFiles[0].text).toString("base64")}`);
function nodes(n) { return !n || typeof n !== "object" ? [] : Array.isArray(n) ? n.flatMap(nodes) : [n, ...n.children.flatMap(nodes)]; }
const render = () => { globalThis.__matchingUi.index = 0; return Panel(); };
const button = (tree, label) => nodes(tree).find(n => n.type === "button" && n.children.includes(label));

test("targeted diagnostic validates IDs and only calls read-only diagnostic endpoints", async () => {
  const previous = globalThis.__matchingUi, calls = [];
  const report = { generation: 4, running: false, sessionId: null, entries: [] };
  const state = globalThis.__matchingUi = { index: 0, values: [], effects: [],
    get: async path => { calls.push(["GET", path]); return report; },
    post: async (path, body) => { calls.push(["POST", path, body]); return { ...report, sessionId: "session-1", generation: 5, running: true }; } };
  try {
    render(); const cleanups = state.effects.splice(0).map(f => f());
    await new Promise(resolve => setImmediate(resolve));
    let tree = render();
    nodes(tree).find(n => n.type === "input").props.onChange({ target: { value: "bad-id" } });
    await button(render(), "Analyser").props.onClick();
    assert.equal(calls.length, 1);
    nodes(render()).find(n => n.type === "input").props.onChange({ target: { value: "523, 403, 523" } });
    await button(render(), "Analyser").props.onClick();
    assert.equal(calls[1][1], "/api/diagnostics/matching/replay");
    assert.deepEqual(calls[1][2].selection, { method: "ids", releaseIds: [523, 403] });
    assert.equal(calls[1][2].mode, "tmdb");
    assert.equal(calls[1][2].budget, 30);
    tree = render(); assert.equal(button(tree, "Analyser").props.disabled, true);
    await button(tree, "Arrêter").props.onClick();
    assert.deepEqual(calls[2], ["POST", "/api/diagnostics/matching/replay/stop", { sessionId: "session-1", generation: 5 }]);
    cleanups.forEach(f => f?.());
  } finally { globalThis.__matchingUi = previous; }
});
