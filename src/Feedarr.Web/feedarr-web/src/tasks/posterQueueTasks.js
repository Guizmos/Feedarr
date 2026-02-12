import { addTask, removeTask } from "../app/taskTracker.js";

/**
 * Helpers pour gérer les tâches de la queue de posters
 */

const QUEUE_TASK_KEY = "poster-queue-active";

/**
 * Démarrer le monitoring de la queue de posters
 * Appelé quand la queue n'est pas vide
 */
export function startPosterQueueMonitoring(queueSize) {
  addTask({
    key: QUEUE_TASK_KEY,
    label: "Récupération posters",
    meta: queueSize > 0 ? `${queueSize} en attente` : "Traitement...",
    ttlMs: 1000 * 60 * 30, // 30 minutes max
  });
}

/**
 * Mettre à jour le nombre d'éléments en queue
 */
export function updatePosterQueueProgress(remaining) {
  if (remaining <= 0) {
    completePosterQueue();
    return;
  }

  addTask({
    key: QUEUE_TASK_KEY,
    label: "Récupération posters",
    meta: `${remaining} en attente`,
    ttlMs: 1000 * 60 * 30,
  });
}

/**
 * Arrêter le monitoring (queue vide)
 */
export function completePosterQueue() {
  removeTask(QUEUE_TASK_KEY);
}
