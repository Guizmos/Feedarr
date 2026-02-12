import { addTask, removeTask } from "../app/taskTracker.js";

/**
 * Helpers pour gérer les tâches de tests d'indexers
 */

const TEST_PREFIX = "test-indexer-";

/**
 * Démarrer le test d'un indexer
 */
export function startIndexerTest(sourceId, sourceName) {
  addTask({
    key: `${TEST_PREFIX}${sourceId}`,
    label: `Test: ${sourceName}`,
    meta: "Test en cours...",
    ttlMs: 1000 * 60 * 2, // 2 minutes max
  });
}

/**
 * Terminer le test d'un indexer
 */
export function completeIndexerTest(sourceId) {
  removeTask(`${TEST_PREFIX}${sourceId}`);
}

/**
 * Démarrer le test d'un nouvel indexer (lors de la création)
 */
export function startNewIndexerTest() {
  addTask({
    key: "test-new-indexer",
    label: "Test nouvel indexer",
    meta: "Validation en cours...",
    ttlMs: 1000 * 60 * 2,
  });
}

/**
 * Terminer le test d'un nouvel indexer
 */
export function completeNewIndexerTest() {
  removeTask("test-new-indexer");
}
