import { useEffect, useRef } from "react";
import { apiGet } from "../api/client.js";
import { updatePosterQueueProgress, completePosterQueue } from "../tasks/posterQueueTasks.js";

/**
 * Hook pour monitorer la queue de posters en arrière-plan
 * Affiche une tâche dans la sidebar quand la queue contient des éléments
 */
export function usePosterQueueMonitoring({ pollIntervalMs = 5000, enabled = true } = {}) {
  const pollRef = useRef(null);

  useEffect(() => {
    if (!enabled) {
      // Nettoyer si désactivé
      if (pollRef.current) {
        clearInterval(pollRef.current);
        pollRef.current = null;
      }
      completePosterQueue();
      return;
    }

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

    // Check immédiat au montage
    checkQueue();

    // Puis polling régulier
    pollRef.current = setInterval(checkQueue, pollIntervalMs);

    return () => {
      if (pollRef.current) {
        clearInterval(pollRef.current);
        pollRef.current = null;
      }
    };
  }, [enabled, pollIntervalMs]);
}
