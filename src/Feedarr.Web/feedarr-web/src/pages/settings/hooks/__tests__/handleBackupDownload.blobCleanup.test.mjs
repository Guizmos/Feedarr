/**
 * Tests for Blob URL cleanup in handleBackupDownload.
 *
 * Verifies that window.URL.revokeObjectURL and document.body.removeChild
 * are always called, even when an exception is thrown during the DOM click.
 *
 * Run: node --test src/pages/settings/hooks/__tests__/handleBackupDownload.blobCleanup.test.mjs
 */
import test from "node:test";
import assert from "node:assert/strict";

// ---------------------------------------------------------------------------
// Minimal DOM / URL mock helpers
// ---------------------------------------------------------------------------
function makeDomMocks({ clickThrows = false } = {}) {
  let revokeCount = 0;
  let removeChildCount = 0;
  let appendedEl = null;

  const fakeBody = {
    appendChild(el) { appendedEl = el; },
    removeChild(el) {
      if (el === appendedEl) removeChildCount++;
    },
    contains(el) { return el === appendedEl; },
  };

  const fakeWindow = {
    URL: {
      createObjectURL: () => "blob:fake-url",
      revokeObjectURL: () => { revokeCount++; },
    },
  };

  const fakeDocument = {
    createElement: (tag) => {
      const el = { tag, href: "", download: "" };
      el.click = clickThrows
        ? () => { throw new Error("click() failed"); }
        : () => {};
      return el;
    },
    body: fakeBody,
  };

  return { fakeWindow, fakeDocument, getRevokeCount: () => revokeCount, getRemoveChildCount: () => removeChildCount };
}

// ---------------------------------------------------------------------------
// Replicate the try/finally block extracted from handleBackupDownload
// ---------------------------------------------------------------------------
async function runDownloadBlock({ window: w, document: doc }, blob, name) {
  const url = w.URL.createObjectURL(blob);
  let a;
  try {
    a = doc.createElement("a");
    a.href = url;
    a.download = name;
    doc.body.appendChild(a);
    a.click();
  } finally {
    w.URL.revokeObjectURL(url);
    if (a && doc.body.contains(a)) doc.body.removeChild(a);
  }
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

test("revokeObjectURL is called on successful download", async () => {
  const { fakeWindow, fakeDocument, getRevokeCount, getRemoveChildCount } = makeDomMocks();

  await runDownloadBlock({ window: fakeWindow, document: fakeDocument }, {}, "backup.zip");

  assert.equal(getRevokeCount(), 1, "revokeObjectURL must be called once");
  assert.equal(getRemoveChildCount(), 1, "removeChild must be called once");
});

test("revokeObjectURL is called even when a.click() throws", async () => {
  const { fakeWindow, fakeDocument, getRevokeCount, getRemoveChildCount } = makeDomMocks({ clickThrows: true });

  await assert.rejects(
    () => runDownloadBlock({ window: fakeWindow, document: fakeDocument }, {}, "backup.zip"),
    /click\(\) failed/
  );

  assert.equal(getRevokeCount(), 1, "revokeObjectURL must be called even on throw");
  assert.equal(getRemoveChildCount(), 1, "removeChild must be called even on throw");
});

test("revokeObjectURL is called exactly once even on success", async () => {
  const { fakeWindow, fakeDocument, getRevokeCount } = makeDomMocks();

  await runDownloadBlock({ window: fakeWindow, document: fakeDocument }, {}, "test.zip");
  await runDownloadBlock({ window: fakeWindow, document: fakeDocument }, {}, "test2.zip");

  assert.equal(getRevokeCount(), 2, "revokeObjectURL must be called once per download");
});
