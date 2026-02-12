# Système de Tâches - Feedarr

## 📋 Description

Le système de tâches permet d'afficher en temps réel les opérations en cours dans la sidebar, en bas à gauche de l'interface.

## 🏗️ Architecture

### Fichiers principaux

- **`taskTracker.js`** : Système central de gestion des tâches (localStorage + événements)
- **`useTasks.js`** : Hook React pour écouter et afficher les tâches
- **`tasks/syncTasks.js`** : Helpers pour les sync RSS (globale et par source)
- **`tasks/retroFetchTasks.js`** : Helpers pour le retro-fetch de posters
- **`tasks/indexerTasks.js`** : Helpers pour les tests d'indexers
- **`tasks/categoriesTasks.js`** : Helpers pour le refresh des catégories (caps)
- **`tasks/posterQueueTasks.js`** : Helpers pour la queue de posters
- **`tasks/maintenanceTasks.js`** : Helpers pour les opérations de maintenance
- **`Sidebar.jsx`** : Composant qui affiche les tâches actives

### CSS

Les classes CSS sont dans `styles.css` :
- `.sidebar__tasks` : Conteneur des tâches
- `.sidebar__task` : Une tâche individuelle
- `.sidebar__task-label` : Label de la tâche
- `.sidebar__task-meta` : Métadonnées (progression, pourcentage, etc.)

## 🧪 Comment tester

### 1. Via la console du navigateur (Mode Dev uniquement)

Ouvrez la console de votre navigateur (F12) et utilisez :

```javascript
// Ajouter une tâche de test
window._feedarr_tasks.test("Ma tâche de test", "50% - En cours")

// Ajouter une tâche personnalisée
window._feedarr_tasks.add({
  key: "my-task",
  label: "Import de données",
  meta: "25/100 fichiers",
  ttlMs: 60000 // Expire après 1 minute
})

// Mettre à jour une tâche
window._feedarr_tasks.update("my-task", { meta: "50/100 fichiers" })

// Supprimer une tâche
window._feedarr_tasks.remove("my-task")

// Lister toutes les tâches
window._feedarr_tasks.list()

// Supprimer toutes les tâches
window._feedarr_tasks.clear()
```

### 2. Via le code React

Dans n'importe quel composant React :

```javascript
import { addTask, updateTask, removeTask } from "../app/taskTracker.js";

// Ajouter une tâche
addTask({
  key: "import-data",
  label: "Import de données",
  meta: "En cours...",
  ttlMs: 300000 // Expire après 5 minutes
});

// Mettre à jour la progression
updateTask("import-data", {
  meta: "50/100 fichiers (50%)"
});

// Supprimer quand terminé
removeTask("import-data");
```

### 3. Via les helpers spécialisés

Pour les tâches de synchronisation RSS :

```javascript
import { startRssSync, updateRssSyncProgress, completeRssSync } from "../tasks/syncTasks.js";

// Démarrer
startRssSync(5); // 5 sources

// Progression
updateRssSyncProgress(3, 5); // 3/5 sources

// Terminer
completeRssSync();
```

Pour les tâches de retro-fetch :

```javascript
import { startRetroFetch, updateRetroFetchProgress, completeRetroFetch } from "../tasks/retroFetchTasks.js";

// Démarrer
startRetroFetch(150); // 150 posters à récupérer

// Progression
updateRetroFetchProgress(75, 150); // 75/150 (50%)

// Terminer
completeRetroFetch();
```

## 🔄 Fonctionnement

1. **Ajout d'une tâche** : `addTask()` enregistre la tâche dans localStorage et émet un événement
2. **Hook React** : `useTasks()` écoute les événements et met à jour l'état
3. **Affichage** : La Sidebar reçoit la liste des tâches et les affiche
4. **Mise à jour** : `updateTask()` modifie la tâche et émet un événement
5. **Suppression** : `removeTask()` supprime la tâche (ou expiration automatique si ttlMs défini)

## 📝 Format d'une tâche

```javascript
{
  key: "task-unique-id",        // Identifiant unique
  label: "Ma tâche",            // Label affiché
  meta: "50% (25/50)",          // Métadonnées (progression, etc.)
  startedAt: 1234567890,        // Timestamp de création
  expiresAt: 1234567890         // Timestamp d'expiration (optionnel)
}
```

## 💡 Bonnes pratiques

1. **Utilisez des clés uniques** : Évitez les collisions (ex: `rss-sync`, `retro-fetch-posters`)
2. **Définissez un TTL** : Les tâches expirent automatiquement pour éviter les fuites
3. **Mettez à jour régulièrement** : Affichez la progression en temps réel
4. **Supprimez quand terminé** : N'oubliez pas de `removeTask()` à la fin
5. **Utilisez les helpers** : Préférez `syncTasks.js` et `retroFetchTasks.js` pour les cas courants

## 🎨 Personnalisation CSS

Les tâches utilisent les variables CSS globales :
- `--sidebar-text` : Couleur du texte
- `--sidebar-muted` : Couleur du texte secondaire

Pour personnaliser, modifiez `.sidebar__task` dans `styles.css`.

## 📦 Tâches intégrées

### ✅ Opérations actuellement trackées dans la sidebar

1. **Retro fetch de posters** (`retro-fetch-posters`)
   - Démarrage automatique depuis Settings → UI → Posters
   - Progression en temps réel (pourcentage et compteurs)
   - Synchronisé avec `useRetroFetchProgress`
   - Fichier: [useRetroFetchProgress.js](../hooks/useRetroFetchProgress.js)

2. **Sync RSS individuelle** (`rss-sync-{sourceId}`)
   - Déclenchée lors du sync manuel d'une source depuis Indexers
   - Label: "Sync: {nom de la source}"
   - Fichier: [Indexers.jsx:485](../pages/Indexers.jsx#L485)

3. **Test d'indexer** (`test-indexer-{sourceId}`)
   - Déclenchée lors du test d'une source depuis Indexers
   - Label: "Test: {nom de la source}"
   - Fichier: [Indexers.jsx:527](../pages/Indexers.jsx#L527)

4. **Test nouvel indexer** (`test-new-indexer`)
   - Déclenchée lors de la création d'un nouvel indexer
   - Label: "Test nouvel indexer"
   - Fichier: [Indexers.jsx:257](../pages/Indexers.jsx#L257)

5. **Refresh des catégories (caps)** (`refresh-caps-{sourceId}`)
   - Déclenchée lors du refresh des caps d'un indexer
   - Label: "Refresh caps: {nom de la source}"
   - Fichier: [Indexers.jsx:257](../pages/Indexers.jsx#L257)

### 🔄 Tâches prêtes (helpers créés, en attente d'intégration API)

6. **Sync RSS globale** (`rss-sync-all`)
   - Helper: [syncTasks.js](../tasks/syncTasks.js)
   - Fonctions: `startRssSyncAll()`, `updateRssSyncAllProgress()`, `completeRssSyncAll()`
   - Usage: Pour sync automatique ou manuelle de toutes les sources

7. **Queue de posters** (`poster-queue-active`)
   - Helper: [posterQueueTasks.js](../tasks/posterQueueTasks.js)
   - Fonctions: `startPosterQueueMonitoring()`, `updatePosterQueueProgress()`, `completePosterQueue()`
   - Note: Nécessite polling de l'API pour obtenir la taille de la queue

8. **Nettoyage cache posters** (`maintenance-clear-cache`)
   - Helper: [maintenanceTasks.js](../tasks/maintenanceTasks.js)
   - Fonctions: `startClearPosterCache()`, `completeClearPosterCache()`
   - Prêt pour Settings → Maintenance

9. **Purge des logs** (`maintenance-purge-logs`)
   - Helper: [maintenanceTasks.js](../tasks/maintenanceTasks.js)
   - Fonctions: `startPurgeLogs()`, `completePurgeLogs()`
   - Prêt pour Settings → Maintenance

10. **Sauvegarde** (`maintenance-backup`)
    - Helper: [maintenanceTasks.js](../tasks/maintenanceTasks.js)
    - Fonctions: `startBackup()`, `updateBackupProgress()`, `completeBackup()`
    - Prêt pour Settings → Maintenance

## 🚀 Prochaines étapes

- [ ] Implémenter l'API pour la queue de posters (GET /api/posters/queue/status)
- [ ] Implémenter les API de maintenance (clear cache, purge logs, backup)
- [ ] Activer les boutons de maintenance dans Settings.jsx
- [ ] Ajouter polling automatique pour la queue de posters
- [ ] Ajouter des icônes par type de tâche dans la sidebar
- [ ] Barre de progression visuelle
- [ ] Notification de fin de tâche
- [ ] Historique des tâches terminées
