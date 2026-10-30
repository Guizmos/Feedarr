import test from "node:test";
import assert from "node:assert/strict";
import { selectArtworkAccent } from "./artworkAccent.js";

function pixels(...colors) {
  return new Uint8ClampedArray(colors.flatMap(([red, green, blue, alpha = 255]) => [
    red,
    green,
    blue,
    alpha,
  ]));
}

function hslParts(accent) {
  const match = /^hsl\((\d+) (\d+)% (\d+)%\)$/.exec(accent || "");
  assert.ok(match, `Expected a normalized HSL color, received ${accent}`);
  return match.slice(1).map(Number);
}

test("normalizes a very dark colorful poster into a readable accent", () => {
  const [, saturation, lightness] = hslParts(selectArtworkAccent(pixels([18, 5, 45])));
  assert.ok(saturation >= 46 && saturation <= 68);
  assert.ok(lightness >= 46 && lightness <= 58);
});

test("normalizes a very bright colorful poster into a discreet accent", () => {
  const [, saturation, lightness] = hslParts(selectArtworkAccent(pixels([250, 210, 180])));
  assert.ok(saturation >= 46 && saturation <= 68);
  assert.ok(lightness >= 46 && lightness <= 58);
});

test("caps highly saturated poster colors", () => {
  const [, saturation, lightness] = hslParts(selectArtworkAccent(pixels([255, 0, 80])));
  assert.equal(saturation, 68);
  assert.ok(lightness >= 46 && lightness <= 58);
});

test("prefers a colorful region over neutral pixels", () => {
  const [hue] = hslParts(selectArtworkAccent(pixels(
    [120, 120, 120],
    [110, 110, 110],
    [20, 110, 220]
  )));
  assert.ok(hue >= 200 && hue <= 225);
});

test("returns null for grayscale, transparent, or unusable pixels", () => {
  assert.equal(selectArtworkAccent(pixels([120, 120, 120], [255, 0, 0, 20])), null);
});
