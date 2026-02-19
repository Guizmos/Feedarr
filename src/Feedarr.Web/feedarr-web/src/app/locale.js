const UI_LANGUAGE_KEY = "feedarr-ui-language";

export const DEFAULT_UI_LANGUAGE = "fr-FR";
export const DEFAULT_MEDIA_INFO_LANGUAGE = "fr-FR";

export const LANGUAGE_OPTIONS = [
  { value: "fr-FR", label: "Français" },
  { value: "en-US", label: "English" },
];

const SUPPORTED_LANGUAGE_VALUES = new Set(LANGUAGE_OPTIONS.map((option) => option.value.toLowerCase()));

export function normalizeUiLanguage(value) {
  const raw = String(value || "").trim();
  if (!raw) return DEFAULT_UI_LANGUAGE;
  return SUPPORTED_LANGUAGE_VALUES.has(raw.toLowerCase()) ? raw : DEFAULT_UI_LANGUAGE;
}

export function normalizeMediaInfoLanguage(value) {
  const raw = String(value || "").trim();
  if (!raw) return DEFAULT_MEDIA_INFO_LANGUAGE;
  return SUPPORTED_LANGUAGE_VALUES.has(raw.toLowerCase()) ? raw : DEFAULT_MEDIA_INFO_LANGUAGE;
}

export function getStoredUiLanguage() {
  if (typeof localStorage === "undefined") return DEFAULT_UI_LANGUAGE;
  return normalizeUiLanguage(localStorage.getItem(UI_LANGUAGE_KEY));
}

export function getActiveUiLanguage() {
  if (typeof document !== "undefined") {
    const docLang = String(document.documentElement.getAttribute("lang") || "").trim();
    if (docLang) return normalizeUiLanguage(docLang);
  }
  return getStoredUiLanguage();
}

export function applyUiLanguage(value, persist = false) {
  const normalized = normalizeUiLanguage(value);

  if (typeof document !== "undefined") {
    document.documentElement.setAttribute("lang", normalized);
  }

  if (persist && typeof localStorage !== "undefined") {
    localStorage.setItem(UI_LANGUAGE_KEY, normalized);
  }

  if (typeof window !== "undefined") {
    window.dispatchEvent(
      new CustomEvent("feedarr:ui-language-changed", {
        detail: { language: normalized },
      })
    );
  }

  return normalized;
}
