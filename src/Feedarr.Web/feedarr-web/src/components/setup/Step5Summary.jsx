import React, { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { apiGet } from "../../api/client.js";
import { ArrowRight } from "lucide-react";
import { getAppLabel } from "../../utils/appTypes.js";
import { tr } from "../../app/uiText.js";

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

function labelForIndexerProvider(key) {
  if (key === "prowlarr") return "Prowlarr";
  return "Jackett";
}

function toHasFlagName(fieldKey) {
  if (!fieldKey) return "";
  const trimmed = String(fieldKey).trim();
  if (!trimmed) return "";
  return `has${trimmed.charAt(0).toUpperCase()}${trimmed.slice(1)}`;
}

function hasRequiredAuth(instance, definition) {
  if (!definition) return false;
  const requiredFields = (definition.fieldsSchema || []).filter((field) => field.required);
  if (requiredFields.length === 0) return true;
  const authFlags = instance?.authFlags || {};
  return requiredFields.every((field) => !!authFlags[toHasFlagName(field.key)]);
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
      const [externalProviders, providerList, srcList, appsList] = await Promise.all([
        apiGet("/api/providers/external"),
        apiGet("/api/providers"),
        apiGet("/api/sources"),
        apiGet("/api/apps"),
      ]);

      const definitions = Array.isArray(externalProviders?.definitions) ? externalProviders.definitions : [];
      const definitionByKey = new Map(
        definitions.map((definition) => [String(definition?.providerKey || "").toLowerCase(), definition])
      );
      const providerRows = (Array.isArray(externalProviders?.instances) ? externalProviders.instances : []).map((instance) => {
        const providerKey = String(instance?.providerKey || "").toLowerCase();
        const definition = definitionByKey.get(providerKey);
        const configured = hasRequiredAuth(instance, definition);
        return {
          key: String(instance?.instanceId || providerKey),
          label: instance?.displayName || definition?.displayName || providerKey || tr("Provider", "Provider"),
          configured,
          enabled: instance?.enabled !== false,
        };
      });
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
      setError(e?.message || tr("Erreur chargement resume", "Summary loading error"));
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
  const configuredIndexerProviderRows = useMemo(
    () => indexerProviderRows.filter((provider) => provider.configured),
    [indexerProviderRows]
  );

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
          setLaunchError(
            tr(
              `Verification echouee: fournisseur manquant en API (${labels}).`,
              `Verification failed: missing provider in API (${labels}).`
            )
          );
          return;
        }
      } catch (e) {
        setLaunchError(e?.message || tr("Impossible de verifier les fournisseurs avant lancement.", "Unable to verify providers before launch."));
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
          <h2>{tr("Resume", "Summary")}</h2>
          <p>{tr("Verifie la configuration puis lance Feedarr.", "Check the configuration then launch Feedarr.")}</p>
        </div>
      </div>

      {error && <div className="onboarding__error">{error}</div>}
      {launchError && <div className="onboarding__error">{launchError}</div>}

      {loading ? (
        <div className="muted">{tr("Chargement...", "Loading...")}</div>
      ) : (
        <div className="setup-summary__grid">
          <div className="setup-summary__card">
            <h3>{tr("Metadonnees", "Metadata")}</h3>
            <ul>
              {providers.map((p) => (
                <li key={p.key}>
                  <ScrollText>{p.label || p.key || tr("Provider", "Provider")}</ScrollText>
                  <span className={`pill ${p.enabled ? "pill-ok" : "pill-warn"}`}>
                    {p.enabled ? tr("Actif", "Active") : tr("Inactif", "Inactive")}
                  </span>
                  <span className={`pill ${p.configured ? "pill-ok" : "pill-warn"}`}>
                    {p.configured ? tr("Auth OK", "Auth OK") : tr("Auth manquante", "Auth missing")}
                  </span>
                </li>
              ))}
            </ul>
          </div>

          <div className="setup-summary__card">
            <h3>{tr("Fournisseurs", "Providers")}</h3>
            {configuredIndexerProviderRows.length === 0 ? (
              <div className="muted">{tr("Aucun fournisseur configure", "No configured provider")}</div>
            ) : (
              <ul>
                {configuredIndexerProviderRows.map((provider) => (
                  <li key={provider.type}>
                    <ScrollText>{labelForIndexerProvider(provider.type)}</ScrollText>
                    <span className="pill pill-ok">{tr("Configure", "Configured")}</span>
                  </li>
                ))}
              </ul>
            )}
          </div>

          <div className="setup-summary__card">
            <h3>{tr("Sources", "Sources")}</h3>
            {sources.length === 0 ? (
              <div className="muted">{tr("Aucune source ajoutee", "No source added")}</div>
            ) : (
              <ul>
                {sources.map((s) => {
                  const cats = categoriesBySource[s.id] || [];
                  return (
                    <li key={s.id}>
                      <ScrollText>{s.name}</ScrollText>
                      <span className="pill pill-blue">
                        {cats.length} {tr("categorie", "category")}{cats.length > 1 ? "s" : ""}
                      </span>
                    </li>
                  );
                })}
              </ul>
            )}
          </div>

          <div className="setup-summary__card">
            <h3>{tr("Applications", "Applications")}</h3>
            {apps.length === 0 ? (
              <div className="muted">{tr("Aucune application", "No application")}</div>
            ) : (
              <ul>
                {apps.map((a) => (
                  <li key={a.id}>
                    <ScrollText>{a.name || getAppLabel(a.type)}</ScrollText>
                    <span className="pill pill-ok">{tr("Configure", "Configured")}</span>
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
          {finishing ? tr("Lancement...", "Launching...") : tr("Lancer Feedarr", "Launch Feedarr")}
          <ArrowRight className="launch-icon" />
        </button>
      </div>
    </div>
  );
}
