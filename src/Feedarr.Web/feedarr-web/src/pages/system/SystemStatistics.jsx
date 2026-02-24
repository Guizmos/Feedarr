import React, { useEffect, useMemo, useState, useCallback } from 'react';
import { apiGet } from '../../api/client.js';
import { fmtUptime, fmtBytes } from './systemUtils.js';
import { getActiveUiLanguage } from '../../app/locale.js';
import { tr } from '../../app/uiText.js';

/* ─────────────────────── Helpers ─────────────────────── */

function formatNumber(num) {
  if (num >= 1000000) return (num / 1000000).toFixed(1) + 'M';
  if (num >= 1000) return (num / 1000).toFixed(num >= 10000 ? 0 : 1) + 'K';
  return num.toString();
}

/* ─────────────────────── Category mapping ─────────────────────── */

const CATEGORY_MAP = {
  2183: { color: "#3b82f6", name: "Films" },
  2145: { color: "#f472b6", name: "Animation" },
  2184: { color: "#22c55e", name: "Séries" },
  2185: { color: "#f472b6", name: "Animation" },
  2178: { color: "#06b6d4", name: "Documentaire" },
  2182: { color: "#a855f7", name: "Émissions" },
  2190: { color: "#f97316", name: "Spectacle" },
  2181: { color: "#84cc16", name: "Sport" },
  2188: { color: "#ec4899", name: "Clip" },
  2189: { color: "#64748b", name: "Autre Vidéo" },
  2090: { color: "#f97316", name: "Spectacle" },
  5080: { color: "#a855f7", name: "Émissions" },
  2101: { color: "#8b5cf6", name: "Musique" },
  2102: { color: "#8b5cf6", name: "Musique" },
  2103: { color: "#8b5cf6", name: "Samples" },
  2104: { color: "#8b5cf6", name: "Podcast" },
  2105: { color: "#8b5cf6", name: "Karaoké" },
  2136: { color: "#ef4444", name: "Livres" },
  2137: { color: "#ef4444", name: "BD" },
  2138: { color: "#ef4444", name: "Comics" },
  2139: { color: "#ef4444", name: "Mangas" },
  2140: { color: "#ef4444", name: "Presse" },
  2141: { color: "#ef4444", name: "Audio Livres" },
  2160: { color: "#f59e0b", name: "Jeux Vidéo" },
  2161: { color: "#f59e0b", name: "Jeux Windows" },
  2162: { color: "#f59e0b", name: "Jeux Linux" },
  2163: { color: "#f59e0b", name: "Jeux Mac" },
  2164: { color: "#f59e0b", name: "Jeux Console" },
  2165: { color: "#f59e0b", name: "Jeux Nintendo" },
  2166: { color: "#f59e0b", name: "Jeux Sony" },
  2167: { color: "#f59e0b", name: "Jeux Microsoft" },
  2168: { color: "#f59e0b", name: "Jeux Android" },
  2169: { color: "#f59e0b", name: "Jeux iOS" },
  2150: { color: "#ec4899", name: "Logiciels" },
  2151: { color: "#ec4899", name: "Apps Windows" },
  2152: { color: "#ec4899", name: "Apps Linux" },
  2153: { color: "#ec4899", name: "Apps Mac" },
  2154: { color: "#ec4899", name: "Apps Mobile" },
  2155: { color: "#ec4899", name: "GPS" },
  2156: { color: "#ec4899", name: "Formation" },
  2191: { color: "#14b8a6", name: "XXX" },
  1000: { color: "#f59e0b", name: "Console" },
  1010: { color: "#f59e0b", name: "NDS" },
  1020: { color: "#f59e0b", name: "PSP" },
  1030: { color: "#f59e0b", name: "Wii" },
  1040: { color: "#f59e0b", name: "Xbox" },
  1050: { color: "#f59e0b", name: "Xbox 360" },
  1060: { color: "#f59e0b", name: "WiiWare" },
  1070: { color: "#f59e0b", name: "Xbox 360 DLC" },
  1080: { color: "#f59e0b", name: "PS3" },
  1090: { color: "#f59e0b", name: "Autre Console" },
  2000: { color: "#3b82f6", name: "Films" },
  2010: { color: "#3b82f6", name: "Films Étranger" },
  2020: { color: "#3b82f6", name: "Films Autre" },
  2030: { color: "#3b82f6", name: "Films SD" },
  2040: { color: "#3b82f6", name: "Films HD" },
  2045: { color: "#3b82f6", name: "Films UHD" },
  2050: { color: "#3b82f6", name: "Films BluRay" },
  2060: { color: "#3b82f6", name: "Films 3D" },
  2070: { color: "#06b6d4", name: "Documentaire" },
  3000: { color: "#8b5cf6", name: "Audio" },
  3010: { color: "#8b5cf6", name: "MP3" },
  3020: { color: "#8b5cf6", name: "Video Clip" },
  3030: { color: "#8b5cf6", name: "Lossless" },
  3040: { color: "#8b5cf6", name: "Autre Audio" },
  3050: { color: "#8b5cf6", name: "Podcast" },
  3060: { color: "#8b5cf6", name: "Audiobook" },
  4000: { color: "#ec4899", name: "PC" },
  4010: { color: "#ec4899", name: "PC 0day" },
  4020: { color: "#ec4899", name: "PC ISO" },
  4030: { color: "#ec4899", name: "PC Mac" },
  4040: { color: "#ec4899", name: "PC Mobile" },
  4050: { color: "#ec4899", name: "PC Games" },
  4060: { color: "#ec4899", name: "PC Mobile iOS" },
  4070: { color: "#ec4899", name: "PC Mobile Android" },
  5000: { color: "#22c55e", name: "Séries" },
  5010: { color: "#22c55e", name: "Séries WEB-DL" },
  5020: { color: "#22c55e", name: "Séries Étranger" },
  5030: { color: "#22c55e", name: "Séries SD" },
  5040: { color: "#22c55e", name: "Séries HD" },
  5045: { color: "#22c55e", name: "Séries UHD" },
  5050: { color: "#22c55e", name: "Séries Autre" },
  5060: { color: "#84cc16", name: "Sport" },
  5070: { color: "#f472b6", name: "Anime" },
  6000: { color: "#14b8a6", name: "XXX" },
  6010: { color: "#14b8a6", name: "XXX DVD" },
  6020: { color: "#14b8a6", name: "XXX WMV" },
  6030: { color: "#14b8a6", name: "XXX XviD" },
  6040: { color: "#14b8a6", name: "XXX x264" },
  6050: { color: "#14b8a6", name: "XXX Pack" },
  6060: { color: "#14b8a6", name: "XXX ImageSet" },
  6070: { color: "#14b8a6", name: "XXX Other" },
  7000: { color: "#ef4444", name: "Livres" },
  7010: { color: "#ef4444", name: "Magazines" },
  7020: { color: "#ef4444", name: "Ebook" },
  7030: { color: "#ef4444", name: "Comics" },
  7040: { color: "#ef4444", name: "Technical" },
  7050: { color: "#ef4444", name: "Foreign Books" },
  8000: { color: "#6366f1", name: "Autre" },
  8010: { color: "#6366f1", name: "Misc" },
  8020: { color: "#6366f1", name: "Hashed" },
};

const MAIN_CATEGORY_FALLBACKS = {
  1: { color: "#f59e0b", name: "Console" },
  2: { color: "#3b82f6", name: "Films" },
  3: { color: "#8b5cf6", name: "Audio" },
  4: { color: "#ec4899", name: "PC" },
  5: { color: "#22c55e", name: "Séries" },
  6: { color: "#14b8a6", name: "XXX" },
  7: { color: "#ef4444", name: "Livres" },
  8: { color: "#6366f1", name: "Autre" },
};

const UNIFIED_CATEGORY_INFO = {
  film: { color: "#3b82f6", name: "Films" },
  serie: { color: "#22c55e", name: "Séries" },
  emission: { color: "#a855f7", name: "Émissions" },
  spectacle: { color: "#f97316", name: "Spectacle" },
  jeuwindows: { color: "#ec4899", name: "PC Games" },
  animation: { color: "#f472b6", name: "Animation" },
  autre: { color: "#94a3b8", name: "Autre" },
};

function getCategoryInfo(categoryId) {
  if (!categoryId) return { color: "#94a3b8", name: "Autre" };
  let normalizedId = categoryId;
  if (categoryId >= 100000) normalizedId = categoryId % 100000;
  if (CATEGORY_MAP[normalizedId]) return CATEGORY_MAP[normalizedId];
  const mainCatDigit = Math.floor(normalizedId / 1000);
  if (MAIN_CATEGORY_FALLBACKS[mainCatDigit]) return MAIN_CATEGORY_FALLBACKS[mainCatDigit];
  return { color: "#94a3b8", name: "Autre" };
}

function getCategoryInfoForStats(categoryId, unifiedCategory) {
  const unifiedKey = String(unifiedCategory || "").trim().toLowerCase();
  if (unifiedKey && UNIFIED_CATEGORY_INFO[unifiedKey]) {
    return UNIFIED_CATEGORY_INFO[unifiedKey];
  }
  return getCategoryInfo(categoryId);
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

  const releaseCategoryPalette = ["#3b82f6", "#22c55e", "#f472b6", "#f59e0b", "#a855f7", "#06b6d4", "#ef4444", "#14b8a6", "#8b5cf6", "#94a3b8"];
  const donutData = (data.releasesByCategory || [])
    .slice()
    .sort((a, b) => (b.count || 0) - (a.count || 0))
    .slice(0, 10)
    .map((category, index) => ({
      key: category.key || "",
      label: category.label || category.key || "Autre",
      value: category.count || 0,
      color: releaseCategoryPalette[index % releaseCategoryPalette.length]
    }));
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
                  const categoryKey = String(r.categoryKey || "").toLowerCase();
                  const categoryLabel = r.categoryLabel || r.categoryKey || "Autre";
                  const categoryColor = releaseColorByKey.get(categoryKey) || "var(--panel-border)";
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
