import { addTask, removeTask } from "../app/taskTracker.js";

/**
 * Helpers pour gérer les tâches de synchronisation RSS
 */

const SYNC_ALL_TASK_KEY = "rss-sync-all";
const SYNC_SINGLE_PREFIX = "rss-sync-";

/**
 * Démarrer une sync globale (toutes les sources)
 */
export function startRssSyncAll(sourceCount = 0) {
  addTask({
    key: SYNC_ALL_TASK_KEY,
    label: "Sync RSS (toutes sources)",
    meta: sourceCount > 0 ? `0/${sourceCount} sources` : "Initialisation...",
    ttlMs: 1000 * 60 * 10, // 10 minutes max
  });
}

/**
 * Mettre à jour la progression de la sync globale
 */
export function updateRssSyncAllProgress(current, total) {
  addTask({
    key: SYNC_ALL_TASK_KEY,
    label: "Sync RSS (toutes sources)",
    meta: `${current}/${total} sources`,
    ttlMs: 1000 * 60 * 10,
  });
}

/**
 * Terminer la sync globale
 */
export function completeRssSyncAll() {
  removeTask(SYNC_ALL_TASK_KEY);
}

/**
 * Démarrer une sync pour une source spécifique
 */
export function startRssSyncSingle(sourceId, sourceName) {
  addTask({
    key: `${SYNC_SINGLE_PREFIX}${sourceId}`,
    label: `Sync: ${sourceName}`,
    meta: "En cours...",
    ttlMs: 1000 * 60 * 5, // 5 minutes max
  });
}

/**
 * Terminer la sync d'une source spécifique
 */
export function completeRssSyncSingle(sourceId) {
  removeTask(`${SYNC_SINGLE_PREFIX}${sourceId}`);
}
