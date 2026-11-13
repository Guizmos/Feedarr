import test from "node:test";
import assert from "node:assert/strict";
import { createDomEnvironment } from "./domEnvironment.mjs";

test("DOM globals replace and restore absent or read-only navigator without invoking its getter", () => {
  const original = Object.getOwnPropertyDescriptor(globalThis, "navigator");
  try {
    for (const descriptor of [undefined, { configurable: true, enumerable: true, get: () => { throw new Error("Native navigator getter must not be read"); } }]) {
      delete globalThis.navigator;
      if (descriptor) Object.defineProperty(globalThis, "navigator", descriptor);
      const before = Object.getOwnPropertyDescriptor(globalThis, "navigator");
      const environment = createDomEnvironment();
      assert.equal(globalThis.navigator, environment.dom.window.navigator);
      environment.restore(); environment.restore();
      assert.deepEqual(Object.getOwnPropertyDescriptor(globalThis, "navigator"), before);
    }
  } finally { if (original) Object.defineProperty(globalThis, "navigator", original); else delete globalThis.navigator; }
});

test("DOM teardown restores original descriptors for browser globals, HTTP and fake timers", () => {
  const keys = ["window", "document", "navigator", "localStorage", "CustomEvent", "getComputedStyle", "ResizeObserver", "fetch", "setTimeout", "clearTimeout", "IS_REACT_ACT_ENVIRONMENT"];
  const before = keys.map(key => Object.getOwnPropertyDescriptor(globalThis, key));
  const environment = createDomEnvironment();
  try { globalThis.fetch = () => {}; globalThis.setTimeout = () => {}; throw new Error("simulated test setup failure"); }
  catch { /* Fixture finally must also clean up an unsuccessful initialization. */ }
  finally { environment.restore(); }
  assert.deepEqual(keys.map(key => Object.getOwnPropertyDescriptor(globalThis, key)), before);
});
