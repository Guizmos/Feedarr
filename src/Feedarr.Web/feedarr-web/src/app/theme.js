const THEME_KEY = 'feedarr-theme';

let systemMql = null;
let systemListener = null;

function normalizeTheme(theme) {
  const value = String(theme || "system").trim().toLowerCase();
  if (value === "light" || value === "dark" || value === "system") return value;
  return "system";
}

export function getStoredTheme() {
  if (typeof localStorage === "undefined") return "system";
  return localStorage.getItem(THEME_KEY) || "system";
}

export function applyTheme(theme, persist = false) {
  if (typeof document === "undefined") return;
  const root = document.documentElement;
  const normalized = normalizeTheme(theme);

  // Persist to localStorage if requested
  if (persist && typeof localStorage !== "undefined") {
    localStorage.setItem(THEME_KEY, normalized);
  }

  if (normalized === "system") {
    const mql = typeof window !== "undefined" && window.matchMedia
      ? window.matchMedia("(prefers-color-scheme: dark)")
      : null;

    if (mql) {
      const setFromMatches = (matches) => {
        root.setAttribute("data-theme", matches ? "dark" : "light");
      };
      setFromMatches(mql.matches);

      if (systemMql !== mql || !systemListener) {
        if (systemMql && systemListener) {
          systemMql.removeEventListener("change", systemListener);
        }
        systemMql = mql;
        systemListener = (e) => setFromMatches(e.matches);
        mql.addEventListener("change", systemListener);
      }
    } else {
      root.setAttribute("data-theme", "light");
    }
    return;
  }

  root.setAttribute("data-theme", normalized);
  if (systemMql && systemListener) {
    systemMql.removeEventListener("change", systemListener);
    systemMql = null;
    systemListener = null;
  }
}

// Initialize theme on module load (for SPA navigation)
if (typeof document !== "undefined") {
  const stored = getStoredTheme();
  applyTheme(stored);
}
