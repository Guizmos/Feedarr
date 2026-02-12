import { useEffect, useState } from "react";
import { getTasks } from "../app/taskTracker.js";

/**
 * Hook React pour écouter et afficher les tâches actives
 * Synchronisé avec le taskTracker via localStorage et événements
 */
export default function useTasks() {
  const [tasks, setTasks] = useState(() => getTasks());

  useEffect(() => {
    // Fonction pour rafraîchir les tâches
    const refresh = () => {
      setTasks(getTasks());
    };

    // Écouter les changements de tâches
    window.addEventListener("tasks:updated", refresh);

    // Rafraîchir au montage pour être sûr d'avoir les dernières données
    refresh();

    return () => {
      window.removeEventListener("tasks:updated", refresh);
    };
  }, []);

  return tasks;
}
