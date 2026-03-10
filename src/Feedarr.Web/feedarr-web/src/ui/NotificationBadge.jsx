import React from "react";
import { shouldShowBadge, getBadgeLabel, buildBadgeClass } from "./notificationBadge.js";

export default function NotificationBadge({ value, tone, baseClass, extraClass }) {
  if (!shouldShowBadge(value)) return null;
  return (
    <span className={buildBadgeClass(baseClass, tone, extraClass)}>
      {getBadgeLabel(value)}
    </span>
  );
}
