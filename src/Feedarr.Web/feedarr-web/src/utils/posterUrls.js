// Preserve the server/manual version in every responsive image candidate.
export function posterThumbUrls(item) {
  const base = item.id ? `/api/posters/release/${item.id}/thumb` : "";
  const url = String(item.posterUrl || "");
  const query = url.includes("?") ? url.slice(url.indexOf("?")) : item.posterUpdatedAtTs ? `?v=${encodeURIComponent(item.posterUpdatedAtTs)}` : "";
  const thumb = width => `${base}/${width}${query}`;
  return { src: base ? thumb(500) : url, srcSet: base ? `${thumb(342)} 342w, ${thumb(500)} 500w, ${thumb(780)} 780w` : undefined };
}
