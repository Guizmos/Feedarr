/**
 * Constantes pour la bibliothèque
 */

export const VIEW_OPTIONS = [
  { value: "grid", label: "Cartes" },
  { value: "banner", label: "Banner" },
  { value: "list", label: "Liste" },
];

export const UNIFIED_CATEGORY_OPTIONS = [
  { key: "films", label: "Films" },
  { key: "series", label: "Series TV" },
  { key: "anime", label: "Anime" },
  { key: "games", label: "Jeux" },
  { key: "shows", label: "Emissions" },
  { key: "spectacle", label: "Spectacle" },
  { key: "audio", label: "Audio" },
  { key: "books", label: "Livres" },
  { key: "comics", label: "Comics" },
];

export const ARR_STATUS_TTL_MS = 10 * 60 * 1000;
export const ARR_STATUS_BATCH_SIZE = 50;
