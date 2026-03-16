/**
 * Retourne true si tous les mots de `query` apparaissent dans le titre de l'item.
 *
 * Découpe la requête sur les espaces et vérifie chaque mot indépendamment (logique AND),
 * ce qui correspond au comportement FTS du backend (`"token1"* AND "token2"*`).
 *
 * Utilise `titleClean` en priorité (version normalisée sans séparateurs de type « . »),
 * puis se replie sur `title` si absent.
 *
 * Exemples :
 *   matchesTitleSearch({ title: "Final.Fantasy.VII" }, "final")     → true
 *   matchesTitleSearch({ title: "Final.Fantasy.VII" }, "final fan") → true  ("fan" ⊆ "fantasy")
 *   matchesTitleSearch({ titleClean: "Final Fantasy VII" }, "final fan") → true
 *   matchesTitleSearch({ title: "Breaking.Bad.S01" }, "final fan")  → false
 */
export function matchesTitleSearch(item, query) {
  const q = String(query || "").trim().toLowerCase();
  if (!q) return true;

  const words = q.split(/\s+/).filter(Boolean);
  const title = String(item?.titleClean || item?.title || "").toLowerCase();
  return words.every((w) => title.includes(w));
}
