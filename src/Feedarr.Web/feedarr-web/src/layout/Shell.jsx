import React, { useCallback, useEffect, useRef, useState } from "react";
import { Outlet, useLocation, useNavigate } from "react-router-dom";
import Topbar from "./Topbar.jsx";
import Sidebar from "./Sidebar.jsx";
import Subbar from "./Subbar.jsx";
import { SubbarProvider } from "./SubbarContext.jsx";
import { apiGet, apiPost } from "../api/client.js";
import { applyTheme } from "../app/theme.js";
import { applyUiLanguage } from "../app/locale.js";
import OnboardingWizard from "../ui/OnboardingWizard.jsx";
import { usePosterQueueMonitoring } from "../hooks/usePosterQueueMonitoring.js";
import { usePosterPollingService } from "../hooks/usePosterPollingService.js";

export default function Shell() {
  const navigate = useNavigate();
  const location = useLocation();
  const isSetupRoute = location.pathname.startsWith("/setup");
  const [onboardingOpen, setOnboardingOpen] = useState(false);
  const [onboardingStatus, setOnboardingStatus] = useState(null);
  const [onboardingDismissed, setOnboardingDismissed] = useState(false);
  const [isNavOpen, setIsNavOpen] = useState(false);
  const swipeRef = useRef({
    active: false,
    startX: 0,
    startY: 0,
    isForm: false,
  });

  // Monitoring automatique de la queue de posters
  usePosterQueueMonitoring({
    pollIntervalMs: 5000, // Check toutes les 5 secondes
    enabled: true,
  });

  // Polling intelligent des posters (burst, event-driven)
  usePosterPollingService({
    intervalMs: 12000,
    maxDurationMs: 90000,
    enabled: true,
  });

  useEffect(() => {
    let active = true;
    apiGet("/api/settings/ui")
      .then((ui) => {
        if (!active) return;
        applyTheme(ui?.theme);
        applyUiLanguage(ui?.uiLanguage, true);
      })
      .catch((error) => {
        console.error("Failed to load UI settings theme", error);
      });
    return () => {
      active = false;
    };
  }, []);

  const refreshOnboarding = useCallback(async (opts = {}) => {
    try {
      const status = await apiGet("/api/system/onboarding");
      setOnboardingStatus(status);

      if (!status?.onboardingDone && !isSetupRoute) {
        navigate("/setup", { replace: true });
        return;
      }

      if (!onboardingDismissed && status?.shouldShow && !isSetupRoute) {
        setOnboardingOpen(true);
      }

      if (opts.closeIfDone && status?.onboardingDone) {
        setOnboardingOpen(false);
      }
    } catch (error) {
      console.error("Failed to refresh onboarding status", error);
    }
  }, [onboardingDismissed, navigate, isSetupRoute]);

  useEffect(() => {
    refreshOnboarding();
    const handler = () => refreshOnboarding({ closeIfDone: false });
    window.addEventListener("onboarding:refresh", handler);
    return () => window.removeEventListener("onboarding:refresh", handler);
  }, [refreshOnboarding]);

  async function completeOnboarding() {
    try {
      await apiPost("/api/system/onboarding/complete");
    } catch (error) {
      console.error("Failed to complete onboarding", error);
    }
    setOnboardingOpen(false);
    setOnboardingDismissed(true);
    setOnboardingStatus((prev) =>
      prev ? { ...prev, onboardingDone: true, shouldShow: false } : prev
    );
  }

  const showOnboardingBar = !isSetupRoute && !!onboardingStatus && !onboardingStatus.onboardingDone;
  const openNav = useCallback(() => setIsNavOpen(true), []);
  const closeNav = useCallback(() => setIsNavOpen(false), []);
  const toggleNav = useCallback(() => setIsNavOpen((v) => !v), []);

  useEffect(() => {
    document.body.classList.toggle("nav-open", isNavOpen);
    return () => document.body.classList.remove("nav-open");
  }, [isNavOpen]);

  useEffect(() => {
    if (!isNavOpen) return;
    const onKeyDown = (e) => {
      if (e.key === "Escape") closeNav();
    };
    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  }, [isNavOpen, closeNav]);

  useEffect(() => {
    const edgeZone = 30;
    const threshold = 50;
    const maxVertical = 40;

    const onTouchStart = (e) => {
      if (e.touches.length !== 1) return;
      const target = e.target;
      const tag = target?.tagName?.toLowerCase();
      const isForm =
        tag === "input" ||
        tag === "textarea" ||
        tag === "select" ||
        Boolean(target?.isContentEditable);
      if (isForm) {
        swipeRef.current.active = false;
        return;
      }
      // Block swipe if starting inside a horizontally scrollable container
      let el = target;
      while (el && el !== document.body) {
        if (el.scrollWidth > el.clientWidth + 2 && el.classList?.contains("subbar")) {
          swipeRef.current.active = false;
          return;
        }
        el = el.parentElement;
      }
      const touch = e.touches[0];
      swipeRef.current = {
        active: true,
        startX: touch.clientX,
        startY: touch.clientY,
        isForm: false,
        locked: false,
        nearEdge: touch.clientX <= edgeZone,
      };
    };

    const onTouchMove = (e) => {
      const s = swipeRef.current;
      if (!s.active || e.touches.length !== 1) return;
      const touch = e.touches[0];
      const dx = touch.clientX - s.startX;
      const dy = touch.clientY - s.startY;

      // Block iOS back-swipe gesture when swiping right from left edge
      if (s.nearEdge && dx > 0 && Math.abs(dx) > Math.abs(dy)) {
        e.preventDefault();
      }

      // Early cancel: if user scrolls vertically first, abort
      if (!s.locked && Math.abs(dy) > 8 && Math.abs(dy) > Math.abs(dx)) {
        s.active = false;
        return;
      }
      // Lock as horizontal once dx is meaningful
      if (!s.locked && Math.abs(dx) > 10) s.locked = true;
      if (!s.locked) return;

      if (Math.abs(dy) > maxVertical) {
        s.active = false;
        return;
      }

      // Close drawer: swipe left when open (from anywhere)
      if (isNavOpen) {
        if (dx < -threshold) {
          closeNav();
          s.active = false;
        }
      }
    };

    const onTouchEnd = () => {
      swipeRef.current.active = false;
      swipeRef.current.locked = false;
    };

    window.addEventListener("touchstart", onTouchStart, { passive: true });
    window.addEventListener("touchmove", onTouchMove, { passive: false });
    window.addEventListener("touchend", onTouchEnd);
    window.addEventListener("touchcancel", onTouchEnd);
    return () => {
      window.removeEventListener("touchstart", onTouchStart);
      window.removeEventListener("touchmove", onTouchMove);
      window.removeEventListener("touchend", onTouchEnd);
      window.removeEventListener("touchcancel", onTouchEnd);
    };
  }, [closeNav, isNavOpen, openNav]);

  return (
    <SubbarProvider>
      <div className={`app app-shell ${showOnboardingBar ? "onboarding-bar-visible" : ""} ${isNavOpen ? "is-open" : ""}`}>
        <Topbar onToggleNav={toggleNav} />
        <button
          type="button"
          className="drawer-overlay"
          aria-label="Fermer le menu latéral"
          onClick={closeNav}
        />

        <div className="body">
          <Sidebar onNavigate={closeNav} />

          <div className="content">
            <Subbar />
            <Outlet />
          </div>
        </div>

        {!isSetupRoute && (
          <OnboardingWizard
            open={onboardingOpen}
            status={onboardingStatus}
            onClose={() => {
              setOnboardingOpen(false);
              setOnboardingDismissed(true);
            }}
            onComplete={completeOnboarding}
          />
        )}

        {showOnboardingBar && (
          <div className="onboarding-bar">
            <div className="onboarding-bar__content">
              <div className="onboarding-bar__title">Configuration rapide</div>
              <div className="onboarding-bar__text">
                Ajoute un indexeur et/ou des providers pour activer toutes les fonctionnalités.
              </div>
            </div>
            <button
              className="btn btn-accent"
              type="button"
              onClick={() => {
                setOnboardingDismissed(false);
                setOnboardingOpen(true);
              }}
            >
              Démarrer le wizard
            </button>
          </div>
        )}
      </div>
    </SubbarProvider>
  );
}
