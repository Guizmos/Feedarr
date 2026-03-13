/**
 * Tests for computeGridLayout — the pure calculation at the core of useGridLayout.
 *
 * These tests validate that the JS formula correctly reproduces the browser's
 * CSS auto-fill + minmax() layout logic, which is required for the future
 * virtualiser row/column calculations.
 *
 * Run: node --test src/pages/library/hooks/__tests__/useGridLayout.test.mjs
 */
import test from "node:test";
import assert from "node:assert/strict";
import { computeGridLayout } from "../useGridLayout.js";

// ---------------------------------------------------------------------------
// numCols — doit reproduire CSS repeat(auto-fill, minmax(cardSize, 1fr))
// ---------------------------------------------------------------------------

test("numCols: conteneur 800px, carte 190px, gap 20px → 4 colonnes", () => {
  // (800 + 20) / (190 + 20) = 820 / 210 ≈ 3.9 → floor = 3 → max(1,3) = 3
  // Attendu: 3 colonnes (800+20=820, 190+20=210, 820/210≈3.9 → 3)
  const { numCols } = computeGridLayout(800, 190, { gap: 20 });
  // Vérification: 3 colonnes × 210px = 630px + ajustement 1fr → correct
  assert.equal(numCols, 3);
});

test("numCols: conteneur 840px, carte 190px, gap 20px → 4 colonnes", () => {
  // (840 + 20) / (190 + 20) = 860 / 210 ≈ 4.09 → floor = 4
  const { numCols } = computeGridLayout(840, 190, { gap: 20 });
  assert.equal(numCols, 4);
});

test("numCols: conteneur 1200px, carte 190px, gap 20px → 5 colonnes", () => {
  // (1200 + 20) / (190 + 20) = 1220 / 210 ≈ 5.8 → floor = 5
  const { numCols } = computeGridLayout(1200, 190, { gap: 20 });
  assert.equal(numCols, 5);
});

test("numCols: conteneur 190px exact, carte 190px, gap 20px → 1 colonne", () => {
  const { numCols } = computeGridLayout(190, 190, { gap: 20 });
  assert.equal(numCols, 1);
});

test("numCols: conteneur 400px, carte 180px (poster), gap 20px → 2 colonnes", () => {
  // (400 + 20) / (180 + 20) = 420 / 200 = 2.1 → floor = 2
  const { numCols } = computeGridLayout(400, 180, { gap: 20 });
  assert.equal(numCols, 2);
});

test("numCols ne descend jamais sous 1 même si conteneur très petit", () => {
  const { numCols } = computeGridLayout(50, 190, { gap: 20 });
  assert.equal(numCols, 1);
});

test("numCols: gap 0 → calcul sans espace entre colonnes", () => {
  // (600 + 0) / (150 + 0) = 4
  const { numCols } = computeGridLayout(600, 150, { gap: 0 });
  assert.equal(numCols, 4);
});

test("numCols: gap personnalisé 10px", () => {
  // (630 + 10) / (150 + 10) = 640 / 160 = 4
  const { numCols } = computeGridLayout(630, 150, { gap: 10 });
  assert.equal(numCols, 4);
});

// ---------------------------------------------------------------------------
// colWidth — doit remplir exactement le conteneur avec numCols colonnes
// ---------------------------------------------------------------------------

test("colWidth: la somme des colonnes + gaps remplit le conteneur", () => {
  const containerWidth = 1000;
  const { numCols, colWidth } = computeGridLayout(containerWidth, 190, { gap: 20 });
  const totalUsed = colWidth * numCols + (numCols - 1) * 20;
  assert.ok(
    Math.abs(totalUsed - containerWidth) < 0.01,
    `totalUsed=${totalUsed} doit être ≈ ${containerWidth}`,
  );
});

test("colWidth: vérification avec poster 180px, conteneur 760px", () => {
  const containerWidth = 760;
  const gap = 20;
  const { numCols, colWidth } = computeGridLayout(containerWidth, 180, { gap });
  const totalUsed = colWidth * numCols + (numCols - 1) * gap;
  assert.ok(
    Math.abs(totalUsed - containerWidth) < 0.01,
    `totalUsed=${totalUsed} doit être ≈ ${containerWidth}`,
  );
});

test("colWidth: toujours >= cardSize (les colonnes s'étendent en 1fr)", () => {
  const cardSize = 190;
  const { colWidth } = computeGridLayout(1000, cardSize, { gap: 20 });
  assert.ok(colWidth >= cardSize, `colWidth=${colWidth} doit être >= cardSize=${cardSize}`);
});

// ---------------------------------------------------------------------------
// estimatedRowHeight — estimation initiale pour le virtualiser
// ---------------------------------------------------------------------------

test("estimatedRowHeight: positif pour des inputs valides", () => {
  const { estimatedRowHeight } = computeGridLayout(800, 190);
  assert.ok(estimatedRowHeight > 0, `estimatedRowHeight doit être > 0, got ${estimatedRowHeight}`);
});

test("estimatedRowHeight: augmente avec la largeur de colonne", () => {
  const narrow = computeGridLayout(400, 190, { gap: 20 });
  const wide = computeGridLayout(1600, 190, { gap: 20 });
  assert.ok(
    wide.estimatedRowHeight > narrow.estimatedRowHeight,
    "une colonne plus large doit produire une hauteur estimée plus grande",
  );
});

test("estimatedRowHeight: cardHeightRatio 2.0 donne une hauteur plus grande", () => {
  const base = computeGridLayout(800, 190, { cardHeightRatio: 1.5 });
  const tall = computeGridLayout(800, 190, { cardHeightRatio: 2.0 });
  assert.ok(tall.estimatedRowHeight > base.estimatedRowHeight);
});

test("estimatedRowHeight: titleAreaPx contribue à la hauteur totale", () => {
  const noTitle = computeGridLayout(800, 190, { titleAreaPx: 0 });
  const withTitle = computeGridLayout(800, 190, { titleAreaPx: 44 });
  assert.equal(withTitle.estimatedRowHeight - noTitle.estimatedRowHeight, 44);
});

// ---------------------------------------------------------------------------
// Cas limites et robustesse
// ---------------------------------------------------------------------------

test("containerWidth = 0 → valeurs de fallback sans erreur", () => {
  const result = computeGridLayout(0, 190);
  assert.equal(result.numCols, 1);
  assert.equal(result.colWidth, 0);
  assert.equal(result.estimatedRowHeight, 0);
});

test("containerWidth < 0 → valeurs de fallback sans erreur", () => {
  const result = computeGridLayout(-100, 190);
  assert.equal(result.numCols, 1);
  assert.equal(result.colWidth, 0);
  assert.equal(result.estimatedRowHeight, 0);
});

test("cardSize = 0 → valeurs de fallback sans erreur", () => {
  const result = computeGridLayout(800, 0);
  assert.equal(result.numCols, 1);
  assert.equal(result.colWidth, 0);
  assert.equal(result.estimatedRowHeight, 0);
});

test("cardSize < 0 → valeurs de fallback sans erreur", () => {
  const result = computeGridLayout(800, -50);
  assert.equal(result.numCols, 1);
  assert.equal(result.colWidth, 0);
  assert.equal(result.estimatedRowHeight, 0);
});

test("aucun NaN dans les résultats pour des inputs normaux", () => {
  const { numCols, colWidth, estimatedRowHeight } = computeGridLayout(1000, 190, { gap: 20 });
  assert.ok(!Number.isNaN(numCols), "numCols ne doit pas être NaN");
  assert.ok(!Number.isNaN(colWidth), "colWidth ne doit pas être NaN");
  assert.ok(!Number.isNaN(estimatedRowHeight), "estimatedRowHeight ne doit pas être NaN");
});

test("defaults appliqués correctement sans objet opts", () => {
  // gap=20, cardHeightRatio=1.5, titleAreaPx=44 par défaut
  const withDefaults = computeGridLayout(800, 190);
  const explicit = computeGridLayout(800, 190, { gap: 20, cardHeightRatio: 1.5, titleAreaPx: 44 });
  assert.deepEqual(withDefaults, explicit);
});
