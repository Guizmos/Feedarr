import test from "node:test";
import assert from "node:assert/strict";
import { getSaveActionVisualState } from "./settingsUiPageModel.js";

test("getSaveActionVisualState maps loading state", () => {
  assert.deepEqual(
    getSaveActionVisualState("loading"),
    { icon: "progress_activity", className: "is-loading" }
  );
});

test("getSaveActionVisualState maps success state", () => {
  assert.deepEqual(
    getSaveActionVisualState("success"),
    { icon: "check_circle", className: "is-success" }
  );
});

test("getSaveActionVisualState maps error state", () => {
  assert.deepEqual(
    getSaveActionVisualState("error"),
    { icon: "cancel", className: "is-error" }
  );
});

test("getSaveActionVisualState falls back to idle presentation", () => {
  assert.deepEqual(
    getSaveActionVisualState("idle"),
    { icon: "save", className: "" }
  );
});
