import React, { useEffect, useSyncExternalStore } from "react";
import { Outlet, useNavigate } from "react-router-dom";
import { apiGet } from "../api/client.js";
import { applyTheme } from "../app/theme.js";
import { applyUiLanguage } from "../app/locale.js";
import Topbar from "./Topbar.jsx";
import { getSessionSnapshot, subscribeSession } from "../auth/session.js";
import Login from "../pages/Login.jsx";

export default function SetupShell() {
  const navigate = useNavigate();
  const { state } = useSyncExternalStore(
    subscribeSession, getSessionSnapshot, getSessionSnapshot
  );
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
  }, []);

  useEffect(() => {
    let active = true;
    apiGet("/api/system/onboarding")
      .then((status) => {
        if (!active) return;
        if (status?.onboardingDone) {
          navigate("/library", { replace: true });
        }
      })
      .catch((error) => {
        console.error("Failed to load onboarding status on setup shell", error);
      });
    return () => {
      active = false;
    };
  }, [navigate]);

  return (
    <div className="app setup-shell">
      <Topbar onToggleNav={() => {}} />
      <main className="setup-main">
        {needsLogin && <div className="auth-setup-login"><Login embedded /></div>}
        <Outlet />
      </main>
    </div>
  );
}
