import React, { useEffect, useSyncExternalStore } from "react";
import { Outlet } from "react-router-dom";
import { apiGet } from "../api/client.js";
import { applyTheme } from "../app/theme.js";
import { applyUiLanguage } from "../app/locale.js";
import { getSessionSnapshot, subscribeSession } from "../auth/session.js";
import Login from "../pages/Login.jsx";
import RestorePendingBanner from "./RestorePendingBanner.jsx";
import Topbar from "./Topbar.jsx";

export default function SetupShell() {
  const { state } = useSyncExternalStore(subscribeSession, getSessionSnapshot, getSessionSnapshot);
  const needsLogin = state?.authRequired && state?.authConfigured && !state?.authenticated;

  useEffect(() => {
    let active = true;
    apiGet("/api/settings/ui")
      .then((ui) => {
        if (!active) return;
        applyTheme(ui?.theme);
        applyUiLanguage(ui?.uiLanguage, true);
      })
      .catch((error) => {
        console.error("Failed to load UI settings theme on setup shell", error);
      });
    return () => {
      active = false;
    };
  }, [state?.authenticated]);

  return (
    <div className="app setup-shell">
      <Topbar onToggleNav={() => {}} />
      <RestorePendingBanner />
      <main className="setup-main">
        {needsLogin && <Login embedded />}
        <Outlet />
      </main>
    </div>
  );
}
