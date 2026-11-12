export function replayPayload(count, mode, budget, generation) {
  if (![10, 25, 50, 100, 200].includes(count) || !["offline", "tmdb"].includes(mode) ||
      !Number.isInteger(budget) || budget < 1 || budget > 30 || !Number.isInteger(generation) || generation < 0) throw new Error("replay_request_invalid");
  return { count, mode, budget, generation };
}
export function replayDifference(value) {
  return value === true ? "oui" : value === false ? "non" : "inconnue";
}
