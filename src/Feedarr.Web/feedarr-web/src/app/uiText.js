import { getActiveUiLanguage } from "./locale.js";

function getLanguageCode(uiLanguage) {
  const raw = String(uiLanguage || "").trim().toLowerCase();
  if (raw.startsWith("en")) return "en";
  return "fr";
}

export function tr(fr, en, uiLanguage = getActiveUiLanguage()) {
  return getLanguageCode(uiLanguage) === "en" ? en : fr;
}

