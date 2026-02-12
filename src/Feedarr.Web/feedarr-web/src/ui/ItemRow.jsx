import React from "react";
import AppIcon from "./AppIcon.jsx";
import ToggleSwitch from "./ToggleSwitch.jsx";

/**
 * IconBtn - Bouton avec icône lucide-react
 */
export function IconBtn({ icon, title, onClick, disabled, className }) {
  return (
    <button
      className={`iconbtn${className ? ` ${className}` : ""}`}
      onClick={onClick}
      disabled={disabled}
      title={title}
    >
      <AppIcon name={icon} />
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
  id,
  title,
  meta,
  enabled = true,
  badges,
  actions = [],
  showToggle = true,
  onToggle,
  toggleDisabled = false,
  statusClass = "",
  className = "",
}) {
  const cardClasses = [
    "indexer-card",
    !enabled && "is-disabled",
    statusClass,
    className,
  ].filter(Boolean).join(" ");

  return (
    <div className={cardClasses}>
      <div className="indexer-row">
        {/* Dot indicator */}
        <span className={`dot ${enabled ? "ok" : "off"}`} />

        {/* ID */}
        <span className="indexer-id">{id}</span>

        {/* Title */}
        <span className="indexer-title">{title}</span>

        {/* Meta / URL */}
        {meta && <span className="indexer-url">{meta}</span>}

        {/* Badges / Categories */}
        {badges && badges.length > 0 && (
          <div className="indexer-categories">
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
          </div>
        )}

        {/* Actions */}
        <div className="indexer-actions">
          {actions.map((action, idx) => (
            <IconBtn
              key={idx}
              icon={action.spinning ? "progress_activity" : action.icon}
              title={action.title}
              onClick={action.onClick}
              disabled={action.disabled}
              className={[
                action.className,
                action.spinning && "iconbtn--spin",
              ].filter(Boolean).join(" ")}
            />
          ))}

          {showToggle && (
            <ToggleSwitch
              checked={enabled}
              onIonChange={onToggle}
              disabled={toggleDisabled}
              title={enabled ? "Désactiver" : "Activer"}
            />
          )}
        </div>
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
