import React from "react";
import Pill from "./Pill.jsx";

export default function ReleaseRow({ item, onDownload, onToggleSeen }) {
  const seen = !!item.seen;

  return (
    <div className={"release" + (seen ? " release--seen" : "")}>
      <div>
        <div className="release__title" title={item.title}>{item.title}</div>
        <div className="release__sub">
          {item.categoryName ? <Pill tone="accent">{item.categoryName}</Pill> : null}
        </div>
      </div>

      <div className="kpi"><span>📦</span><strong>{item.size || "-"}</strong></div>
      <div className="kpi"><span>🌱</span><strong>{item.seeders ?? "-"}</strong></div>
      <div className="kpi"><span>🧲</span><strong>{item.leechers ?? "-"}</strong></div>
      <div className="kpi"><span>🕒</span><strong>{item.date || "-"}</strong></div>

      <div style={{ display: "flex", gap: 8, justifyContent: "flex-end" }}>
        <button className="btn-soft btn-soft--accent" onClick={() => onDownload?.(item)}>
          Download
        </button>
        <button className="btn-soft" onClick={() => onToggleSeen?.(item)}>
          {seen ? "Unseen" : "Seen"}
        </button>
      </div>
    </div>
  );
}
