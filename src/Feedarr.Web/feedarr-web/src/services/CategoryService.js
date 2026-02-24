const UNIFIED_LABELS = {
  films: "Films",
  series: "Series TV",
  anime: "Anime",
  games: "Jeux PC",
  spectacle: "Spectacle",
  shows: "Emissions",
  audio: "Audio",
  books: "Livres",
  comics: "Comics",
  other: "Autre",
};

const UNIFIED_PRIORITY = ["series", "anime", "films", "games", "spectacle", "shows", "audio", "books", "comics", "other"];

const EXCLUDED_TOKENS = [
  "application",
  "applications",
  "app",
  "apps",
  "appli",
  "applis",
  "software",
  "softwares",
  "mobile",
  "apk",
  "ipa",
  "exe",
  "msi",
  "dmg",
  "deb",
  "rpm",
  "iso",
  "crack",
  "keygen",
  "serial",
  "serials",
  "warez",
  "nulled",
  "firmware",
  "driver",
  "drivers",
  "android",
  "ios",
  "macos",
  "mac",
  "windows",
  "linux",
  "emulation",
  "emulator",
  "emulators",
  "emu",
  "gps",
  "garmin",
  "tomtom",
  "imprimante",
  "imprimantes",
  "printer",
  "printers",
  "console",
  "xbox",
  "ps4",
  "ps5",
  "playstation",
  "nintendo",
  "switch",
  "wallpaper",
  "wallpapers",
  "image",
  "images",
  "photo",
  "photos",
  "pic",
  "pics",
  "picture",
  "pictures",
  "porn",
  "porno",
  "erotic",
  "erotique",
  "hentai",
  "nsfw",
  "xxx",
  "adult",
  "sport",
  "sports",
  "misc",
  "other",
  "divers",
];

// DEPRECATED (unused): runtime category mapping is now backend-driven.
// Keep the export surface for compatibility, but neutralize legacy hardcoded maps.
const LEGACY_INDEXER_CATEGORY_MAP = {};

function normalizeIndexerKey(value) {
  return String(value || "")
    .toUpperCase()
    .replace(/[^A-Z0-9]/g, "");
}

function getLegacyMap(indexerName) {
  const key = normalizeIndexerKey(indexerName);
  return LEGACY_INDEXER_CATEGORY_MAP[key] || null;
}

function normalizeCategoryLabel(value) {
  return String(value || "")
    .toLowerCase()
    .normalize("NFD")
    .replace(/[\u0300-\u036f]/g, "")
    .replace(/[^a-z0-9]+/g, " ")
    .trim();
}

function tokenizeLabel(value) {
  const normalized = normalizeCategoryLabel(value);
  return new Set(normalized ? normalized.split(/\s+/).filter(Boolean) : []);
}

function hasAnyToken(tokens, list) {
  return list.some((t) => tokens.has(t));
}

function isBlacklistedTokens(tokens, ignoreTokens = []) {
  if (!tokens || tokens.size === 0) return false;
  if (!ignoreTokens || ignoreTokens.length === 0) {
    return hasAnyToken(tokens, EXCLUDED_TOKENS);
  }
  const ignore = new Set(ignoreTokens);
  return EXCLUDED_TOKENS.some((t) => !ignore.has(t) && tokens.has(t));
}

function isPcWindowsGameTokens(tokens) {
  if (!tokens || tokens.size === 0) return false;
  const hasPc = hasAnyToken(tokens, ["pc", "windows", "win32", "win64"]);
  const hasGame =
    hasAnyToken(tokens, ["game", "games", "jeu", "jeux", "gaming", "videogame", "videogames"]) ||
    (tokens.has("jeu") && tokens.has("video")) ||
    (tokens.has("jeux") && tokens.has("video"));
  return hasPc && hasGame;
}

function normalizeCategoryId(value) {
  if (!Number.isFinite(value)) return null;
  if (value >= 100000) return value % 100000;
  return value;
}

function classifyByTokens(tokens) {
  if (!tokens || tokens.size === 0) return null;
  const isPcGame = isPcWindowsGameTokens(tokens);
  if (isBlacklistedTokens(tokens, isPcGame ? ["windows", "win32", "win64"] : [])) return null;

  const scores = {
    series: 0,
    films: 0,
    anime: 0,
    games: 0,
    spectacle: 0,
    shows: 0,
    audio: 0,
    books: 0,
    comics: 0,
  };

  const hasSeriesToken = hasAnyToken(tokens, ["serie", "series", "tv", "tele"]);
  const hasAppOsToken = hasAnyToken(tokens, [
    "app",
    "apps",
    "application",
    "applications",
    "software",
    "softwares",
    "mobile",
    "android",
    "ios",
    "apk",
    "ipa",
    "exe",
    "msi",
    "dmg",
    "deb",
    "rpm",
    "iso",
    "firmware",
    "driver",
    "drivers",
    "windows",
    "linux",
    "macos",
    "mac",
  ]);
  if (hasSeriesToken && !hasAppOsToken) scores.series = 3;
  const hasFilmToken = hasAnyToken(tokens, ["film", "films", "movie", "movies", "cinema"]);
  const hasVideoToken = tokens.has("video");
  const hasSpectacleToken = hasAnyToken(tokens, [
    "spectacle",
    "concert",
    "opera",
    "theatre",
    "ballet",
    "symphonie",
    "orchestr",
    "philharmon",
    "ring",
    "choregraph",
    "danse",
  ]);

  if (hasAnyToken(tokens, ["anime", "animation"])) scores.anime = 4;
  if (hasAnyToken(tokens, ["audio", "music", "musique", "mp3", "flac", "wav", "aac", "m4a", "opus", "podcast", "audiobook", "audiobooks", "album", "albums", "soundtrack", "ost"])) scores.audio = 4;
  if (hasAnyToken(tokens, ["book", "books", "livre", "livres", "ebook", "ebooks", "epub", "mobi", "kindle", "isbn"])) scores.books = 4;
  if (hasAnyToken(tokens, ["comic", "comics", "bd", "manga", "scan", "scans", "graphic", "novel", "novels"])) scores.comics = 4;
  if (hasSpectacleToken) scores.spectacle = 4;
  if (hasAnyToken(tokens, [
    "emission",
    "show",
    "talk",
    "reality",
    "documentaire",
    "docu",
    "magazine",
    "reportage",
    "enquete",
    "quotidien",
    "quotidienne",
  ])) scores.shows = 4;

  const hasPc = hasAnyToken(tokens, ["pc", "windows", "win32", "win64"]);
  const hasGame = hasAnyToken(tokens, ["jeu", "jeux", "game", "games"]);
  const isGame = hasPc && hasGame;
  if (isGame) scores.games = 3;

  if (!hasSpectacleToken && (hasFilmToken || (hasVideoToken && !hasGame && !isGame))) {
    scores.films = 3;
  }

  const maxScore = Math.max(...Object.values(scores));
  if (maxScore < 3) return null;

  return UNIFIED_PRIORITY.find((key) => scores[key] === maxScore) || null;
}

function classifyById(id, tokens) {
  if (!Number.isFinite(id)) return null;

  if (id === 5070) return "anime";
  if (id >= 3000 && id < 4000) return "audio";
  if (id >= 2000 && id < 3000) return "films";
  if (id >= 5000 && id < 6000) return "series";
  if (id === 4050) return "games";
  if (id >= 7000 && id < 8000) {
    if (id >= 7030 && id < 7040) return "comics";
    return "books";
  }
  if (id >= 4000 && id < 5000) {
    if (hasAnyToken(tokens, ["jeu", "jeux", "game", "games", "pc", "windows"])) return "games";
  }

  return null;
}

export function parseCapsXmlToCategories(xml) {
  if (!xml || typeof xml !== "string") return [];
  try {
    const doc = new DOMParser().parseFromString(xml, "text/xml");
    if (doc.querySelector("parsererror")) return [];
    const results = [];
    const categories = Array.from(doc.querySelectorAll("category"));
    categories.forEach((cat) => {
      const id = Number(cat.getAttribute("id"));
      const name = (cat.getAttribute("name") || "").trim();
      if (Number.isFinite(id) && name) {
        results.push({ id, name, isSub: false, parentId: null });
        const subs = Array.from(cat.querySelectorAll("subcat"));
        subs.forEach((sub) => {
          const sid = Number(sub.getAttribute("id"));
          const sname = (sub.getAttribute("name") || "").trim();
          if (Number.isFinite(sid) && sname) {
            results.push({ id: sid, name: sname, isSub: true, parentId: id });
          }
        });
      }
    });
    return results;
  } catch {
    return [];
  }
}

export function legacyCategoriesForIndexer(indexerName) {
  const map = getLegacyMap(indexerName);
  if (!map) return [];
  return Object.entries(map).map(([rawId, unifiedKey]) => {
    const id = Number(rawId);
    return {
      id,
      name: `${UNIFIED_LABELS[unifiedKey] || unifiedKey} (${id})`,
      isSub: false,
      parentId: null,
      unifiedKey,
      unifiedLabel: UNIFIED_LABELS[unifiedKey] || unifiedKey,
    };
  });
}

export function decorateCategories(categories, context = {}) {
  const legacyMap = context.legacyMap || getLegacyMap(context.indexerName);

  return (Array.isArray(categories) ? categories : [])
    .map((cat) => {
      if (!cat) return null;
      const id = Number(cat.id);
      const name = String(cat.name || "").trim();
      if (!Number.isFinite(id) || !name) return null;

      const existingKey = String(cat.unifiedKey || "").trim().toLowerCase();
      if (existingKey) {
        return {
          ...cat,
          id,
          name,
          unifiedKey: existingKey,
          unifiedLabel: cat.unifiedLabel || UNIFIED_LABELS[existingKey] || existingKey,
        };
      }

      const tokens = tokenizeLabel(name);
      const normalizedId = normalizeCategoryId(id);
      const legacyKey = legacyMap ? (legacyMap[id] || legacyMap[normalizedId]) : null;
      const byId = classifyById(normalizedId, tokens);
      const byLabel = classifyByTokens(tokens);
      const unifiedKey = legacyKey || byId || byLabel || "other";

      return {
        ...cat,
        id,
        name,
        unifiedKey,
        unifiedLabel: UNIFIED_LABELS[unifiedKey] || unifiedKey,
      };
    })
    .filter(Boolean);
}

export { UNIFIED_LABELS, UNIFIED_PRIORITY };
