export const SOURCE_COLOR_PALETTE = [
  "#187d3d",
  "#002c22",
  "#14b8a6",
  "#199e9d",
  "#0ea5e9",
  "#3b82f6",
  "#98e3da",
  "#d6b07e",
  "#eab308",
  "#f59e0b",
  "#8b5cf6",
  "#e50a6c",
  "#ef4444",
];

export function normalizeHexColor(value) {
  if (!value) return null;
  let hex = String(value).trim();
  if (!hex) return null;
  if (!hex.startsWith("#")) hex = `#${hex}`;
  if (hex.length !== 7) return null;
  const body = hex.slice(1);
  if (!/^[0-9a-fA-F]{6}$/.test(body)) return null;
  return `#${body.toLowerCase()}`;
}

export function hexToRgbChannels(value) {
  const hex = normalizeHexColor(value);
  if (!hex) return null;
  const r = parseInt(hex.slice(1, 3), 16);
  const g = parseInt(hex.slice(3, 5), 16);
  const b = parseInt(hex.slice(5, 7), 16);
  return `${r} ${g} ${b}`;
}

export function getSourceColor(sourceId, color) {
  const normalized = normalizeHexColor(color);
  if (normalized) return normalized;
  const palette = SOURCE_COLOR_PALETTE;
  if (palette.length === 0) return "#9ca3af";
  const key = String(sourceId ?? "");
  let hash = 0;
  for (let i = 0; i < key.length; i += 1) {
    hash = (hash * 31 + key.charCodeAt(i)) | 0;
  }
  const idx = Math.abs(hash) % palette.length;
  return palette[idx];
}

function hexToRgb(value) {
  const hex = normalizeHexColor(value);
  if (!hex) return null;
  return {
    r: parseInt(hex.slice(1, 3), 16),
    g: parseInt(hex.slice(3, 5), 16),
    b: parseInt(hex.slice(5, 7), 16),
  };
}

function clampChannel(value) {
  return Math.max(0, Math.min(255, Math.round(value)));
}

function tintChannel(base, amount) {
  return clampChannel(base + (255 - base) * amount);
}

function shadeChannel(base, amount) {
  return clampChannel(base * (1 - amount));
}

function luminance({ r, g, b }) {
  const toLinear = (v) => {
    const s = v / 255;
    return s <= 0.03928 ? s / 12.92 : Math.pow((s + 0.055) / 1.055, 2.4);
  };
  return 0.2126 * toLinear(r) + 0.7152 * toLinear(g) + 0.0722 * toLinear(b);
}

export function buildIndexerPillStyle(color) {
  const rgb = hexToRgb(color);
  if (!rgb) return null;
  const top = {
    r: tintChannel(rgb.r, 0.35),
    g: tintChannel(rgb.g, 0.35),
    b: tintChannel(rgb.b, 0.35),
  };
  const bottom = {
    r: shadeChannel(rgb.r, 0.25),
    g: shadeChannel(rgb.g, 0.25),
    b: shadeChannel(rgb.b, 0.25),
  };
  const border = {
    r: shadeChannel(rgb.r, 0.35),
    g: shadeChannel(rgb.g, 0.35),
    b: shadeChannel(rgb.b, 0.35),
  };
  const shadow = {
    r: shadeChannel(rgb.r, 0.45),
    g: shadeChannel(rgb.g, 0.45),
    b: shadeChannel(rgb.b, 0.45),
  };
  const text = luminance(rgb) > 0.6 ? "#2f1c00" : "#f8fafc";
  return {
    background: `linear-gradient(135deg, rgb(${top.r}, ${top.g}, ${top.b}) 0%, rgb(${rgb.r}, ${rgb.g}, ${rgb.b}) 45%, rgb(${bottom.r}, ${bottom.g}, ${bottom.b}) 100%)`,
    borderColor: `rgba(${border.r}, ${border.g}, ${border.b}, 0.7)`,
    color: text,
    boxShadow: `0 2px 4px rgba(${shadow.r}, ${shadow.g}, ${shadow.b}, 0.28)`,
  };
}
