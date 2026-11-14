export function buildFeedParams(filters, now = Date.now()) {
  const params = new URLSearchParams();
  const list = filters.viewMode === "list" || filters.viewMode === "banner";
  const sort = list ? filters.listSortBy : filters.sortBy;
  params.set("sort", sort === "downloads" ? "grabs" : sort || "date");
  params.set("direction", list ? filters.listSortDir || "desc" : "desc");
  params.set("categorized", "true");
  if (filters.q?.trim()) params.set("q", filters.q.trim());
  if (filters.seen) params.set("seen", filters.seen);
  if (Number(filters.sourceId) > 0) params.append("sourceId", filters.sourceId);
  if (filters.categoryId) params.set("unifiedCategory", filters.categoryId);
  if (filters.quality) params.set("quality", filters.quality);
  if (Number(filters.maxAgeDays) > 0)
    params.set("publishedAfter", String(Math.floor(now / 1000) - Number(filters.maxAgeDays) * 86400));
  if (filters.viewMode === "missing") params.set("missingPoster", "true");
  return params;
}

// One display window only. Rejected application matches are discarded as we scan;
// never accumulate the catalog or silently stop at a per-source cutoff.
export async function loadLibraryPage(params, { cursor = null, limit = 200, signal, fetchFeed, selectItems }) {
  const items = [];
  let facets = null;
  let nextCursor = cursor;
  do {
    signal?.throwIfAborted();
    const query = new URLSearchParams(params);
    query.set("limit", String(selectItems ? 200 : Math.min(200, limit - items.length)));
    if (nextCursor) query.set("cursor", nextCursor);
    let data = await fetchFeed(`/api/feed/releases?${query}`, { signal });
    if (!Array.isArray(data?.items) || !data.pagination ||
        data.items.length > Number(query.get("limit")) ||
        (data.pagination.hasMore && (!data.pagination.nextCursor || data.pagination.nextCursor === nextCursor)))
      throw new Error("Invalid paged feed response");
    facets ??= data.facets;
    const selected = selectItems ? await selectItems(data.items, signal) : data.items;
    const remaining = limit - items.length;
    if (selected.length > remaining) {
      // Re-read just the consumed prefix to obtain its server-owned cursor.
      // Sparse filters still scan 200 candidates per request, even with one slot left.
      const lastId = selected[remaining - 1].id;
      query.set("limit", String(data.items.findIndex(item => item.id === lastId) + 1));
      const prefix = await fetchFeed(`/api/feed/releases?${query}`, { signal });
      if (prefix?.items?.at(-1)?.id !== lastId || !prefix.pagination ||
          prefix.pagination.snapshotMaxId !== data.pagination.snapshotMaxId)
        throw new Error("Catalog changed while paging; refresh results");
      data = prefix;
    }
    items.push(...selected.slice(0, remaining));
    nextCursor = data.pagination.hasMore ? data.pagination.nextCursor : null;
  } while (nextCursor && items.length < limit);
  return { items, facets, nextCursor };
}
