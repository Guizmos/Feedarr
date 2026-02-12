import { addTask, removeTask } from "../app/taskTracker.js";

/**
 * Helpers pour gérer les tâches de refresh de catégories (caps)
 */

const CAPS_PREFIX = "refresh-caps-";

/**
 * Démarrer le refresh des catégories pour un indexer
 */
export function startCapsRefresh(sourceId, sourceName) {
  addTask({
    key: `${CAPS_PREFIX}${sourceId}`,
    label: `Refresh caps: ${sourceName}`,
    meta: "Récupération...",
    ttlMs: 1000 * 60 * 3, // 3 minutes max
  });
}

/**
 * Terminer le refresh des catégories
 */
export function completeCapsRefresh(sourceId) {
  removeTask(`${CAPS_PREFIX}${sourceId}`);
}
