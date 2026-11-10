// Keep source identity with each settled result. Never surface raw upstream errors/URLs.
export async function loadSourceFeeds(sources, params, { signal, fetchFeed }) {
  const settled = await Promise.allSettled(sources.map(async source => {
    const id = Number(source.id ?? source.sourceId);
    if (!Number.isSafeInteger(id) || id <= 0) throw new Error("Invalid source identity");
    const data = await fetchFeed(`/api/feed/${id}?${params.toString()}`, { signal });
    if (!Array.isArray(data)) throw new Error("Invalid feed response");
    return data.map(item => ({ ...item, sourceId: item.sourceId ?? id }));
  }));
  // Only the shared navigation/search signal makes an abort voluntary.
  if (signal.aborted) throw signal.reason;
  const items = [];
  const failedSourceIds = [];
  settled.forEach((result, index) => {
    if (result.status === "fulfilled") items.push(...result.value);
    else {
      const id = Number(sources[index].id ?? sources[index].sourceId);
      failedSourceIds.push(Number.isSafeInteger(id) && id > 0 ? id : null);
    }
  });
  const outcome = failedSourceIds.length === 0 ? "success"
    : failedSourceIds.length === sources.length ? "error" : "partial";
  return { items, failedSourceIds, outcome };
}

export function sourceFailureMessage(ids, total = false) {
  const known = ids.filter(id => Number.isSafeInteger(id) && id > 0);
  const identity = known.length ? ` (sources : ${known.join(", ")})` : "";
  return total
    ? `Impossible de charger les releases : toutes les sources ont échoué${identity}. Réessayez.`
    : `${ids.length} source(s) indisponible(s)${identity}. Les résultats disponibles restent affichés.`;
}
