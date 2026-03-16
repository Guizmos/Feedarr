import test from "node:test";
import assert from "node:assert/strict";
import { matchesTitleSearch } from "../searchUtils.js";

// ---------------------------------------------------------------------------
// Recherche mono-mot
// ---------------------------------------------------------------------------

test("recherche simple : 'final' matche un titre avec séparateurs points", () => {
  assert.equal(matchesTitleSearch({ title: "Final.Fantasy.VII.Remake" }, "final"), true);
});

test("recherche simple : 'final' ne matche pas un titre non lié", () => {
  assert.equal(matchesTitleSearch({ title: "Breaking.Bad.S01E01" }, "final"), false);
});

test("recherche simple : insensible à la casse", () => {
  assert.equal(matchesTitleSearch({ title: "Final.Fantasy.VII" }, "FINAL"), true);
});

// ---------------------------------------------------------------------------
// Recherche multi-mots (cas principal du bug)
// ---------------------------------------------------------------------------

test("recherche multi-mots : 'final fan' matche 'Final.Fantasy.VII'", () => {
  assert.equal(matchesTitleSearch({ title: "Final.Fantasy.VII" }, "final fan"), true);
});

test("recherche multi-mots : 'final fan' matche 'Final.Fantasy.XIV.Online'", () => {
  assert.equal(matchesTitleSearch({ title: "Final.Fantasy.XIV.Online.2024" }, "final fan"), true);
});

test("recherche multi-mots : tous les mots doivent être présents (logique AND)", () => {
  // "fantasy" est présent mais "breaking" non
  assert.equal(matchesTitleSearch({ title: "Final.Fantasy.VII" }, "final breaking"), false);
});

test("recherche multi-mots : espaces multiples normalisés", () => {
  assert.equal(matchesTitleSearch({ title: "Final.Fantasy.VII" }, "  final   fan  "), true);
});

// ---------------------------------------------------------------------------
// Priorité titleClean sur title
// ---------------------------------------------------------------------------

test("utilise titleClean en priorité quand disponible", () => {
  const item = {
    titleClean: "Final Fantasy VII Remake",
    title: "Final.Fantasy.VII.Remake.2024.BLURAY-GROUP",
  };
  // Avec titleClean (espaces), "final fan" matche directement
  assert.equal(matchesTitleSearch(item, "final fan"), true);
});

test("fallback sur title si titleClean absent", () => {
  const item = { title: "Final.Fantasy.VII.Remake" };
  assert.equal(matchesTitleSearch(item, "final fan"), true);
});

test("fallback sur title si titleClean est une chaîne vide", () => {
  const item = { titleClean: "", title: "Final.Fantasy.VII" };
  assert.equal(matchesTitleSearch(item, "final fan"), true);
});

test("fallback sur title si titleClean est null", () => {
  const item = { titleClean: null, title: "Final.Fantasy.VII" };
  assert.equal(matchesTitleSearch(item, "final fan"), true);
});

// ---------------------------------------------------------------------------
// Cas limites
// ---------------------------------------------------------------------------

test("requête vide retourne true (pas de filtre)", () => {
  assert.equal(matchesTitleSearch({ title: "Any.Title" }, ""), true);
});

test("requête null retourne true", () => {
  assert.equal(matchesTitleSearch({ title: "Any.Title" }, null), true);
});

test("requête uniquement espaces retourne true", () => {
  assert.equal(matchesTitleSearch({ title: "Any.Title" }, "   "), true);
});

test("item sans titre retourne false si requête non vide", () => {
  assert.equal(matchesTitleSearch({}, "final"), false);
});

test("item null/undefined ne lève pas d'erreur", () => {
  assert.equal(matchesTitleSearch(null, "final"), false);
  assert.equal(matchesTitleSearch(undefined, "final"), false);
});

// ---------------------------------------------------------------------------
// Comportement identique source unique / multi-source
// (la fonction est partagée : les deux chemins passent par matchesTitleSearch)
// ---------------------------------------------------------------------------

test("comportement cohérent : même résultat quel que soit le mode (logique pure)", () => {
  const item = { title: "Final.Fantasy.VII.Remake.BLURAY" };
  // En source unique, le backend FTS pré-filtre puis le client re-filtre
  // En multi-source, seul le client filtre — les deux appellent matchesTitleSearch
  assert.equal(matchesTitleSearch(item, "final fan"), true);
  assert.equal(matchesTitleSearch(item, "remake bluray"), true);
  assert.equal(matchesTitleSearch(item, "final xyz"), false);
});
