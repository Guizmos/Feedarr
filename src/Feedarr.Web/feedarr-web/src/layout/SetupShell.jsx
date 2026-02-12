import React, { useEffect } from "react";
import { Outlet, useNavigate } from "react-router-dom";
import { apiGet } from "../api/client.js";
import { applyTheme } from "../app/theme.js";
import Topbar from "./Topbar.jsx";

export default function SetupShell() {
  const navigate = useNavigate();

  useEffect(() => {
    let active = true;
    apiGet("/api/settings/ui")
      .then((ui) => {
        if (!active) return;
        applyTheme(ui?.theme);
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
        <Outlet />
      </main>
    </div>
  );
}
