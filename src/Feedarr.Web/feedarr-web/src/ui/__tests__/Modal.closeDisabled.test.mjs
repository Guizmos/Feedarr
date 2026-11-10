import test from "node:test";
import assert from "node:assert/strict";
import { build } from "esbuild";
import { createRequire } from "node:module";
import { fileURLToPath, pathToFileURL } from "node:url";
const require = createRequire(import.meta.url);
const react = pathToFileURL(require.resolve("react")).href;
const bundle = await build({
  entryPoints: [fileURLToPath(new URL("../Modal.jsx", import.meta.url))], bundle: true,
  platform: "node", format: "esm", write: false,
  plugins: [{ name: "modal-control", setup(builder) {
    builder.onResolve({ filter: /^react$/ }, () => ({ path: "react", namespace: "test" }));
    builder.onResolve({ filter: /^react-dom$/ }, () => ({ path: "portal", namespace: "test" }));
    builder.onResolve({ filter: /AppIcon\.jsx$/ }, () => ({ path: "icon", namespace: "test" }));
    builder.onResolve({ filter: /^file:/ }, args => ({ path: args.path, external: true }));
    builder.onLoad({ filter: /.*/, namespace: "test" }, args => ({ contents: args.path === "react"
      ? `import React from ${JSON.stringify(react)}; export * from ${JSON.stringify(react)}; export default React; export const useEffect = effect => globalThis.__modalEffects.push(effect);`
      : args.path === "portal" ? "export const createPortal = child => child;" : "export default () => null;" }));
  } }],
});
const { default: Modal } = await import(`data:text/javascript;base64,${Buffer.from(bundle.outputFiles[0].text).toString("base64")}`);
test("modal disables cross, backdrop and Escape during create/restore operation", () => {
  const oldWindow = globalThis.window, oldDocument = globalThis.document, oldEffects = globalThis.__modalEffects;
  try {
    for (const locked of [true, false]) {
      globalThis.__modalEffects = [];
      let onKey, closed = 0;
      globalThis.window = { addEventListener: (_, fn) => { onKey = fn; }, removeEventListener: () => {} };
      globalThis.document = { body: {} };
      const node = Modal({ open: true, closeDisabled: locked, onClose: () => { closed++; } });
      const button = node.props.children.props.children[0].props.children[1];
      assert.equal(button.props.disabled, locked);
      assert.equal(typeof node.props.onClick, locked ? "undefined" : "function");
      const cleanup = globalThis.__modalEffects[0]();
      onKey({ key: "Escape" });
      assert.equal(closed, locked ? 0 : 1);
      cleanup();
    }
  } finally { globalThis.window = oldWindow; globalThis.document = oldDocument; globalThis.__modalEffects = oldEffects; }
});
