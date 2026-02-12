import React, { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { apiGet } from "../../api/client.js";
import { ArrowRight } from "lucide-react";

const STORAGE_PROVIDER_CONFIGURED = {
  jackett: "feedarr:jackettConfigured",
  prowlarr: "feedarr:prowlarrConfigured",
};

function ScrollText({ children }) {
  const textRef = useRef(null);
  const wrapperRef = useRef(null);
  const [isOverflowing, setIsOverflowing] = useState(false);
  const [scrollDistance, setScrollDistance] = useState(0);

  useEffect(() => {
    const checkOverflow = () => {
      if (textRef.current && wrapperRef.current) {
        const textWidth = textRef.current.scrollWidth;
        const wrapperWidth = wrapperRef.current.clientWidth;
        const overflow = textWidth > wrapperWidth;
        setIsOverflowing(overflow);
        if (overflow) {
          setScrollDistance(-(textWidth - wrapperWidth + 10));
        }
      }
    };
    checkOverflow();
    window.addEventListener("resize", checkOverflow);
    return () => window.removeEventListener("resize", checkOverflow);
  }, [children]);

  return (
    <div className="text-scroll-wrapper" ref={wrapperRef}>
      <span
        className={`text-scroll-content${isOverflowing ? " is-overflowing" : ""}`}
        ref={textRef}
        style={isOverflowing ? { "--scroll-distance": `${scrollDistance}px` } : undefined}
      >
        {children}
      </span>
    </div>
  );
}

function maskUrl(url) {
  if (!url) return "—";
  try {
    const u = new URL(url);
    const host = u.host;
    return `${u.protocol}//${host}/•••`;
  } catch {
    if (url.length <= 8) return "•••";
    return `${url.slice(0, 8)}•••`;
  }
}

function labelForProvider(key) {
  if (key === "tmdb") return "TMDB";
  if (key === "tvmaze") return "TVmaze";
  if (key === "fanart") return "Fanart";
  if (key === "igdb") return "IGDB";
  return key;
}

function labelForIndexerProvider(key) {
  if (key === "prowlarr") return "Prowlarr";
  return "Jackett";
}

export default function Step5Summary({ onFinish, finishing }) {
  const [loading, setLoading] = useState(true);
  const [providers, setProviders] = useState([]);
  const [indexerProviders, setIndexerProviders] = useState([]);
  const [sources, setSources] = useState([]);
  const [categoriesBySource, setCategoriesBySource] = useState({});
  const [apps, setApps] = useState([]);
  const [error, setError] = useState("");
  const [launchError, setLaunchError] = useState("");

  const loadAll = useCallback(async () => {
    setLoading(true);
    setError("");
    try {
      const [ext, providerList, srcList, appsList] = await Promise.all([
        apiGet("/api/settings/external"),
        apiGet("/api/providers"),
        apiGet("/api/sources"),
        apiGet("/api/apps"),
      ]);

      const providerRows = [
        { key: "tmdb", configured: !!ext?.hasTmdbApiKey, enabled: ext?.tmdbEnabled !== false },
        { key: "tvmaze", configured: !!ext?.hasTvmazeApiKey || !!ext?.tvmazeEnabled, enabled: ext?.tvmazeEnabled !== false },
        { key: "fanart", configured: !!ext?.hasFanartApiKey, enabled: ext?.fanartEnabled !== false },
        { key: "igdb", configured: !!ext?.hasIgdbClientId && !!ext?.hasIgdbClientSecret, enabled: ext?.igdbEnabled !== false },
      ];
      setProviders(providerRows);
      setIndexerProviders(Array.isArray(providerList) ? providerList : []);

      const sourcesList = Array.isArray(srcList) ? srcList : [];
      setSources(sourcesList);

      const categoriesMap = {};
      await Promise.all(
        sourcesList.map(async (s) => {
          if (!s?.id) return;
          try {
            const cats = await apiGet(`/api/categories/${s.id}`);
            categoriesMap[s.id] = Array.isArray(cats) ? cats : [];
          } catch {
            categoriesMap[s.id] = [];
          }
        })
      );
      setCategoriesBySource(categoriesMap);

      setApps(Array.isArray(appsList) ? appsList : []);
    } catch (e) {
      setError(e?.message || "Erreur chargement résumé");
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    loadAll();
  }, [loadAll]);

  const indexerProviderRows = useMemo(() => {
    const byType = new Map(
      (Array.isArray(indexerProviders) ? indexerProviders : []).map((p) => [
        String(p?.type || "").toLowerCase(),
        p,
      ])
    );
    return ["jackett", "prowlarr"].map((type) => {
      const row = byType.get(type);
      const hasApiKey = !!row?.hasApiKey;
      const hasBaseUrl = !!String(row?.baseUrl || "").trim();
      const enabled = !!row?.enabled;
      return {
        type,
        configured: hasApiKey && hasBaseUrl && enabled,
        maskedUrl: maskUrl(row?.baseUrl || ""),
      };
    });
  }, [indexerProviders]);

  const handleFinish = useCallback(async () => {
    if (finishing) return;
    setLaunchError("");

    const expectedTypes = [];
    if (typeof window !== "undefined") {
      if (window.localStorage.getItem(STORAGE_PROVIDER_CONFIGURED.jackett) === "true") {
        expectedTypes.push("jackett");
      }
      if (window.localStorage.getItem(STORAGE_PROVIDER_CONFIGURED.prowlarr) === "true") {
        expectedTypes.push("prowlarr");
      }
    }

    if (expectedTypes.length > 0) {
      try {
        const list = await apiGet("/api/providers");
        const types = new Set((Array.isArray(list) ? list : []).map((p) => String(p?.type || "").toLowerCase()));
        const missing = expectedTypes.filter((type) => !types.has(type));
        if (missing.length > 0) {
          const labels = missing.map((type) => labelForIndexerProvider(type)).join(", ");
          setLaunchError(`Vérification échouée: fournisseur manquant en API (${labels}).`);
          return;
        }
      } catch (e) {
        setLaunchError(e?.message || "Impossible de vérifier les fournisseurs avant lancement.");
        return;
      }
    }

    onFinish?.();
  }, [finishing, onFinish]);

  return (
    <div className="setup-step setup-summary">
      <div className={`launch-transition-overlay${finishing ? " is-active" : ""}`} />
      <div className="setup-summary__header">
        <div>
          <h2>Résumé</h2>
          <p>Vérifie la configuration puis lance Feedarr.</p>
        </div>
      </div>

      {error && <div className="onboarding__error">{error}</div>}
      {launchError && <div className="onboarding__error">{launchError}</div>}

      {loading ? (
        <div className="muted">Chargement...</div>
      ) : (
        <div className="setup-summary__grid">
          <div className="setup-summary__card">
            <h3>Providers</h3>
            <ul>
              {providers.map((p) => (
                <li key={p.key}>
                  <ScrollText>{labelForProvider(p.key)}</ScrollText>
                  <span className={`pill ${p.configured && p.enabled ? "pill-ok" : "pill-warn"}`}>
                    {p.configured && p.enabled ? "Configuré" : "Non configuré"}
                  </span>
                </li>
              ))}
            </ul>
          </div>

          <div className="setup-summary__card">
            <h3>Fournisseurs</h3>
            <ul>
              {indexerProviderRows.map((provider) => (
                <li key={provider.type}>
                  <ScrollText>{labelForIndexerProvider(provider.type)}</ScrollText>
                  <span className={`pill ${provider.configured ? "pill-ok" : "pill-warn"}`}>
                    {provider.configured ? "Configuré" : "Non configuré"}
                  </span>
                </li>
              ))}
            </ul>
            {indexerProviderRows
              .filter((provider) => !provider.configured && provider.maskedUrl !== "—")
              .map((provider) => (
                <div key={`url-${provider.type}`} className="muted">
                  {labelForIndexerProvider(provider.type)}: {provider.maskedUrl}
                </div>
              ))}
          </div>

          <div className="setup-summary__card">
            <h3>Sources</h3>
            {sources.length === 0 ? (
              <div className="muted">Aucune source ajoutée</div>
            ) : (
              <ul>
                {sources.map((s) => {
                  const cats = categoriesBySource[s.id] || [];
                  return (
                    <li key={s.id}>
                      <ScrollText>{s.name}</ScrollText>
                      <span className="pill pill-blue">
                        {cats.length} catégorie{cats.length > 1 ? "s" : ""}
                      </span>
                    </li>
                  );
                })}
              </ul>
            )}
          </div>

          <div className="setup-summary__card">
            <h3>Applications</h3>
            {apps.length === 0 ? (
              <div className="muted">Aucune application</div>
            ) : (
              <ul>
                {apps.map((a) => (
                  <li key={a.id}>
                    <ScrollText>{a.name || (a.type === "sonarr" ? "Sonarr" : "Radarr")}</ScrollText>
                    <span className="pill pill-ok">Configuré</span>
                  </li>
                ))}
              </ul>
            )}
          </div>
        </div>
      )}

      <div className="setup-summary__cta">
        <button
          className={`btn-launch${finishing ? " is-launching" : ""}`}
          type="button"
          onClick={handleFinish}
          disabled={finishing}
        >
          {finishing ? "Lancement..." : "Lancer Feedarr"}
          <ArrowRight className="launch-icon" />
        </button>
      </div>
    </div>
  );
}
