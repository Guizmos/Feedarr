# Guide de Configuration Feedarr (Setup Wizard)

Français | [English](configuration-wizard.md)

Ce guide décrit les 6 étapes du wizard Feedarr et explique:
- quoi faire à chaque étape,
- ce que chaque étape modifie dans l’application,
- comment créer ou récupérer les clés API pour chaque provider.

## Prérequis

Avant de lancer `/setup`, prépare:
- au moins un provider metadata (TMDB, Fanart, IGDB, TVmaze optionnel),
- un fournisseur d’indexeurs (Jackett ou Prowlarr),
- optionnel: une instance Sonarr et/ou Radarr (URL + API key).

## Étape 1 - Intro

Capture:

![Étape 1 Intro](screenshots/wizard-step0.png)

À faire:
- Lire le résumé du wizard puis cliquer `Suivant`.

Impact:
- Aucune configuration persistée à ce stade.

## Étape 2 - Providers (Metadata)

Capture:

![Étape 2 Providers](screenshots/wizard-step1.png)

À faire:
1. Sélectionner un provider dans la liste.
2. Saisir les identifiants.
3. Cliquer `Tester`.
4. Si test valide, cliquer `Sauvegarder`.
5. Refaire pour les autres providers utiles.

Impact:
- Sauvegarde des credentials dans `Settings > External`.
- Active l’enrichissement metadata (posters, détails, personnes, etc.).
- Au moins un provider valide est nécessaire pour continuer.

### Création des clés API - Providers Metadata

#### TMDB

1. Créer/se connecter à un compte TMDB.
2. Ouvrir les paramètres API:
   - `https://www.themoviedb.org/settings/api`
3. Demander l’accès API puis récupérer la clé API v3.
4. Coller dans Feedarr `TMDB -> Clé API`.

Doc:
- `https://developer.themoviedb.org/docs/getting-started`

#### Fanart

1. Créer/se connecter à un compte Fanart.
2. Générer une clé API sur:
   - `https://fanart.tv/get-an-api-key/`
3. Coller la clé dans Feedarr `Fanart -> Clé API`.

#### IGDB (via Twitch Developer Console)

1. Créer/se connecter à un compte Twitch.
2. Ouvrir la console dev:
   - `https://dev.twitch.tv/console/apps`
3. Créer une application.
4. Récupérer `Client ID` et générer/récupérer `Client Secret`.
5. Coller dans Feedarr `IGDB -> Client ID / Client Secret`.

Docs:
- `https://api-docs.igdb.com/#getting-started`

#### TVmaze

La clé TVmaze est optionnelle dans Feedarr.

1. Référence API:
   - `https://www.tvmaze.com/api`
2. Si tu as un plan nécessitant une clé, utilise la clé du compte.
3. Sinon, TVmaze peut rester activé sans clé selon les endpoints utilisés.

## Étape 3 - Fournisseurs (Jackett / Prowlarr)

Capture:

![Étape 3 Fournisseurs](screenshots/wizard-step2.png)

À faire:
1. Choisir `Jackett` ou `Prowlarr`.
2. Saisir:
   - Base URL (ex: `http://localhost:9117` ou `http://localhost:9696`)
   - API key
3. Cliquer `Tester`.
4. Si test valide, cliquer `Sauvegarder`.

Impact:
- Persistance côté API Feedarr (`/api/setup/indexer-providers/{type}`).
- Le provider sert à lister les indexeurs dans l’étape suivante.

### Récupération API key - Jackett / Prowlarr

#### Jackett

1. Ouvrir l’interface web Jackett.
2. Copier l’API key affichée sur le dashboard/home.
3. Coller dans Feedarr (étape 3).

#### Prowlarr

1. Ouvrir l’interface web Prowlarr.
2. Aller dans `Settings -> General -> Security`.
3. Copier l’API key.
4. Coller dans Feedarr (étape 3).

## Étape 4 - Indexeurs

Capture:

![Étape 4 Indexeurs](screenshots/wizard-step3.png)

Sélecteur de catégories:

![Étape 4 Sélecteur Catégories](screenshots/wizard-step3_cat.png)

À faire:
1. Sélectionner un indexeur disponible (par fournisseur).
2. Laisser Feedarr charger les catégories (CAPS).
3. Garder les catégories recommandées, ou basculer sur liste complète.
4. Ajouter l’indexeur.
5. Répéter pour d’autres indexeurs.

Impact:
- Création des sources Feedarr (`/api/sources`).
- Les catégories pilotent filtrage, parsing et sync.
- Au moins un indexeur ajouté est nécessaire pour continuer.

## Étape 5 - Applications (Sonarr / Radarr) - Optionnel

Capture:

![Étape 5 Applications](screenshots/wizard-step4.png)

À faire:
1. Choisir le type (`Sonarr` ou `Radarr`).
2. Saisir base URL + API key.
3. Cliquer `Tester`.
4. Si valide, cliquer `Sauvegarder`.
5. Optionnel: configurer les options avancées (root folder, quality profile, tags, etc.).

Impact:
- Sauvegarde de l’intégration ARR dans Feedarr (`/api/apps`).
- Active les workflows de statut et de synchronisation avec Sonarr/Radarr.
- Cette étape peut être ignorée (`Passer`).

### Récupération API key - Sonarr / Radarr

#### Sonarr

1. Ouvrir l’interface web Sonarr.
2. Aller dans `Settings -> General -> Security`.
3. Copier l’API key.

#### Radarr

1. Ouvrir l’interface web Radarr.
2. Aller dans `Settings -> General -> Security`.
3. Copier l’API key.

## Étape 6 - Résumé

Capture:

![Étape 6 Résumé](screenshots/wizard-step5.png)

À faire:
1. Vérifier l’état providers/fournisseurs/sources/applications.
2. Cliquer `Lancer Feedarr`.

Impact:
- Marque l’onboarding comme terminé (`/api/system/onboarding/complete`).
- Redirige vers la bibliothèque.

## Checklist post-setup rapide

1. Lancer une première sync et vérifier l’arrivée des releases.
2. Vérifier l’enrichissement posters/métadonnées sur plusieurs items.
3. Contrôler les pages `System` et `Activity` pour détecter les erreurs.
4. Ajuster la sécurité si exposition hors LAN.
5. Créer une première sauvegarde via les outils système.
