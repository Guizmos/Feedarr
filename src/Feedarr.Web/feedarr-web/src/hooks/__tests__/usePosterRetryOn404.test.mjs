import test from "node:test";
import assert from "node:assert/strict";
import { probePosterHead404 } from "../usePosterRetryOn404.js";

test("probePosterHead404 returns true when HEAD returns 404", async () => {
  let capturedSignal = null;

  const fetchImpl = async (_url, options) => {
    capturedSignal = options?.signal ?? null;
    return { status: 404 };
  };

  const controller = new AbortController();
  const result = await probePosterHead404("/api/posters/release/1", controller, 50, fetchImpl);

  assert.equal(result, true);
  assert.ok(capturedSignal, "fetch should receive an AbortSignal");
  assert.equal(capturedSignal.aborted, false);
});

test("probePosterHead404 aborts pending HEAD on timeout and returns false", async () => {
  let abortedBySignal = false;

  const fetchImpl = (_url, options) =>
    new Promise((_resolve, reject) => {
      options.signal.addEventListener(
        "abort",
        () => {
          abortedBySignal = true;
          const error = new Error("aborted");
          error.name = "AbortError";
          reject(error);
        },
        { once: true }
      );
    });

  const controller = new AbortController();
  const result = await probePosterHead404("/api/posters/release/2", controller, 10, fetchImpl);

  assert.equal(result, false);
  assert.equal(abortedBySignal, true);
  assert.equal(controller.signal.aborted, true);
});

test("probePosterHead404 returns false when HEAD is not 404", async () => {
  const fetchImpl = async () => ({ status: 200 });
  const controller = new AbortController();

  const result = await probePosterHead404("/api/posters/release/3", controller, 50, fetchImpl);

  assert.equal(result, false);
  assert.equal(controller.signal.aborted, false);
});
