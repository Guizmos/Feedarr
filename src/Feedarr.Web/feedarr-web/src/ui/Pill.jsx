import React from "react";

export default function Pill({ children, tone = "default" }) {
  return <span className={`pill pill--${tone}`}>{children}</span>;
}
