import { addTask, updateTask, removeTask } from "../app/taskTracker.js";

/**
 * Helpers pour gérer les tâches de retro-fetch de posters
 */

const RETRO_FETCH_TASK_KEY = "retro-fetch-posters";

export function startRetroFetch(total = 0) {
  addTask({
    key: RETRO_FETCH_TASK_KEY,
    label: "Récupération de posters",
    meta: total > 0 ? `0/${total} (0%)` : "Initialisation...",
    ttlMs: 1000 * 60 * 60, // 1 heure max
  });
}

export function updateRetroFetchProgress(done, total) {
  const percent = total > 0 ? Math.round((done / total) * 100) : 0;
  updateTask(RETRO_FETCH_TASK_KEY, {
    meta: `${done}/${total} (${percent}%)`,
  });
}

export function completeRetroFetch() {
  removeTask(RETRO_FETCH_TASK_KEY);
}
