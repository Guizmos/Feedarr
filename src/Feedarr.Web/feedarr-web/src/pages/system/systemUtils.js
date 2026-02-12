import { fmtBytes as sharedFmtBytes } from "../../utils/formatters.js";

export function fmtTs(tsSeconds) {
  if (!tsSeconds) return "-";
  return new Date(tsSeconds * 1000).toLocaleString("fr-FR");
}

export function fmtUptime(seconds) {
  if (!seconds && seconds !== 0) return "-";
  const s = Math.max(0, Number(seconds) || 0);
  const d = Math.floor(s / 86400);
  const h = Math.floor((s % 86400) / 3600);
  const m = Math.floor((s % 3600) / 60);
  const parts = [];
  if (d) parts.push(`${d}j`);
  if (h || d) parts.push(`${h}h`);
  parts.push(`${m}m`);
  return parts.join(" ");
}

export function fmtBytes(bytes) {
  return sharedFmtBytes(bytes) || "0 B";
}

export function fmtMs(value) {
  const ms = Number(value ?? 0);
  return ms > 0 ? `${ms} ms` : "-";
}
