import React, { useEffect, useMemo, useState, useCallback } from 'react';
import { apiGet } from '../../api/client.js';
import { fmtUptime, fmtBytes } from './systemUtils.js';
import { getActiveUiLanguage } from '../../app/locale.js';
import { tr } from '../../app/uiText.js';
import { CATEGORY_GROUP_LABELS, normalizeCategoryGroupKey } from '../../domain/categories/index.js';

/* ─────────────────────── Helpers ─────────────────────── */

function formatNumber(num) {
  if (num >= 1000000) return (num / 1000000).toFixed(1) + 'M';
  if (num >= 1000) return (num / 1000).toFixed(num >= 10000 ? 0 : 1) + 'K';
  return num.toString();
}

/* ─────────────────────── Category mapping ─────────────────────── */

const CATEGORY_COLOR_FALLBACK = "var(--cat-stats-unknown)";

const cssVarCache = new Map();

function getCssVar(name) {
  if (cssVarCache.has(name)) return cssVarCache.get(name);
  const value = getComputedStyle(document.documentElement)
    .getPropertyValue(name)
    .trim();
  cssVarCache.set(name, value);
  return value;
}

function getCategoryColor(key) {
  const normalized = String(key || "").trim().toLowerCase();
  if (!normalized) return CATEGORY_COLOR_FALLBACK;
  if (typeof window === "undefined" || typeof document === "undefined") {
    return `var(--cat-${normalized})`;
  }
  const value = getCssVar(`--cat-${normalized}`);
  return value || CATEGORY_COLOR_FALLBACK;
}

const MAIN_CATEGORY_FALLBACKS = {
  1: { tokenKey: "stats-games", name: "Console" },
  2: { tokenKey: "stats-films", name: "Films" },
  3: { tokenKey: "stats-audio", name: "Audio" },
  4: { tokenKey: "stats-pc", name: "PC" },
  5: { tokenKey: "stats-series", name: "Séries" },
  6: { tokenKey: "stats-xxx", name: "XXX" },
  7: { tokenKey: "stats-books", name: "Livres" },
  8: { tokenKey: "stats-other", name: "Autre" },
};

const CATEGORY_ID_OVERRIDES = {
  2145: { tokenKey: "stats-animation", name: "Animation" },
  2185: { tokenKey: "stats-animation", name: "Animation" },
  5070: { tokenKey: "stats-animation", name: "Anime" },
  2182: { tokenKey: "stats-emissions", name: "Émissions" },
  5080: { tokenKey: "stats-emissions", name: "Émissions" },
  2090: { tokenKey: "stats-spectacle", name: "Spectacle" },
  2190: { tokenKey: "stats-spectacle", name: "Spectacle" },
  2178: { tokenKey: "stats-documentaire", name: "Documentaire" },
  2070: { tokenKey: "stats-documentaire", name: "Documentaire" },
  2181: { tokenKey: "stats-sport", name: "Sport" },
  5060: { tokenKey: "stats-sport", name: "Sport" },
  2188: { tokenKey: "stats-pc", name: "Clip" },
  2189: { tokenKey: "stats-other", name: "Autre Vidéo" },
};

const UNIFIED_CATEGORY_INFO = {
  films: { tokenKey: "stats-films", name: CATEGORY_GROUP_LABELS.films || "Films" },
  series: { tokenKey: "stats-series", name: CATEGORY_GROUP_LABELS.series || "Série TV" },
  emissions: { tokenKey: "stats-emissions", name: CATEGORY_GROUP_LABELS.emissions || "Émissions" },
  games: { tokenKey: "stats-pc", name: CATEGORY_GROUP_LABELS.games || "Jeux Vidéo" },
  comics: { tokenKey: "stats-books", name: CATEGORY_GROUP_LABELS.comics || "Comics" },
  books: { tokenKey: "stats-books", name: CATEGORY_GROUP_LABELS.books || "Livres" },
  audio: { tokenKey: "stats-audio", name: CATEGORY_GROUP_LABELS.audio || "Audio" },
  anime: { tokenKey: "stats-animation", name: CATEGORY_GROUP_LABELS.anime || "Anime" },
  spectacle: { tokenKey: "stats-spectacle", name: CATEGORY_GROUP_LABELS.spectacle || "Spectacle" },
  animation: { tokenKey: "stats-animation", name: CATEGORY_GROUP_LABELS.animation || "Animation" },
  jeuwindows: { tokenKey: "stats-pc", name: "PC Games" },
  autre: { tokenKey: "stats-unknown", name: "Autre" },
};

const STATS_TOKEN_BY_CANONICAL_CATEGORY = {
  films: "stats-films",
  series: "stats-series",
  animation: "stats-animation",
  anime: "stats-animation",
  games: "stats-pc",
  comics: "stats-books",
  books: "stats-books",
  audio: "stats-audio",
  spectacle: "stats-spectacle",
  emissions: "stats-emissions",
};

function getCategoryInfoById(categoryId) {
  if (!categoryId) return { color: getCategoryColor("stats-unknown"), name: "Autre" };
  let normalizedId = categoryId;
  if (categoryId >= 100000) normalizedId = categoryId % 100000;

  const override = CATEGORY_ID_OVERRIDES[normalizedId];
  if (override) {
    return { color: getCategoryColor(override.tokenKey), name: override.name };
  }

  const mainCatDigit = Math.floor(normalizedId / 1000);
  const fallback = MAIN_CATEGORY_FALLBACKS[mainCatDigit];
  if (fallback) {
    return { color: getCategoryColor(fallback.tokenKey), name: fallback.name };
  }

  return { color: getCategoryColor("stats-unknown"), name: "Autre" };
}

function getCategoryInfoForStats(categoryId, unifiedCategory) {
  const canonicalKey = normalizeCategoryGroupKey(unifiedCategory);
  if (canonicalKey) {
    const fromUnified = UNIFIED_CATEGORY_INFO[canonicalKey];
    const tokenKey = fromUnified?.tokenKey || STATS_TOKEN_BY_CANONICAL_CATEGORY[canonicalKey] || "stats-unknown";
    const name = CATEGORY_GROUP_LABELS[canonicalKey] || fromUnified?.name || canonicalKey;
    return {
      color: getCategoryColor(tokenKey),
      name,
    };
  }

  const legacyKey = String(unifiedCategory || "").trim().toLowerCase();
  if (legacyKey && UNIFIED_CATEGORY_INFO[legacyKey]) {
    const legacy = UNIFIED_CATEGORY_INFO[legacyKey];
    return { color: getCategoryColor(legacy.tokenKey), name: legacy.name };
  }
  return getCategoryInfoById(categoryId);
}

const CATEGORY_ORDER = [
  "Films", "Séries", "Émissions", "Spectacle", "Documentaire", "Animation",
  "Anime", "Sport", "Musique", "Audio", "Jeux Vidéo", "PC Games", "Console",
  "Livres", "BD", "Mangas", "Comics", "XXX", "Autre"
];

/* ─────────────────────── Chart components ─────────────────────── */

function HorizontalBarChart({
  data, valueKey, labelKey, secondaryKey,
  color = "#5cb3ff", secondaryColor = "#f0c54d",
  height = 280, showLegend = false, legendLabels = [],
  barGap = 16, barMaxWidth, stretchBars = false,
  valueFormatter = formatNumber,
}) {
  const maxValue = Math.max(...data.map(d => Math.max(d[valueKey] || 0, d[secondaryKey] || 0)), 1);
  const columnCount = Math.max(1, data.length);
  const columnWidth = stretchBars && columnCount > 0
    ? `calc((100% - ${Math.max(0, columnCount - 1) * barGap}px) / ${columnCount})` : undefined;
  const justifyContent = stretchBars ? "space-between" : "flex-start";

  return (
    <div style={{ height, width: "100%", display: "flex", flexDirection: "column" }}>
      {showLegend && legendLabels.length > 0 && (
        <div style={{ display: "flex", justifyContent: "center", gap: 24, marginBottom: 12, fontSize: 12 }}>
          {legendLabels.map((label, i) => (
            <div key={i} style={{ display: "flex", alignItems: "center", gap: 6 }}>
              <div style={{ width: 16, height: 12, backgroundColor: i === 0 ? color : secondaryColor, borderRadius: 2 }} />
              <span>{label}</span>
            </div>
          ))}
        </div>
      )}
      <div style={{ flex: 1, width: "100%", display: "flex", alignItems: "stretch", gap: barGap, justifyContent }}>
        {data.map((item, i) => {
          const value = item[valueKey] || 0;
          const secondary = secondaryKey ? (item[secondaryKey] || 0) : 0;
          const percentage = (value / maxValue) * 100;
          const secondaryPercentage = secondary > 0 ? (secondary / maxValue) * 100 : 0;
          return (
            <div key={i} style={{ flex: stretchBars ? `0 0 ${columnWidth}` : 1, width: stretchBars ? columnWidth : "100%", display: "flex", flexDirection: "column", alignItems: "center", minWidth: 0 }}>
              <div style={{ fontSize: 12, marginBottom: 6, color: "var(--muted)", fontWeight: 600 }}>
                {value > 0 ? valueFormatter(value) : ""}
              </div>
              <div style={{ flex: 1, width: "100%", display: "flex", flexDirection: "column", justifyContent: "flex-end", gap: 2 }}>
                {secondaryKey && secondary > 0 && (
                  <div style={{ width: "100%", maxWidth: barMaxWidth ?? undefined, height: `${Math.max(secondaryPercentage, 3)}%`, backgroundColor: secondaryColor, borderRadius: "4px 4px 0 0", margin: "0 auto", transition: "height 0.3s ease" }} />
                )}
                <div style={{ width: "100%", maxWidth: barMaxWidth ?? undefined, height: `${Math.max(percentage, 3)}%`, backgroundColor: item.color || color, borderRadius: secondaryKey && secondary > 0 ? "0 0 4px 4px" : 4, margin: "0 auto", transition: "height 0.3s ease" }} />
              </div>
              <div style={{ fontSize: 11, marginTop: 8, color: "var(--text)", textAlign: "center", width: "100%", overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" }}>
                {item[labelKey]}
              </div>
            </div>
          );
        })}
      </div>
    </div>
  );
}

function FailureBarChart({ data, valueKey, labelKey, height = 200, barGap = 24 }) {
  const maxValue = Math.max(...data.map(d => d[valueKey] || 0), 0);
  return (
    <div style={{ height, width: "100%", display: "flex", alignItems: "stretch", gap: barGap }}>
      {data.map((item, i) => {
        const value = item[valueKey] || 0;
        const percent = maxValue > 0 ? (value / maxValue) * 100 : 0;
        const barStyle = value <= 0 ? { height: 2 } : { height: `${Math.max(percent, 3)}%` };
        return (
          <div key={i} style={{ flex: 1, minWidth: 0, display: "flex", flexDirection: "column", alignItems: "center" }}>
            <div style={{ fontSize: 12, marginBottom: 6, color: "var(--muted)" }}>{value.toFixed(2)}</div>
            <div style={{ flex: 1, width: "100%", display: "flex", flexDirection: "column", justifyContent: "flex-end" }}>
              <div style={{ width: "100%", ...barStyle, backgroundColor: value > 0 ? "#f8a0a0" : "rgba(248,160,160,0.25)", borderRadius: 4, transition: "height 0.3s ease" }} />
            </div>
            <div style={{ fontSize: 11, marginTop: 8, color: "var(--text)", textAlign: "center", width: "100%", overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" }}>{item[labelKey]}</div>
          </div>
        );
      })}
    </div>
  );
}

function StackedHorizontalBarChart({ data, height = 260, barGap = 12 }) {
  if (!data || data.length === 0) {
    return <div style={{ height, display: "flex", alignItems: "center", justifyContent: "center", color: "var(--muted)" }}>Aucune donnée</div>;
  }
  const maxTotal = Math.max(...data.map(d => d.total || 0), 1);
  const categoryMap = new Map();
  data.forEach(d => {
    (d.categories || []).forEach(c => {
      if (!categoryMap.has(c.name)) {
        const info = getCategoryInfoForStats(c.categoryId, c.unifiedCategory);
        categoryMap.set(c.name, { name: c.name, color: info.color });
      }
    });
  });
  const categoryList = Array.from(categoryMap.values()).sort((a, b) => {
    const orderA = CATEGORY_ORDER.indexOf(a.name);
    const orderB = CATEGORY_ORDER.indexOf(b.name);
    return (orderA === -1 ? 999 : orderA) - (orderB === -1 ? 999 : orderB);
  });

  return (
    <div style={{ height, width: "100%", display: "flex", flexDirection: "column" }}>
      <div style={{ display: "flex", flexWrap: "wrap", justifyContent: "center", gap: 12, marginTop: 20, marginBottom: 50, fontSize: 11 }}>
        {categoryList.slice(0, 8).map(cat => (
          <div key={cat.name} style={{ display: "flex", alignItems: "center", gap: 4 }}>
            <div style={{ width: 10, height: 10, backgroundColor: cat.color, borderRadius: 2 }} />
            <span style={{ color: "var(--muted)" }}>{cat.name}</span>
          </div>
        ))}
      </div>
      <div style={{ flex: 1, display: "flex", flexDirection: "column", gap: barGap, overflow: "auto" }}>
        {data.map((item, i) => {
          const totalWidth = (item.total / maxTotal) * 100;
          return (
            <div key={i} style={{ display: "flex", alignItems: "center", gap: 6, minHeight: 24 }}>
              <div style={{ width: 50, fontSize: 11, color: "var(--text)", textAlign: "right", overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap", flexShrink: 0 }}>{item.name}</div>
              <div style={{ flex: 1, height: 20, display: "flex", borderRadius: 4, overflow: "hidden", backgroundColor: "var(--page-bg)" }}>
                {(item.categories || []).map((cat, j) => {
                  const segmentWidth = item.total > 0 ? (cat.count / item.total) * totalWidth : 0;
                  const info = getCategoryInfoForStats(cat.categoryId, cat.unifiedCategory);
                  return <div key={j} style={{ width: `${segmentWidth}%`, height: "100%", backgroundColor: info.color, transition: "width 0.3s ease" }} title={`${info.name}: ${cat.count}`} />;
                })}
              </div>
              <div style={{ width: 40, fontSize: 11, color: "var(--muted)", textAlign: "right", flexShrink: 0 }}>{formatNumber(item.total)}</div>
            </div>
          );
        })}
      </div>
    </div>
  );
}

/* ─────────────────────── NEW: Donut Chart ─────────────────────── */

function DonutChart({ data, size = 200, thickness = 40 }) {
  if (!data || data.length === 0) {
    return <div style={{ width: size, height: size, display: "flex", alignItems: "center", justifyContent: "center", color: "var(--muted)" }}>Aucune donnée</div>;
  }
  const total = data.reduce((s, d) => s + d.value, 0);
  if (total === 0) {
    return <div style={{ width: size, height: size, display: "flex", alignItems: "center", justifyContent: "center", color: "var(--muted)" }}>Aucune donnée</div>;
  }
  const radius = (size - thickness) / 2;
  const center = size / 2;
  const circumference = 2 * Math.PI * radius;
  let offset = 0;

  return (
    <div style={{ display: "flex", alignItems: "center", gap: 24, justifyContent: "center" }}>
      <svg width={size} height={size} viewBox={`0 0 ${size} ${size}`}>
        {data.map((item, i) => {
          const pct = item.value / total;
          const dash = pct * circumference;
          const gap = circumference - dash;
          const currentOffset = offset;
          offset += dash;
          return (
            <circle
              key={i}
              cx={center} cy={center} r={radius}
              fill="none" stroke={item.color} strokeWidth={thickness}
              strokeDasharray={`${dash} ${gap}`}
              strokeDashoffset={-currentOffset}
              style={{ transition: "stroke-dasharray 0.4s ease, stroke-dashoffset 0.4s ease" }}
            >
              <title>{`${item.label}: ${formatNumber(item.value)} (${(pct * 100).toFixed(1)}%)`}</title>
            </circle>
          );
        })}
        <text x={center} y={center - 6} textAnchor="middle" fill="var(--text)" fontSize="18" fontWeight="800">{formatNumber(total)}</text>
        <text x={center} y={center + 14} textAnchor="middle" fill="var(--muted)" fontSize="11">total</text>
      </svg>
      <div style={{ display: "flex", flexDirection: "column", gap: 6 }}>
        {data.map((item, i) => (
          <div key={i} style={{ display: "flex", alignItems: "center", gap: 8, fontSize: 12 }}>
            <div style={{ width: 10, height: 10, borderRadius: 2, backgroundColor: item.color, flexShrink: 0 }} />
            <span style={{ color: "var(--text)" }}>{item.label}</span>
            <span style={{ color: "var(--muted)", marginLeft: "auto", fontWeight: 600 }}>{formatNumber(item.value)}</span>
          </div>
        ))}
      </div>
    </div>
  );
}

/* ─────────────────────── NEW: Line Chart ─────────────────────── */

function LineChart({ data, height = 200, color = "#5cb3ff" }) {
  const containerRef = React.useRef(null);
  const [containerW, setContainerW] = useState(0);

  useEffect(() => {
    if (!containerRef.current) return;
    const obs = new ResizeObserver(entries => {
      for (const e of entries) setContainerW(Math.floor(e.contentRect.width));
    });
    obs.observe(containerRef.current);
    return () => obs.disconnect();
  }, []);

  if (!data || data.length === 0) {
    return <div ref={containerRef} style={{ height, display: "flex", alignItems: "center", justifyContent: "center", color: "var(--muted)" }}>Aucune donnée</div>;
  }

  const maxVal = Math.max(...data.map(d => d.value), 1);
  const padding = { top: 20, right: 16, bottom: 36, left: 48 };
  const chartW = Math.max(containerW, 300);
  const chartH = height;
  const innerW = chartW - padding.left - padding.right;
  const innerH = chartH - padding.top - padding.bottom;

  const points = data.map((d, i) => {
    const x = padding.left + (data.length > 1 ? (i / (data.length - 1)) * innerW : innerW / 2);
    const y = padding.top + innerH - (d.value / maxVal) * innerH;
    return { x, y, ...d };
  });

  const linePath = points.map((p, i) => `${i === 0 ? 'M' : 'L'}${p.x},${p.y}`).join(' ');
  const areaPath = `${linePath} L${points[points.length - 1].x},${padding.top + innerH} L${points[0].x},${padding.top + innerH} Z`;

  const gridLines = 4;
  const gridVals = Array.from({ length: gridLines + 1 }, (_, i) => Math.round((maxVal / gridLines) * i));

  const labelStep = Math.max(1, Math.floor(data.length / 8));

  return (
    <div ref={containerRef} style={{ width: "100%" }}>
      {containerW > 0 && (
        <svg width={chartW} height={chartH} style={{ display: "block" }}>
          {gridVals.map((val, i) => {
            const y = padding.top + innerH - (val / maxVal) * innerH;
            return (
              <g key={i}>
                <line x1={padding.left} y1={y} x2={chartW - padding.right} y2={y} stroke="var(--panel-border)" strokeWidth="1" />
                <text x={padding.left - 8} y={y + 4} textAnchor="end" fill="var(--muted)" fontSize="10">{formatNumber(val)}</text>
              </g>
            );
          })}
          <defs>
            <linearGradient id="lineChartGrad" x1="0" y1="0" x2="0" y2="1">
              <stop offset="0%" stopColor={color} stopOpacity="0.25" />
              <stop offset="100%" stopColor={color} stopOpacity="0.02" />
            </linearGradient>
          </defs>
          <path d={areaPath} fill="url(#lineChartGrad)" />
          <path d={linePath} fill="none" stroke={color} strokeWidth="2" strokeLinejoin="round" strokeLinecap="round" />
          {points.map((p, i) => (
            <g key={i}>
              <circle cx={p.x} cy={p.y} r="3" fill={color} stroke="var(--panel)" strokeWidth="1.5">
                <title>{`${p.label}: ${formatNumber(p.value)}`}</title>
              </circle>
              {i % labelStep === 0 && (
                <text x={p.x} y={padding.top + innerH + 20} textAnchor="middle" fill="var(--muted)" fontSize="10">
                  {p.label}
                </text>
              )}
            </g>
          ))}
        </svg>
      )}
    </div>
  );
}

/* ─────────────────────── NEW: Period Selector ─────────────────────── */

function PeriodSelector({ value, onChange }) {
  const options = [{ v: 7, l: "7j" }, { v: 30, l: "30j" }, { v: 90, l: "90j" }];
  return (
    <div className="period-selector">
      {options.map(o => (
        <button key={o.v} className={`period-btn${value === o.v ? ' period-btn--active' : ''}`} onClick={() => onChange(o.v)}>
          {o.l}
        </button>
      ))}
    </div>
  );
}

/* ─────────────────────── NEW: Stat Tab ─────────────────────── */

function StatTab({ id, label, value, sub, active, onClick }) {
  const isActive = active === id;
  return (
    <button
      className={`card card-fourth stat-tab${isActive ? ' stat-tab--active' : ''}`}
      onClick={() => onClick(id)}
      type="button"
    >
      <div className="card-title" style={{ fontSize: 13 }}>{label}</div>
      <div className="card-value" style={{ fontSize: 22 }}>{value ?? '-'}</div>
      {sub && <div className="stat-tab__sub" style={{ fontSize: 11, color: "var(--muted)", marginTop: 2 }}>{sub}</div>}
    </button>
  );
}

/* ─────────────────────── Loading / Metric card ─────────────────────── */

function PanelLoading() {
  return <div className="card" style={{ padding: 32, textAlign: "center", color: "var(--muted)" }}>Chargement...</div>;
}

function MetricCard({ title, value, color }) {
  return (
    <div style={{ textAlign: "center", padding: 16, background: "var(--page-bg)", borderRadius: 8 }}>
      <div style={{ fontSize: 24, fontWeight: 800, color: color || "var(--accent)" }}>{value}</div>
      <div style={{ fontSize: 12, color: "var(--muted)", marginTop: 4 }}>{title}</div>
    </div>
  );
}

/* ─────────────────────── PANEL: Feedarr ─────────────────────── */

function FeedarrPanel({ refreshKey }) {
  const [data, setData] = useState(null);
  const [days, setDays] = useState(30);
  const [error, setError] = useState("");

  const load = useCallback(async (d) => {
    setError("");
    try {
      const res = await apiGet(`/api/system/stats/feedarr?days=${d}`);
      setData(res);
    } catch (e) { setError(e?.message || "Erreur"); }
  }, []);

  useEffect(() => { load(days); }, [refreshKey, days, load]);

  const handlePeriod = useCallback((d) => { setDays(d); }, []);

  if (error) return <div className="card" style={{ color: "red", fontWeight: 700 }}>Erreur: {error}</div>;
  if (!data) return <PanelLoading />;

  const matchCount = data.releasesCount - (data.missingPosters || 0);
  const matchPct = data.releasesCount > 0 ? Math.round((matchCount / data.releasesCount) * 100) : 0;

  const storageData = [];
  if (data.storage?.databaseBytes > 0) storageData.push({ label: "Base de données", value: data.storage.databaseBytes, color: "#5cb3ff" });
  if (data.storage?.postersBytes > 0) storageData.push({ label: "Posters", value: data.storage.postersBytes, color: "#22c55e" });
  if (data.storage?.backupsBytes > 0) storageData.push({ label: "Backups", value: data.storage.backupsBytes, color: "#f59e0b" });

  const lineData = (data.releasesPerDay || []).map(d => ({
    label: d.date?.slice(5) || "",
    value: d.count
  }));

  const appTypeMeta = {
    sonarr: { color: "#5cb3ff", label: "Sonarr" },
    radarr: { color: "#f59e0b", label: "Radarr" },
    overseerr: { color: "#ef4444", label: "Overseerr" },
    jellyseerr: { color: "#14b8a6", label: "Jellyseerr" },
    seer: { color: "#a855f7", label: "Seer" }
  };

  const appStats = (Array.isArray(data.arrApps) ? data.arrApps : []).map((app) => {
    const type = String(app?.type || "").toLowerCase();
    const isRequestType = type === "overseerr" || type === "jellyseerr" || type === "seer";
    const sonarrMatchedCount = Math.max(0, Math.trunc(Number(data?.sonarrMatchedCount || 0)));
    const radarrMatchedCount = Math.max(0, Math.trunc(Number(data?.radarrMatchedCount || 0)));
    const displayCount = type === "sonarr"
      ? sonarrMatchedCount
      : type === "radarr"
        ? radarrMatchedCount
        : Number.isFinite(Number(app?.displayCount))
          ? Number(app.displayCount)
          : Number(app?.lastSyncCount || 0);
    const countMode = app?.countMode || (isRequestType ? "requests" : "matches");
    const meta = appTypeMeta[type] || { color: "var(--accent)", label: type || "Application" };
    const isEnabled = !!app?.enabled;
    return {
      id: app?.id ?? `${type}-${app?.name || "app"}`,
      name: String(app?.name || meta.label),
      typeLabel: meta.label,
      color: meta.color,
      enabled: isEnabled,
      displayCount: Math.max(0, Math.trunc(displayCount)),
      metricLabel: countMode === "requests" ? "demandes" : "éléments matchés"
    };
  });

  return (
    <>
      {/* Metric cards */}
      <div className="card" style={{ padding: 20, marginBottom: 20 }}>
        <div style={{ display: "grid", gridTemplateColumns: "repeat(3, 1fr)", gap: 16 }}>
          <MetricCard title="Uptime" value={fmtUptime(data.uptimeSeconds)} />
          <MetricCard title="Taille base" value={`${data.dbSizeMB || 0} Mo`} />
          <MetricCard title="Posters locaux" value={formatNumber(data.localPosters || 0)} />
        </div>
      </div>

      {/* Releases per day */}
      <div className="card" style={{ padding: "12px 16px", marginBottom: 20 }}>
        <div style={{ display: "flex", alignItems: "center", justifyContent: "space-between", marginBottom: 12 }}>
          <div className="card-title">Releases par jour</div>
          <PeriodSelector value={days} onChange={handlePeriod} />
        </div>
        <LineChart data={lineData} height={220} color="#5cb3ff" />
      </div>

      {/* Posters row */}
      <div className="card-row card-row-third" style={{ marginBottom: 20 }}>
        {/* Couverture Posters */}
        <div className="card card-third" style={{ padding: "12px 16px" }}>
          <div className="card-title" style={{ marginBottom: 12 }}>Couverture Posters</div>
          <div style={{ display: "flex", flexDirection: "column", alignItems: "center", gap: 10, padding: "8px 0" }}>
            <div style={{ fontSize: 40, fontWeight: 800, color: matchPct >= 70 ? "#22c55e" : matchPct >= 50 ? "#f59e0b" : "#ef4444" }}>
              {matchPct}%
            </div>
            <div style={{ width: "85%", height: 10, background: "var(--page-bg)", borderRadius: 5, overflow: "hidden" }}>
              <div style={{ width: `${matchPct}%`, height: "100%", background: matchPct >= 70 ? "#22c55e" : matchPct >= 50 ? "#f59e0b" : "#ef4444", borderRadius: 5, transition: "width 0.4s ease" }} />
            </div>
            <div style={{ fontSize: 11, color: "var(--muted)", textAlign: "center" }}>
              {formatNumber(matchCount)} / {formatNumber(data.releasesCount)} releases
            </div>
          </div>
        </div>

        {/* Posters Affichés */}
        <div className="card card-third" style={{ padding: "12px 16px" }}>
          <div className="card-title" style={{ marginBottom: 12 }}>Posters Affichés</div>
          <div style={{ display: "flex", flexDirection: "column", alignItems: "center", gap: 10, padding: "8px 0" }}>
            <div style={{ fontSize: 40, fontWeight: 800, color: matchPct >= 80 ? "#22c55e" : matchPct >= 50 ? "#f59e0b" : "#ef4444" }}>
              {matchPct}%
            </div>
            <div style={{ display: "flex", gap: 16, fontSize: 12, color: "var(--muted)" }}>
              <span>{formatNumber(data.distinctPosterFiles || data.localPosters || 0)} fichiers uniques</span>
            </div>
            <div style={{ fontSize: 11, color: "var(--muted)", textAlign: "center" }}>
              {formatNumber(data.missingPosters || 0)} manquants
            </div>
          </div>
        </div>

        {/* Réutilisation Posters */}
        <div className="card card-third" style={{ padding: "12px 16px" }}>
          <div className="card-title" style={{ marginBottom: 12 }}>Réutilisation Posters</div>
          <div style={{ display: "flex", flexDirection: "column", alignItems: "center", gap: 10, padding: "8px 0" }}>
            <div style={{ fontSize: 40, fontWeight: 800, color: (data.posterReuseRatio || 0) >= 2 ? "#8b5cf6" : "var(--accent)" }}>
              {data.posterReuseRatio || '—'}x
            </div>
            <div style={{ fontSize: 12, color: "var(--muted)", textAlign: "center" }}>
              releases par poster
            </div>
            <div style={{ fontSize: 11, color: "var(--muted)", textAlign: "center" }}>
              {formatNumber(data.distinctPosterFiles || 0)} posters pour {formatNumber(data.releasesWithPoster || 0)} releases
            </div>
          </div>
        </div>
      </div>

      {/* Storage + Arr Apps row */}
      <div className="card-row card-row-half system-feedarr-wide" style={{ marginBottom: 20 }}>
        {/* Storage */}
        <div className="card card-half system-feedarr-wide-card" style={{ padding: "12px 16px" }}>
          <div className="card-title" style={{ marginBottom: 16 }}>Répartition stockage</div>
          {storageData.length > 0 ? (
            <DonutChart
              data={storageData.map(d => ({ ...d, label: `${d.label} (${fmtBytes(d.value)})`, value: d.value }))}
              size={160}
              thickness={32}
            />
          ) : (
            <div style={{ color: "var(--muted)", textAlign: "center", padding: 24 }}>Aucune donnée</div>
          )}
        </div>

        {/* Arr Apps */}
        <div className="card card-half system-feedarr-wide-card" style={{ padding: "12px 16px" }}>
          <div className="card-title" style={{ marginBottom: 16 }}>Applications</div>
          {appStats.length > 0 ? (
            <>
              <HorizontalBarChart
                data={appStats.map((app) => ({
                  name: app.name,
                  count: app.displayCount,
                  color: app.enabled ? app.color : "var(--panel-border)"
                }))}
                labelKey="name"
                valueKey="count"
                height={180}
                stretchBars
              />
              <div style={{ marginTop: 12, display: "flex", flexWrap: "wrap", gap: 10, justifyContent: "center" }}>
                {appStats.map((app) => (
                  <div key={app.id} style={{ display: "inline-flex", alignItems: "center", gap: 6, fontSize: 11, color: "var(--muted)", padding: "4px 8px", background: "var(--page-bg)", borderRadius: 999 }}>
                    <span style={{ display: "inline-flex", alignItems: "center", gap: 6 }}>
                      <span style={{ width: 8, height: 8, borderRadius: "50%", background: app.enabled ? app.color : "var(--panel-border)", display: "inline-block" }} />
                      <span style={{ color: "var(--text)" }}>{app.name}</span>
                      {!app.enabled && <span>(désactivée)</span>}
                    </span>
                    <span style={{ fontWeight: 700, color: "var(--text)" }}>
                      {formatNumber(app.displayCount)} {app.metricLabel}
                    </span>
                  </div>
                ))}
              </div>
            </>
          ) : (
            <div style={{ color: "var(--muted)", textAlign: "center", padding: 24 }}>Aucune application connectée</div>
          )}
        </div>
      </div>
    </>
  );
}

/* ─────────────────────── PANEL: Indexeurs ─────────────────────── */

function IndexersPanel({ refreshKey }) {
  const [data, setData] = useState(null);
  const [error, setError] = useState("");

  useEffect(() => {
    (async () => {
      setError("");
      try { setData(await apiGet('/api/system/stats/indexers')); }
      catch (e) { setError(e?.message || "Erreur"); }
    })();
  }, [refreshKey]);

  const stackedByIndexer = useMemo(() => {
    if (!data?.releasesByCategoryByIndexer) return [];
    const byIndexer = {};
    data.releasesByCategoryByIndexer.forEach(item => {
      if (!byIndexer[item.sourceName]) byIndexer[item.sourceName] = { name: item.sourceName, categoriesMap: {}, total: 0 };
      const info = getCategoryInfoForStats(item.categoryId, item.unifiedCategory);
      const key = info.name;
      if (!byIndexer[item.sourceName].categoriesMap[key]) byIndexer[item.sourceName].categoriesMap[key] = {
        categoryId: item.categoryId,
        unifiedCategory: item.unifiedCategory,
        name: info.name,
        count: 0
      };
      byIndexer[item.sourceName].categoriesMap[key].count += item.count;
      byIndexer[item.sourceName].total += item.count;
    });
    return Object.values(byIndexer)
      .map(ix => ({ name: ix.name, total: ix.total, categories: Object.values(ix.categoriesMap).sort((a, b) => (CATEGORY_ORDER.indexOf(a.name) === -1 ? 999 : CATEGORY_ORDER.indexOf(a.name)) - (CATEGORY_ORDER.indexOf(b.name) === -1 ? 999 : CATEGORY_ORDER.indexOf(b.name))) }))
      .sort((a, b) => b.total - a.total).slice(0, 8);
  }, [data?.releasesByCategoryByIndexer]);

  if (error) return <div className="card" style={{ color: "red", fontWeight: 700 }}>Erreur: {error}</div>;
  if (!data) return <PanelLoading />;

  const indexerChartData = (data.indexerStatsBySource || []).map(src => ({ name: src.name, releases: src.releaseCount, failed: src.lastStatus === "error" ? 1 : 0 }));
  const responseChartData = (data.indexerResponseMsBySource || []).map(src => ({ name: src.name, avgMs: Number(src.avgMs || 0) }));

  return (
    <>
      {/* Metric cards */}
      <div className="card" style={{ padding: 20, marginBottom: 20 }}>
        <div style={{ display: "grid", gridTemplateColumns: "repeat(4, 1fr)", gap: 16 }}>
          <MetricCard title="Requêtes totales" value={formatNumber(data.queries || 0)} />
          <MetricCard title="Échecs requêtes" value={data.failures || 0} color={data.failures > 0 ? "#ef4444" : undefined} />
          <MetricCard title="Sync Jobs" value={formatNumber(data.syncJobs || 0)} />
          <MetricCard title="Échecs sync" value={data.syncFailures || 0} color={data.syncFailures > 0 ? "#ef4444" : undefined} />
        </div>
      </div>

      {/* Charts */}
      {(indexerChartData.length > 0 || responseChartData.length > 0) && (
        <div className="card-row card-row-half system-mobile-full-row" style={{ marginBottom: 20 }}>
          {indexerChartData.length > 0 && (
            <div className="card card-half system-mobile-full-card" style={{ padding: "12px 16px" }}>
              <div className="card-title" style={{ marginBottom: 12 }}>Releases par fournisseur</div>
              <HorizontalBarChart data={indexerChartData} valueKey="releases" labelKey="name" color="#5cb3ff" height={260} barGap={24} barMaxWidth={110} />
            </div>
          )}
          {responseChartData.length > 0 && (
            <div className="card card-half system-mobile-full-card" style={{ padding: "12px 16px" }}>
              <div className="card-title" style={{ marginBottom: 12 }}>Temps de réponse</div>
              <HorizontalBarChart
                data={responseChartData}
                valueKey="avgMs"
                labelKey="name"
                color="#22c55e"
                height={260}
                barGap={24}
                barMaxWidth={110}
                valueFormatter={(value) => `${Math.round(value)} ms`}
              />
            </div>
          )}
        </div>
      )}
      {stackedByIndexer.length > 0 && (
        <div className="card" style={{ padding: "12px 12px 12px 8px", marginBottom: 20 }}>
          <div className="card-title" style={{ marginBottom: 8 }}>Catégories par indexeur</div>
          <StackedHorizontalBarChart data={stackedByIndexer} height={260} barGap={8} />
        </div>
      )}

      {/* Indexer detail table */}
      {data.indexerDetails?.length > 0 && (
        <div className="card" style={{ padding: 20 }}>
          <div className="card-title" style={{ marginBottom: 16 }}>Détail fournisseurs</div>
          <div style={{ overflowX: "auto" }}>
            <table className="stats-table">
              <thead>
                <tr>
                  <th>{tr("Nom", "Name")}</th>
                  <th>{tr("Actif", "Active")}</th>
                  <th>Releases</th>
                  <th>Dernier Sync</th>
                  <th>Statut</th>
                  <th>{tr("Derniers items", "Last items")}</th>
                </tr>
              </thead>
              <tbody>
                {data.indexerDetails.map(ix => (
                  <tr key={ix.id}>
                    <td style={{ fontWeight: 600 }}>{ix.name}</td>
                    <td>
                      <span style={{ display: "inline-block", width: 8, height: 8, borderRadius: "50%", backgroundColor: ix.enabled ? "#22c55e" : "#94a3b8" }} />
                    </td>
                    <td>{formatNumber(ix.releaseCount)}</td>
                    <td style={{ fontSize: 12, color: "var(--muted)" }}>
                      {ix.lastSyncAtTs ? new Date(ix.lastSyncAtTs * 1000).toLocaleString(getActiveUiLanguage()) : "-"}
                    </td>
                    <td>
                      <span className={`status-badge status-badge--${ix.lastStatus === "ok" ? "ok" : ix.lastStatus === "error" ? "error" : "unknown"}`}>
                        {ix.lastStatus || "-"}
                      </span>
                    </td>
                    <td>{ix.lastItemCount || 0}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </div>
      )}
    </>
  );
}

/* ─────────────────────── PANEL: Providers ─────────────────────── */

function ProvidersPanel({ refreshKey }) {
  const [data, setData] = useState(null);
  const [externalProviders, setExternalProviders] = useState({ definitions: [], instances: [] });
  const [error, setError] = useState("");

  useEffect(() => {
    (async () => {
      setError("");
      try {
        const [statsData, providersData] = await Promise.all([
          apiGet('/api/system/stats/providers'),
          apiGet('/api/providers/external'),
        ]);
        setData(statsData || {});
        setExternalProviders({
          definitions: Array.isArray(providersData?.definitions) ? providersData.definitions : [],
          instances: Array.isArray(providersData?.instances) ? providersData.instances : [],
        });
      }
      catch (e) { setError(e?.message || "Erreur"); }
    })();
  }, [refreshKey]);

  if (error) return <div className="card" style={{ color: "red", fontWeight: 700 }}>Erreur: {error}</div>;
  if (!data) return <PanelLoading />;

  const labels = {
    tmdb: 'TMDB',
    tvmaze: 'TVmaze',
    fanart: 'Fanart',
    igdb: 'IGDB',
    jikan: 'Jikan (MAL)',
    googlebooks: 'Google Books',
    theaudiodb: 'TheAudioDB',
    comicvine: 'Comic Vine',
  };

  const activeProviderKeys = (externalProviders?.instances || [])
    .filter((instance) => instance?.enabled !== false)
    .map((instance) => String(instance?.providerKey || "").toLowerCase())
    .filter(Boolean);

  const providerKeys = Array.from(new Set(activeProviderKeys)).sort((a, b) => a.localeCompare(b));

  const providerData = providerKeys.map(p => ({
    provider: labels[p] || p.toUpperCase(),
    calls: Number(data?.[p]?.calls || 0),
    failures: Number(data?.[p]?.failures || 0),
    avgMs: Number(data?.[p]?.avgMs || 0),
  }));
  const matchingByProvider = (data?._matchingByProvider && typeof data._matchingByProvider === "object")
    ? data._matchingByProvider
    : {};
  const matchingData = providerKeys.map((key) => ({
    provider: labels[key] || key.toUpperCase(),
    matched: Number(matchingByProvider[key] || 0),
  }));
  const failureData = providerData.map(p => ({ provider: p.provider, rate: p.calls > 0 ? (p.failures / p.calls) : 0 }));

  const totalCalls = providerData.reduce((s, p) => s + p.calls, 0);
  const totalFails = providerData.reduce((s, p) => s + p.failures, 0);
  const globalRate = totalCalls > 0 ? ((totalFails / totalCalls) * 100).toFixed(1) + '%' : '0%';

  return (
    <>
      {/* Metric cards */}
      <div className="card" style={{ padding: 20, marginBottom: 20 }}>
        <div style={{ display: "grid", gridTemplateColumns: "repeat(3, 1fr)", gap: 16 }}>
          <MetricCard title="Total Appels" value={formatNumber(totalCalls)} />
          <MetricCard title="Total échecs" value={formatNumber(totalFails)} color={totalFails > 0 ? "#ef4444" : undefined} />
          <MetricCard title="Taux d'échec global" value={globalRate} color={totalFails > 0 ? "#f59e0b" : undefined} />
        </div>
      </div>

      {/* Charts row */}
      <div className="card-row card-row-third system-mobile-full-row" style={{ marginBottom: 20 }}>
        <div className="card card-third system-mobile-full-card" style={{ padding: "12px 16px" }}>
          <div className="card-title" style={{ marginBottom: 12 }}>Appels API</div>
          <HorizontalBarChart data={providerData} valueKey="calls" labelKey="provider" color="#5cb3ff" height={200} barGap={8} stretchBars />
        </div>
        <div className="card card-third system-mobile-full-card" style={{ padding: "12px 16px" }}>
          <div className="card-title" style={{ marginBottom: 12 }}>Taux d'échec</div>
          <FailureBarChart data={failureData} valueKey="rate" labelKey="provider" height={200} />
        </div>
        <div className="card card-third system-mobile-full-card" style={{ padding: "12px 16px" }}>
          <div className="card-title" style={{ marginBottom: 12 }}>Temps de réponse moyen</div>
          <HorizontalBarChart data={providerData} valueKey="avgMs" labelKey="provider" color="#22c55e" height={200} barGap={8} stretchBars />
        </div>
      </div>

      {/* Matching */}
      <div className="card" style={{ padding: "12px 16px", marginBottom: 20 }}>
        <div className="card-title" style={{ marginBottom: 12 }}>Matching</div>
        <HorizontalBarChart data={matchingData} valueKey="matched" labelKey="provider" color="#f59e0b" height={220} barGap={12} stretchBars />
      </div>

      {/* Detail table */}
      <div className="card" style={{ padding: 20 }}>
        <div className="card-title" style={{ marginBottom: 16 }}>Détail Métadonnées</div>
        <table className="stats-table">
          <thead>
            <tr>
              <th>Provider</th>
              <th>Appels</th>
              <th>Échecs</th>
              <th>Taux échec</th>
              <th>{tr("Temps moyen", "Average time")}</th>
            </tr>
          </thead>
          <tbody>
            {providerData.map(p => (
              <tr key={p.provider}>
                <td style={{ fontWeight: 600 }}>{p.provider}</td>
                <td>{formatNumber(p.calls)}</td>
                <td style={{ color: p.failures > 0 ? "#ef4444" : undefined }}>{p.failures}</td>
                <td>{p.calls > 0 ? ((p.failures / p.calls) * 100).toFixed(2) + '%' : '0%'}</td>
                <td>{p.avgMs > 0 ? `${p.avgMs} ms` : '-'}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </>
  );
}

/* ─────────────────────── PANEL: Releases ─────────────────────── */

function ReleasesPanel({ refreshKey }) {
  const [data, setData] = useState(null);
  const [error, setError] = useState("");

  useEffect(() => {
    (async () => {
      setError("");
      try { setData(await apiGet('/api/system/stats/releases')); }
      catch (e) { setError(e?.message || "Erreur"); }
    })();
  }, [refreshKey]);

  if (error) return <div className="card" style={{ color: "red", fontWeight: 700 }}>Erreur: {error}</div>;
  if (!data) return <PanelLoading />;

  const donutData = (data.releasesByCategory || [])
    .slice()
    .sort((a, b) => (b.count || 0) - (a.count || 0))
    .slice(0, 10)
    .map((category) => {
      const rawKey = category.key || category.categoryKey || "";
      const canonicalKey = normalizeCategoryGroupKey(rawKey);
      const tokenKey = canonicalKey
        ? STATS_TOKEN_BY_CANONICAL_CATEGORY[canonicalKey] || "stats-unknown"
        : "stats-unknown";
      return {
        key: canonicalKey || String(rawKey || "").toLowerCase(),
        label: canonicalKey
          ? CATEGORY_GROUP_LABELS[canonicalKey] || category.label || canonicalKey
          : category.label || category.key || "Autre",
        value: category.count || 0,
        color: getCategoryColor(tokenKey),
      };
    });
  const releaseColorByKey = new Map(donutData.map(item => [String(item.key || "").toLowerCase(), item.color]));

  const sizeData = (data.sizeDistribution || []).filter(d => d.range !== "Inconnu");
  const seedData = (data.seedersDistribution || []).filter(d => d.range !== "Inconnu");

  return (
    <>
      {/* Metric cards */}
      <div className="card" style={{ padding: 20, marginBottom: 20 }}>
        <div style={{ display: "grid", gridTemplateColumns: "repeat(3, 1fr)", gap: 16 }}>
          <MetricCard title="Total Releases" value={formatNumber(data.releasesCount || 0)} />
          <MetricCard title="Avec Poster" value={formatNumber(data.withPoster || 0)} color="#22c55e" />
          <MetricCard title="Sans Poster" value={formatNumber(data.missingPoster || 0)} color={data.missingPoster > 0 ? "#f59e0b" : "#22c55e"} />
        </div>
      </div>

      {/* Donut + distributions */}
      <div className="card-row card-row-half system-mobile-full-row" style={{ marginBottom: 20 }}>
        <div className="card card-half system-mobile-full-card" style={{ padding: "12px 16px" }}>
          <div className="card-title" style={{ marginBottom: 16 }}>Top Catégories</div>
          <DonutChart data={donutData} size={190} thickness={36} />
        </div>
        <div className="card card-half system-mobile-full-card" style={{ padding: "12px 16px" }}>
          <div className="card-title" style={{ marginBottom: 12 }}>Distribution par taille</div>
          <HorizontalBarChart data={sizeData.map(d => ({ label: d.range, value: d.count }))} valueKey="value" labelKey="label" color="#8b5cf6" height={200} barGap={8} stretchBars />
        </div>
      </div>

      {/* Seeders distribution */}
      <div className="card" style={{ padding: "12px 16px", marginBottom: 20 }}>
        <div className="card-title" style={{ marginBottom: 12 }}>Distribution Seeders</div>
        <HorizontalBarChart data={seedData.map(d => ({ label: d.range, value: d.count }))} valueKey="value" labelKey="label" color="#22c55e" height={200} barGap={12} stretchBars />
      </div>

      {/* Top grabbed */}
      {data.topGrabbed?.length > 0 && (
        <div className="card" style={{ padding: 20 }}>
          <div className="card-title" style={{ marginBottom: 16 }}>Top Releases (Grabs)</div>
          <div style={{ overflowX: "auto" }}>
            <table className="stats-table">
              <thead>
                <tr>
                  <th style={{ width: "45%" }}>Titre</th>
                  <th>Catégorie</th>
                  <th>Grabs</th>
                  <th>Seeders</th>
                  <th>Taille</th>
                </tr>
              </thead>
              <tbody>
                {data.topGrabbed.map((r, i) => {
                  const categoryKey = normalizeCategoryGroupKey(r.categoryKey) || String(r.categoryKey || "").toLowerCase();
                  const categoryLabel = categoryKey
                    ? CATEGORY_GROUP_LABELS[categoryKey] || r.categoryLabel || r.categoryKey || "Autre"
                    : r.categoryLabel || r.categoryKey || "Autre";
                  const categoryColor =
                    releaseColorByKey.get(categoryKey) ||
                    getCategoryColor(
                      categoryKey
                        ? STATS_TOKEN_BY_CANONICAL_CATEGORY[categoryKey] || "stats-unknown"
                        : "stats-unknown"
                    );
                  return (
                    <tr key={i}>
                      <td style={{ maxWidth: 300, overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" }} title={r.title}>{r.title}</td>
                      <td><span style={{ display: "inline-flex", alignItems: "center", gap: 4 }}><span style={{ width: 8, height: 8, borderRadius: 2, backgroundColor: categoryColor, display: "inline-block" }} />{categoryLabel}</span></td>
                      <td style={{ fontWeight: 700, color: "var(--accent)" }}>{r.grabs}</td>
                      <td>{r.seeders}</td>
                      <td>{fmtBytes(r.sizeBytes)}</td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        </div>
      )}
    </>
  );
}

/* ─────────────────────── MAIN COMPONENT ─────────────────────── */

export default function SystemStatistics({ refreshKey = 0 }) {
  const [activeTab, setActiveTab] = useState('feedarr');
  const [summary, setSummary] = useState(null);
  const [error, setError] = useState("");

  useEffect(() => {
    (async () => {
      setError("");
      try { setSummary(await apiGet('/api/system/stats/summary')); }
      catch (e) { setError(e?.message || "Erreur chargement statistiques"); }
    })();
  }, [refreshKey]);

  if (error) return <div className="card" style={{ color: "red", fontWeight: 700 }}>Erreur: {error}</div>;

  return (
    <section className="system-section">
      {/* Tab buttons */}
      <div className="card-row card-row-fourth" style={{ marginBottom: 0 }}>
        <StatTab
          id="feedarr" label="Feedarr" active={activeTab} onClick={setActiveTab}
          value={summary?.version ?? '...'}
          sub={summary ? fmtUptime(summary.uptimeSeconds) : null}
        />
        <StatTab
          id="indexers" label="Fournisseurs" active={activeTab} onClick={setActiveTab}
          value={summary ? `${summary.activeIndexers} actifs` : '...'}
          sub={summary ? `${formatNumber(summary.totalQueries)} requêtes` : null}
        />
        <StatTab
          id="providers" label="Métadonnées" active={activeTab} onClick={setActiveTab}
          value={summary ? formatNumber(summary.totalCalls) : '...'}
          sub={summary ? `${summary.totalFailures} échecs` : null}
        />
        <StatTab
          id="releases" label="Releases" active={activeTab} onClick={setActiveTab}
          value={summary ? formatNumber(summary.releasesCount) : '...'}
          sub={summary ? `${summary.matchingPercent}% matchés` : null}
        />
      </div>

      {/* Tab content */}
      {activeTab === 'feedarr' && <FeedarrPanel refreshKey={refreshKey} />}
      {activeTab === 'indexers' && <IndexersPanel refreshKey={refreshKey} />}
      {activeTab === 'providers' && <ProvidersPanel refreshKey={refreshKey} />}
      {activeTab === 'releases' && <ReleasesPanel refreshKey={refreshKey} />}
    </section>
  );
}
