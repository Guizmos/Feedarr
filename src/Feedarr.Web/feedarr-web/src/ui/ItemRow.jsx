import React from "react";
import AppIcon from "./AppIcon.jsx";
import ToggleSwitch from "./ToggleSwitch.jsx";

function AutoScrollLine({ className = "", title, children }) {
  const outerRef = React.useRef(null);
  const innerRef = React.useRef(null);
  const rafRef = React.useRef(0);
  const hoveredRef = React.useRef(false);

  const getMaxOffset = React.useCallback(() => {
    const outer = outerRef.current;
    const inner = innerRef.current;
    if (!outer || !inner) return 0;

    // Use both layout and rendered widths to avoid sub-pixel false negatives.
    const outerWidth = outer.getBoundingClientRect().width || outer.clientWidth || 0;
    const innerWidth = Math.max(inner.scrollWidth || 0, inner.getBoundingClientRect().width || 0);
    return Math.max(Math.ceil(innerWidth - outerWidth), 0);
  }, []);

  const stop = React.useCallback(() => {
    hoveredRef.current = false;
    if (rafRef.current) {
      cancelAnimationFrame(rafRef.current);
      rafRef.current = 0;
    }
    if (innerRef.current) {
      innerRef.current.style.transform = "translateX(0px)";
    }
  }, []);

  const start = React.useCallback(() => {
    const outer = outerRef.current;
    const inner = innerRef.current;
    if (!outer || !inner) return;

    const reduceMotion = typeof window !== "undefined"
      && window.matchMedia
      && window.matchMedia("(prefers-reduced-motion: reduce)").matches;
    if (reduceMotion) return;

    const max = getMaxOffset();
    if (max <= 0) return;

    hoveredRef.current = true;
    let offset = 0;
    let direction = 1;
    let last = performance.now();
    let pauseUntil = last + 400;
    const speedPxPerSec = 45;

    const step = (now) => {
      if (!hoveredRef.current) return;
      const currentOuter = outerRef.current;
      const currentInner = innerRef.current;
      if (!currentOuter || !currentInner) return;

      const currentMax = getMaxOffset();
      if (currentMax <= 0) {
        currentInner.style.transform = "translateX(0px)";
        return;
      }

      if (now >= pauseUntil) {
        const dt = Math.max(0, (now - last) / 1000);
        offset += direction * speedPxPerSec * dt;
        if (offset >= currentMax) {
          offset = currentMax;
          direction = -1;
          pauseUntil = now + 700;
        } else if (offset <= 0) {
          offset = 0;
          direction = 1;
          pauseUntil = now + 500;
        }
        currentInner.style.transform = `translateX(${-offset}px)`;
      }
      last = now;
      rafRef.current = requestAnimationFrame(step);
    };

    if (rafRef.current) cancelAnimationFrame(rafRef.current);
    rafRef.current = requestAnimationFrame(step);
  }, [getMaxOffset]);

  React.useEffect(() => stop, [stop]);

  return (
    <span
      ref={outerRef}
      className={`line-scroll ${className}`.trim()}
      title={title}
      onMouseEnter={start}
      onMouseLeave={stop}
      onPointerEnter={start}
      onPointerLeave={stop}
    >
      <span ref={innerRef} className="line-scroll__content">
        {children}
      </span>
    </span>
  );
}

/**
 * IconBtn - Bouton avec icône lucide-react
 */
export function IconBtn({ icon, title, label, onClick, disabled, className }) {
  const actionLabel = className?.includes("itemrow__action") ? label : "";
  return (
    <button
      className={`iconbtn${className ? ` ${className}` : ""}`}
      onClick={onClick}
      disabled={disabled}
      title={title}
      type="button"
    >
      <AppIcon name={icon} />
      {actionLabel && <span className="itemrow__action-label">{actionLabel}</span>}
    </button>
  );
}

/**
 * ItemRow - Composant réutilisable pour les lignes (Indexers, Providers, Applications)
 *
 * Props:
 * - id: Identifiant à afficher
 * - title: Titre principal
 * - meta: Texte secondaire (URL, stats, etc.)
 * - enabled: État activé/désactivé
 * - badges: Array de badges [{label, className}] ou éléments React
 * - actions: Array d'actions [{icon, title, onClick, disabled, className, spinning}]
 * - showToggle: Afficher le toggle on/off
 * - onToggle: Callback pour le toggle
 * - toggleDisabled: Désactiver le toggle
 * - statusClass: Classes de statut additionnelles (test-ok, test-err, sync-ok, sync-err)
 * - className: Classes additionnelles pour la carte
 */
export default function ItemRow({
  id: _id,
  title,
  meta,
  metaSub,
  enabled = true,
  badges,
  actions = [],
  showToggle = true,
  onToggle,
  toggleDisabled = false,
  statusClass = "",
  className = "",
}) {
  const actionLabelByIcon = {
    sync: "Sync",
    science: "Tester",
    edit: "Modifier",
    delete: "Supprimer",
  };

  const cardClasses = [
    "indexer-card",
    "itemrow",
    !enabled && "is-disabled",
    statusClass,
    className,
  ].filter(Boolean).join(" ");

  return (
    <div className={cardClasses}>
      <div className="itemrow__body">
        <div className="itemrow__head">
          <span className={`itemrow__statusdot ${enabled ? "ok" : "off"}`} />
          <AutoScrollLine className="itemrow__title" title={title}>
            {title}
          </AutoScrollLine>
          {showToggle && (
            <div className="itemrow__toggle itemrow__toggle--head" title={enabled ? "Actif" : "Inactif"}>
              <ToggleSwitch
                checked={enabled}
                onIonChange={onToggle}
                disabled={toggleDisabled}
                title={enabled ? "Désactiver" : "Activer"}
              />
            </div>
          )}
        </div>
        {meta && (
          <AutoScrollLine className="itemrow__meta" title={meta}>
            {meta}
          </AutoScrollLine>
        )}
        {metaSub && (
          <AutoScrollLine className="itemrow__meta-sub" title={metaSub}>
            {metaSub}
          </AutoScrollLine>
        )}
        {badges && badges.length > 0 && (
          <AutoScrollLine className="itemrow__badges" title="Badges">
            {badges.map((badge, idx) =>
              React.isValidElement(badge) ? (
                <React.Fragment key={idx}>{badge}</React.Fragment>
              ) : (
                <span
                  key={idx}
                  className={`pill ${badge.className || ""}`}
                  title={badge.title}
                >
                  {badge.label}
                </span>
              )
            )}
          </AutoScrollLine>
        )}
      </div>
      <div className={`itemrow__footer itemrow__footer--count-${actions.length}`}>
        {actions.map((action, idx) => {
          const label = action.label || actionLabelByIcon[action.icon] || "Action";
          return (
            <IconBtn
              key={idx}
              icon={action.spinning ? "progress_activity" : action.icon}
              title={action.title || label}
              label={label}
              onClick={action.onClick}
              disabled={action.disabled}
              className={[
                "itemrow__action",
                `itemrow__action--${String(action.icon || "").toLowerCase()}`,
                action.className,
                action.spinning && "iconbtn--spin",
              ].filter(Boolean).join(" ")}
            />
          );
        })}
      </div>
    </div>
  );
}

/**
 * CategoryBubble - Bulle de catégorie (pour Indexers)
 */
export function CategoryBubble({ unifiedKey, label, title }) {
  return (
    <span
      className={`cat-bubble cat-bubble--${unifiedKey || "unknown"}`}
      title={title}
    >
      {label}
    </span>
  );
}
