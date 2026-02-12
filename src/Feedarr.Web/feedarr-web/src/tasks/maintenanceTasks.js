import { addTask, removeTask } from "../app/taskTracker.js";

/**
 * Helpers pour gérer les tâches de maintenance
 */

const CLEAR_CACHE_KEY = "maintenance-clear-cache";
const PURGE_LOGS_KEY = "maintenance-purge-logs";
const BACKUP_KEY = "maintenance-backup";

/**
 * Démarrer le nettoyage du cache des posters
 */
export function startClearPosterCache() {
  addTask({
    key: CLEAR_CACHE_KEY,
    label: "Nettoyage cache posters",
    meta: "Suppression en cours...",
    ttlMs: 1000 * 60 * 5, // 5 minutes max
  });
}

/**
 * Terminer le nettoyage du cache
 */
export function completeClearPosterCache() {
  removeTask(CLEAR_CACHE_KEY);
}

/**
 * Démarrer la purge des logs
 */
export function startPurgeLogs() {
  addTask({
    key: PURGE_LOGS_KEY,
    label: "Purge des logs",
    meta: "Suppression en cours...",
    ttlMs: 1000 * 60 * 3, // 3 minutes max
  });
}

/**
 * Terminer la purge des logs
 */
export function completePurgeLogs() {
  removeTask(PURGE_LOGS_KEY);
}

/**
 * Démarrer une sauvegarde
 */
export function startBackup() {
  addTask({
    key: BACKUP_KEY,
    label: "Sauvegarde",
    meta: "Création en cours...",
    ttlMs: 1000 * 60 * 10, // 10 minutes max
  });
}

/**
 * Mettre à jour la progression du backup
 */
export function updateBackupProgress(meta) {
  addTask({
    key: BACKUP_KEY,
    label: "Sauvegarde",
    meta,
    ttlMs: 1000 * 60 * 10,
  });
}

/**
 * Terminer la sauvegarde
 */
export function completeBackup() {
  removeTask(BACKUP_KEY);
}
