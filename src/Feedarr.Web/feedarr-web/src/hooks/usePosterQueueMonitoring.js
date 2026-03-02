import { useEffect } from "react";
import { apiGet } from "../api/client.js";
import { updatePosterQueueProgress, completePosterQueue } from "../tasks/posterQueueTasks.js";
import usePolling from "./usePolling.js";

/**
 * Hook pour monitorer la queue de posters en arrière-plan
 * Affiche une tâche dans la sidebar quand la queue contient des éléments
 */
export function usePosterQueueMonitoring({ pollIntervalMs = 5000, enabled = true } = {}) {
  async function checkQueue() {
    try {
      const status = await apiGet("/api/posters/queue/status");
      const queueSize = Number(status?.queueSize ?? 0);

      if (queueSize > 0) {
        updatePosterQueueProgress(queueSize);
      } else {
        completePosterQueue();
      }
    } catch {
      // Ignorer silencieusement les erreurs
      // La tâche sera nettoyée après le TTL
    }
  }

  // Nettoyer la tâche quand le monitoring est désactivé
  useEffect(() => {
    if (!enabled) completePosterQueue();
  }, [enabled]);

  usePolling(checkQueue, pollIntervalMs, enabled);
}
