import React from "react";
import { useSubbar } from "./useSubbar.js";

export default function Subbar() {
  const { content } = useSubbar();

  if (!content) return null;

  return (
    <div className="subbar">
      {content}
    </div>
  );
}
