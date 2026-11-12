export const replayCategories = ["animation", "emissions", "films", "series", "spectacle"];
export const replayCategoryLabels = { films: "Films", series: "Séries", animation: "Animations", emissions: "Émissions", spectacle: "Spectacles" };
export function parseReplayIds(text) {
  if (typeof text !== "string" || text.length > 4000) throw new Error("replay_ids_invalid");
  const tokens = text.trim() ? text.trim().split(/[\s,;]+/) : [];
  if (tokens.length > 200 || tokens.some(token => !/^[1-9]\d{0,15}$/.test(token))) throw new Error("replay_ids_invalid");
  const ids = tokens.map(Number);
  if (ids.some(id => !Number.isSafeInteger(id)) || new Set(ids).size !== ids.length) throw new Error("replay_ids_invalid");
  return ids;
}
export function replayPayload(count, mode, budget, generation, selection, expectedIds) {
  if (![10, 25, 50, 100, 200].includes(count) || !["offline", "tmdb"].includes(mode) ||
      !Number.isInteger(budget) || budget < 1 || budget > 30 || !Number.isSafeInteger(generation) || generation < 0) throw new Error("replay_request_invalid");
  const payload = { count, mode, budget, generation };
  if (selection) {
    const { method, categories, releaseIds, filter, fairBudget } = selection;
    if (!["sample", "ids", "shadow_divergences"].includes(method) || !["all", "without_poster", "without_identity", "with_identity"].includes(filter) ||
        !Array.isArray(categories) || categories.length < 1 || categories.length > 5 || categories.some(c => !replayCategories.includes(c)) || new Set(categories).size !== categories.length ||
        !Array.isArray(releaseIds) || releaseIds.length > 200 || releaseIds.some(id => !Number.isSafeInteger(id) || id < 1) || new Set(releaseIds).size !== releaseIds.length ||
        (method === "ids" ? releaseIds.length === 0 : releaseIds.length !== 0) || typeof fairBudget !== "boolean") throw new Error("replay_selection_invalid");
    payload.selection = { method, categories: [...categories], releaseIds: [...releaseIds], filter, fairBudget };
  }
  if (expectedIds !== undefined) {
    if (!Array.isArray(expectedIds) || expectedIds.length > 200 || expectedIds.some(id => !Number.isSafeInteger(id) || id < 1) || new Set(expectedIds).size !== expectedIds.length) throw new Error("replay_expected_ids_invalid");
    payload.expectedIds = [...expectedIds];
  }
  return payload;
}
export function replayDifference(value) {
  return value === true ? "oui" : value === false ? "non" : "inconnue";
}
