import React, { useEffect, useRef, useState } from "react";
import { NavLink, useLocation } from "react-router-dom";
import useBadges from "../hooks/useBadges.js";
import useTasks from "../hooks/useTasks.js";
import AppIcon from "../ui/AppIcon.jsx";

function Item({ to, icon, label, badge, end = true, onNavigate, forceActive = false, animateOnIncrease = false }) {
  const normalized =
    badge && typeof badge === "object"
      ? badge
      : { value: badge, tone: badge === "warn" ? "warn" : undefined };

  const badgeValue = normalized?.value;
  const badgeTone = normalized?.tone;
  const showBadge = badgeValue != null && badgeValue !== false && badgeValue !== 0;
  const badgeLabel = badgeValue === "warn" ? "!" : badgeValue;
  const [isBumping, setIsBumping] = useState(false);
  const prevNumericRef = useRef(0);
  const bumpTimerRef = useRef(null);
  const badgeClass = "snav__badge"
    + (badgeTone ? ` snav__badge--${badgeTone}` : "")
    + (isBumping ? " snav__badge--pulse" : "");

  useEffect(() => {
    const numericValue = typeof badgeValue === "number"
      ? badgeValue
      : Number.isFinite(Number(badgeValue))
        ? Number(badgeValue)
        : 0;
    const previous = prevNumericRef.current;
    prevNumericRef.current = numericValue;

    if (animateOnIncrease && numericValue > previous && numericValue > 0) {
      setIsBumping(false);
      const raf = requestAnimationFrame(() => setIsBumping(true));
      if (bumpTimerRef.current) clearTimeout(bumpTimerRef.current);
      bumpTimerRef.current = setTimeout(() => setIsBumping(false), 460);
      return () => cancelAnimationFrame(raf);
    }
  }, [animateOnIncrease, badgeValue]);

  useEffect(() => () => {
    if (bumpTimerRef.current) clearTimeout(bumpTimerRef.current);
  }, []);

  return (
    <NavLink
      to={to}
      className={({ isActive }) => "snav__item" + (isActive || forceActive ? " is-active" : "")}
      end={end}
      onClick={onNavigate}
    >
      <AppIcon name={icon} className="snav__icon" />
      <span className="snav__label">{label}</span>

      {/* badge: nombre (ex: 3) ou "warn" */}
      {showBadge && <span className={badgeClass}>{badgeLabel}</span>}
    </NavLink>
  );
}

export default function Sidebar({ onNavigate }) {
  const {
    activity,
    activityTone,
    system: systemBadge,
    releases,
    latestActivityTs,
    lastSeenActivityTs,
    markActivitySeen,
    latestReleasesCount,
    latestReleasesTs,
    lastSeenReleasesTs,
    markReleasesSeen,
  } = useBadges({
    pollMs: 25000,
    activityLimit: 200,
  });
  const tasks = useTasks();
  const location = useLocation();
  const isIndexers = location.pathname.startsWith("/indexers");
  const isSettings = location.pathname.startsWith("/settings") || isIndexers;
  const isSystem = location.pathname.startsWith("/system");
  const path = location.pathname;
  const isLibrary = path.startsWith("/library");
  const isLogs = path.startsWith("/activity");

  useEffect(() => {
    if (isLogs && latestActivityTs > 0 && latestActivityTs > lastSeenActivityTs) {
      markActivitySeen(latestActivityTs);
    }
  }, [isLogs, latestActivityTs, lastSeenActivityTs, markActivitySeen]);

  useEffect(() => {
    if (!isLibrary) return;
    const hasNewTs = latestReleasesTs > 0 && latestReleasesTs > lastSeenReleasesTs;
    if (hasNewTs) {
      markReleasesSeen(latestReleasesCount, latestReleasesTs);
    }
  }, [
    isLibrary,
    latestReleasesCount,
    latestReleasesTs,
    lastSeenReleasesTs,
    markReleasesSeen,
  ]);

  return (
    <aside className="sidebar drawer">
      <div className="snav">
        <Item
          to="/library"
          icon="video_library"
          label="Bibliothèque"
          badge={!isLibrary && releases ? releases : null}
          animateOnIncrease
          onNavigate={onNavigate}
        />

        {isLibrary && (
          <div className="snav__sub">
            <NavLink
              to="/library/top"
              className={() => "snav__subitem" + (path === "/library/top" ? " is-active" : "")}
              onClick={onNavigate}
            >
              <AppIcon name="military_tech" className="snav__subicon" />
              Top 24h
            </NavLink>
          </div>
        )}

        <Item
          to="/settings"
          icon="settings"
          label="Paramètres"
          end={false}
          onNavigate={onNavigate}
          forceActive={isIndexers}
        />

        {isSettings && (
          <div className="snav__sub">
            <NavLink
              to="/settings/ui"
              className={() => "snav__subitem" + (path === "/settings/ui" ? " is-active" : "")}
              onClick={onNavigate}
            >
              UI
            </NavLink>
            <NavLink
              to="/settings/providers"
              className={() => "snav__subitem" + (path === "/settings/providers" ? " is-active" : "")}
              onClick={onNavigate}
            >
              Fournisseurs
            </NavLink>
            <NavLink
              to="/indexers"
              className={() => "snav__subitem" + (isIndexers ? " is-active" : "")}
              onClick={onNavigate}
            >
              Indexeurs
            </NavLink>
            <NavLink
              to="/settings/externals"
              className={() => "snav__subitem" + (path === "/settings/externals" ? " is-active" : "")}
              onClick={onNavigate}
            >
              Providers
            </NavLink>
            <NavLink
              to="/settings/applications"
              className={() => "snav__subitem" + (path === "/settings/applications" ? " is-active" : "")}
              onClick={onNavigate}
            >
              Applications
            </NavLink>
            <NavLink
              to="/settings/users"
              className={() => "snav__subitem" + (path === "/settings/users" ? " is-active" : "")}
              onClick={onNavigate}
            >
              Utilisateurs
            </NavLink>
            <NavLink
              to="/settings/maintenance"
              className={() => "snav__subitem" + (path === "/settings/maintenance" ? " is-active" : "")}
              onClick={onNavigate}
            >
              Maintenance
            </NavLink>
            <NavLink
              to="/settings/backup"
              className={() => "snav__subitem" + (path === "/settings/backup" ? " is-active" : "")}
              onClick={onNavigate}
            >
              Sauvegarde
            </NavLink>
          </div>
        )}

        {/* badge warning "!" */}
        <Item
          to="/system"
          icon="dns"
          label="Système"
          badge={!isSystem && systemBadge ? { value: "warn", tone: systemBadge } : null}
          end={false}
          onNavigate={onNavigate}
        />

        {isSystem && (
          <div className="snav__sub">
            <NavLink
              to="/system/indexers"
              className={() => "snav__subitem" + (path === "/system/indexers" ? " is-active" : "")}
              onClick={onNavigate}
            >
              Indexeurs
            </NavLink>
            <NavLink
              to="/system/providers"
              className={() => "snav__subitem" + (path === "/system/providers" ? " is-active" : "")}
              onClick={onNavigate}
            >
              Providers
            </NavLink>
            <NavLink
              to="/system/statistics"
              className={() => "snav__subitem" + (path === "/system/statistics" ? " is-active" : "")}
              onClick={onNavigate}
            >
              Statistiques
            </NavLink>
            <NavLink
              to="/system/storage"
              className={() => "snav__subitem" + (path === "/system/storage" ? " is-active" : "")}
              onClick={onNavigate}
            >
              Stockage
            </NavLink>
            <NavLink
              to="/system/volumes"
              className={() => "snav__subitem" + (path === "/system/volumes" ? " is-active" : "")}
              onClick={onNavigate}
            >
              Volume
            </NavLink>
          </div>
        )}

        {/* badge numérique */}
        <Item
          to="/history"
          icon="history"
          label="Historique"
          onNavigate={onNavigate}
        />
        <Item
          to="/activity"
          icon="schedule"
          label="Logs"
          badge={!isLogs && activity > 0 ? { value: activity, tone: activityTone } : null}
          onNavigate={onNavigate}
        />
      </div>

      {tasks.length > 0 && (
        <div className="sidebar__tasks">
          {/* Tâches du taskTracker (incluant retro-fetch) */}
          {tasks.map((task) => (
            <div key={task.key} className="sidebar__task" title={task.label}>
              <span className="sidebar__task-label">{task.label}</span>
              {task.meta && (
                <span className="sidebar__task-meta">{task.meta}</span>
              )}
            </div>
          ))}
        </div>
      )}
    </aside>
  );
}
