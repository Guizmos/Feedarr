import React from "react";
import { useSubbar } from "./useSubbar.js";

export default function Subbar() {
  const { content } = useSubbar();

  if (!content) return null;

  const extraClass = React.isValidElement(content) ? content.props?.subbarClassName : "";

  return (
    <div className={`subbar${extraClass ? ` ${extraClass}` : ""}`}>
      {content}
    </div>
  );
}
