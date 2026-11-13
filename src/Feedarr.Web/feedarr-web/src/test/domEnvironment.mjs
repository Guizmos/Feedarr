import { JSDOM } from "jsdom";

// Install before importing react-dom/client. Node 20 has no navigator;
// newer Node versions may expose it through a read-only accessor.
export function createDomEnvironment({ url = "https://feedarr.test/", html = '<!doctype html><html><body><div id="root"></div></body></html>' } = {}) {
  const dom = new JSDOM(html, { url, pretendToBeVisual: true });
  const values = {
    window: dom.window, document: dom.window.document, navigator: dom.window.navigator,
    FormData: dom.window.FormData,
    HTMLElement: dom.window.HTMLElement, HTMLInputElement: dom.window.HTMLInputElement,
    CustomEvent: dom.window.CustomEvent, MutationObserver: dom.window.MutationObserver,
    localStorage: dom.window.localStorage, IS_REACT_ACT_ENVIRONMENT: true,
    getComputedStyle: dom.window.getComputedStyle.bind(dom.window),
    requestAnimationFrame: dom.window.requestAnimationFrame.bind(dom.window),
    cancelAnimationFrame: dom.window.cancelAnimationFrame.bind(dom.window),
    // jsdom has no layout/resize engine; observers are inert in DOM interaction tests.
    ResizeObserver: class { observe() {} unobserve() {} disconnect() {} },
  };
  const keys = [...Object.keys(values), "fetch", "setTimeout", "clearTimeout"];
  const descriptors = new Map(keys.map(key => [key, Object.getOwnPropertyDescriptor(globalThis, key)]));
  let restored = false;
  const restore = () => {
    if (restored) return;
    restored = true;
    try { dom.window.close(); }
    finally {
      for (const [key, descriptor] of descriptors) {
        if (descriptor) Object.defineProperty(globalThis, key, descriptor);
        else delete globalThis[key];
      }
    }
  };
  try {
    for (const [key, value] of Object.entries(values)) Object.defineProperty(globalThis, key, { configurable: true, writable: true, value });
    return { dom, restore };
  } catch (error) { restore(); throw error; }
}
