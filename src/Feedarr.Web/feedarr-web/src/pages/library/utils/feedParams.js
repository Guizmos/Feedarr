export function buildFeedParams(query, seen, fetchLimit) {
  const params = new URLSearchParams();
  params.set("limit", String(fetchLimit));
  if (query?.trim()) params.set("q", query.trim());
  if (seen) params.set("seen", seen);
  return params;
}
