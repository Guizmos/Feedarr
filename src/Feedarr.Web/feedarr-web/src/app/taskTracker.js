const TASKS_KEY = "feedarr:tasks";

function notify() {
  if (typeof window !== "undefined") {
    window.dispatchEvent(new Event("tasks:updated"));
  }
}

function readTasks() {
  if (typeof window === "undefined") return [];
  try {
    const raw = window.localStorage.getItem(TASKS_KEY);
    const parsed = raw ? JSON.parse(raw) : [];
    if (!Array.isArray(parsed)) return [];
    const now = Date.now();
    return parsed.filter((t) => !t?.expiresAt || t.expiresAt > now);
  } catch {
    return [];
  }
}

function writeTasks(tasks) {
  if (typeof window === "undefined") return;
  try {
    window.localStorage.setItem(TASKS_KEY, JSON.stringify(tasks));
  } catch {
    // ignore
  }
}

export function getTasks() {
  return readTasks();
}

export function addTask({ key, label, meta, ttlMs } = {}) {
  if (!key) return;
  const tasks = readTasks();
  const now = Date.now();
  const next = tasks.filter((t) => t?.key && t.key !== key);
  next.push({
    key,
    label: label || key,
    meta: meta || "",
    startedAt: now,
    expiresAt: ttlMs ? now + Number(ttlMs) : null,
  });
  writeTasks(next);
  notify();
}

export function updateTask(key, patch = {}) {
  if (!key) return;
  const tasks = readTasks();
  let updated = false;
  const next = tasks.map((t) => {
    if (t?.key !== key) return t;
    updated = true;
    return { ...t, ...patch };
  });
  if (!updated) return;
  writeTasks(next);
  notify();
}

export function removeTask(key) {
  if (!key) return;
  const tasks = readTasks();
  const next = tasks.filter((t) => t?.key && t.key !== key);
  writeTasks(next);
  notify();
}

// Fonctions de développement/debug (exposées dans window en mode dev)
if (typeof window !== "undefined" && import.meta.env?.DEV) {
  window._feedarr_tasks = {
    list: getTasks,
    add: addTask,
    update: updateTask,
    remove: removeTask,
    // Fonction de test pour ajouter rapidement une tâche
    test: (label = "Test task", meta = "En cours...") => {
      addTask({
        key: `test-${Date.now()}`,
        label,
        meta,
        ttlMs: 60000, // Expire après 1 minute
      });
      console.log("✅ Tâche de test ajoutée:", label);
    },
    // Nettoyer toutes les tâches
    clear: () => {
      if (typeof window !== "undefined") {
        window.localStorage.removeItem(TASKS_KEY);
        notify();
        console.log("✅ Toutes les tâches ont été supprimées");
      }
    },
  };
  console.log(
    "🔧 Dev tools disponibles: window._feedarr_tasks\n" +
    "  - test(label, meta) : ajouter une tâche de test\n" +
    "  - list() : lister les tâches\n" +
    "  - clear() : supprimer toutes les tâches"
  );
}
