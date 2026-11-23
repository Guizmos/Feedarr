import test from "node:test";
import assert from "node:assert/strict";
import React, { act, createRef } from "react";
import { createDomEnvironment } from "../../../../test/domEnvironment.mjs";
import { ScrollContainerProvider } from "../../../../context/ScrollContainerContext.js";
import { useVirtualGrid } from "../useVirtualGrid.js";

test("visible row measurements and cached estimates follow resize, regrouping, hover and scroll", async () => {
  const environment = createDomEnvironment();
  const observers = new Set();
  class Observer {
    constructor(callback) { this.callback = callback; this.targets = new Set(); observers.add(this); }
    observe(target) {
      this.targets.add(target);
      if (target.dataset.width) this.callback([{ target, contentRect: { width: Number(target.dataset.width) } }]);
    }
    unobserve(target) { this.targets.delete(target); }
    disconnect() { observers.delete(this); }
  }
  globalThis.ResizeObserver = window.ResizeObserver = Observer;
  const prototype = window.HTMLElement.prototype;
  prototype.getBoundingClientRect = function () {
    return { top: 0, left: 0, width: Number(this.dataset.width || 1200), height: Number(this.dataset.height || 500) };
  };
  Object.defineProperties(prototype, { offsetWidth: { get() { return 1200; } }, offsetHeight: { get() { return 500; } } });
  prototype.scrollTo = function ({ top }) { this.scrollTop = top; };
  const { createRoot } = await import("react-dom/client");
  const root = createRoot(document.getElementById("root"));
  const scrollRef = createRef();
  const items = Array.from({ length: 5000 }, (_, id) => ({ id }));
  let view;
  function Grid({ size, width, mode }) {
    view = useVirtualGrid(items, size, { gap: 10, titleAreaPx: 0, measureRows: true, minColsFn: w => w > 0 && w <= 768 ? (mode === "poster" ? 3 : 2) : 1 });
    // Controlled measurements exercise the actual hook/TanStack integration;
    // jsdom itself has no CSS layout engine.
    const measuredHeight = mode === "poster" ? ((width - 10 * (view.numCols - 1)) / view.numCols) * 1.5 : size * 1.2 + 60.25;
    return React.createElement("div", { ref: view.containerRef, "data-width": width },
      view.virtualizer.getVirtualItems().map(row => React.createElement("div", {
        key: row.key, "data-index": row.index, "data-height": measuredHeight, ref: view.virtualizer.measureElement,
      })));
  }
  async function render(size, width, mode = "cards") {
    await act(async () => {
      root.render(React.createElement(ScrollContainerProvider, { scrollRef },
        React.createElement("div", { ref: scrollRef }, React.createElement(Grid, { size, width, mode }))));
    });
    // Reproduce the container observer's width notification after resize.
    await act(async () => {
      for (const observer of observers) for (const target of observer.targets) {
        if (target.dataset.width) observer.callback([{ target, contentRect: { width } }]);
      }
    });
  }
  function assertGap() {
    const rows = view.virtualizer.getVirtualItems();
    assert.ok(rows.length > 1 && rows.length < 100, "only the visible window is mounted");
    for (let i = 1; i < rows.length; i++) assert.ok(Math.abs(rows[i].start - rows[i - 1].end - 10) < 1e-8);
  }
  try {
    for (const mode of ["poster", "cards"]) {
      for (const width of [360, 768, 1200]) for (const size of mode === "poster" ? [90, 180, 270] : [95, 190, 285]) {
        await render(size, width, mode);
        assertGap();
        const expectedHeight = mode === "poster" ? ((width - 10 * (view.numCols - 1)) / view.numCols) * 1.5 : size * 1.2 + 60.25;
        assert.ok(Math.abs(view.virtualizer.getVirtualItems()[0].size - expectedHeight) < 1e-8);
      }
    }
    // Same number of columns: old item sizes must still be invalidated.
    await render(185, 800);
    const columns = view.numCols;
    await render(190, 800);
    assert.equal(view.numCols, columns);
    assert.equal(view.virtualizer.getVirtualItems()[0].size, 288.25);

    // Simulate the ResizeObserver notifications emitted throughout a hover transition.
    const row = document.querySelector("[data-index='0']");
    row.matches = selector => selector === ":hover";
    await act(async () => {
      for (const observer of observers) if (observer.targets.has(row))
        observer.callback([{ target: row, borderBoxSize: [{ blockSize: 308.25 }] }]);
    });
    assert.equal(view.virtualizer.getVirtualItems()[0].size, 308.25);
    assertGap();
    row.matches = () => false;
    await act(async () => {
      for (const observer of observers) if (observer.targets.has(row))
        observer.callback([{ target: row, borderBoxSize: [{ blockSize: 288.25 }] }]);
    });
    assert.equal(view.virtualizer.getVirtualItems()[0].size, 288.25);
    assertGap();

    await act(async () => { scrollRef.current.scrollTop = 3000; scrollRef.current.dispatchEvent(new window.Event("scroll")); });
    await render(285, 600);
    assertGap();
    assert.ok(view.virtualizer.getVirtualItems().some(row => row.index > 0));
  } finally { await act(async () => root.unmount()); environment.restore(); }
});
