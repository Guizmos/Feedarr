import test from "node:test";
import assert from "node:assert/strict";
import { executeAsync, getErrorMessage } from "./executeAsync.js";

test("getErrorMessage returns trimmed message or fallback", () => {
  assert.equal(getErrorMessage(new Error("  boom  "), "fallback"), "boom");
  assert.equal(getErrorMessage({ message: "   " }, "fallback"), "fallback");
  assert.equal(getErrorMessage(null, "fallback"), "fallback");
});

test("executeAsync returns value and clears/finalizes on success", async () => {
  const events = [];

  const value = await executeAsync(
    async () => "ok",
    {
      clearError: () => events.push("clear"),
      onFinally: () => events.push("finally"),
    }
  );

  assert.equal(value, "ok");
  assert.deepEqual(events, ["clear", "finally"]);
});

test("executeAsync sets user-facing error and calls hooks on failure", async () => {
  const events = [];
  const originalConsoleError = console.error;
  console.error = () => {};

  try {
    const value = await executeAsync(
      async () => {
        throw new Error("network down");
      },
      {
        context: "sync failed",
        clearError: () => events.push("clear"),
        setError: (message) => events.push(`set:${message}`),
        onError: (_error, message) => events.push(`onError:${message}`),
        onFinally: () => events.push("finally"),
      }
    );

    assert.equal(value, null);
    assert.deepEqual(events, [
      "clear",
      "set:network down",
      "onError:network down",
      "finally",
    ]);
  } finally {
    console.error = originalConsoleError;
  }
});

test("executeAsync can rethrow while still finalizing", async () => {
  let finalized = false;
  const originalConsoleError = console.error;
  console.error = () => {};

  try {
    await assert.rejects(
      () =>
        executeAsync(
          async () => {
            throw new Error("fatal");
          },
          {
            rethrow: true,
            onFinally: () => {
              finalized = true;
            },
          }
        ),
      /fatal/
    );
    assert.equal(finalized, true);
  } finally {
    console.error = originalConsoleError;
  }
});
