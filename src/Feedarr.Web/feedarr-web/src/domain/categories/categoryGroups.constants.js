const removeDiacritics = (value) =>
  String(value || "")
    .normalize("NFD")
    .replace(/[\u0300-\u036f]/g, "");

const normalizeToken = (value) =>
  removeDiacritics(value)
    .trim()
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, "");

export const CATEGORY_GROUPS = [
  { key: "films", label: "Films" },
  { key: "series", label: "Série TV" },
  { key: "animation", label: "Animation" },
  { key: "anime", label: "Anime" },
  { key: "games", label: "Jeux Vidéo" },
  { key: "comics", label: "Comics" },
  { key: "books", label: "Livres" },
  { key: "audio", label: "Audio" },
  { key: "spectacle", label: "Spectacle" },
  { key: "emissions", label: "Émissions" },
];

export const CATEGORY_GROUP_KEYS = new Set(CATEGORY_GROUPS.map((group) => group.key));

export const CATEGORY_GROUP_LABELS = Object.fromEntries(
  CATEGORY_GROUPS.map((group) => [group.key, group.label])
);

export const CATEGORY_GROUP_ALIASES = {
  shows: "emissions",
  show: "emissions",
  emission: "emissions",
  emissions: "emissions",
  emissionstv: "emissions",
  tv: "series",
  serie: "series",
  series: "series",
  tvseries: "series",
  seriestv: "series",
  serietv: "series",
  movie: "films",
  movies: "films",
  film: "films",
  films: "films",
  game: "games",
  games: "games",
  jeuxvideo: "games",
  jeuvideo: "games",
  book: "books",
  books: "books",
  comic: "comics",
  comics: "comics",
  other: null,
};

const CANONICAL_BY_NORMALIZED = Object.fromEntries([
  ...CATEGORY_GROUPS.map((group) => [normalizeToken(group.key), group.key]),
  ...CATEGORY_GROUPS.map((group) => [normalizeToken(group.label), group.key]),
]);

const ALIAS_BY_NORMALIZED = Object.fromEntries(
  Object.entries(CATEGORY_GROUP_ALIASES).map(([alias, canonical]) => [normalizeToken(alias), canonical || null])
);

export function normalizeCategoryGroupKey(value) {
  const normalized = normalizeToken(value);
  if (!normalized) return null;

  if (Object.prototype.hasOwnProperty.call(CANONICAL_BY_NORMALIZED, normalized)) {
    return CANONICAL_BY_NORMALIZED[normalized] || null;
  }

  if (Object.prototype.hasOwnProperty.call(ALIAS_BY_NORMALIZED, normalized)) {
    return ALIAS_BY_NORMALIZED[normalized] || null;
  }

  return null;
}

export function isCanonicalGroupKey(key) {
  const normalized = normalizeCategoryGroupKey(key);
  return !!normalized && CATEGORY_GROUP_KEYS.has(normalized);
}

export function assertCanonicalKey(key, context = "") {
  const isDev = !!(typeof import.meta !== "undefined" && import.meta?.env?.DEV);
  if (!isDev) return;

  const raw = String(key ?? "").trim();
  if (!raw) return;

  if (normalizeCategoryGroupKey(raw)) return;

  const suffix = context ? ` (${context})` : "";
  throw new Error(`[categories] Unsupported category key "${raw}"${suffix}`);
}
