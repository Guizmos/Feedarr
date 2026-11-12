export function shadowIdentity(identity) {
  if (!identity || identity.provider !== "tmdb" || !["movie", "tv"].includes(identity.type) || !/^\d+$/.test(identity.id)) return "—";
  return `tmdb:${identity.type}:${identity.id}`;
}
export function shadowCounts(report) {
  return ["accept", "ambiguous", "reject", "notEvaluable"].map(key => `${key === "notEvaluable" ? "not_evaluable" : key}: ${report[key] || 0}`).join(" · ");
}
