# P4 — Réutilisation locale des identités média — 25 novembre 2026

Implémentation locale, sans modification de production, réimport, purge, relance Retro Fetch, commit ou push. Les modifications préexistantes du workspace sont conservées.

## Éléments repris

Le rapport initial `.tmp/audit-posters/rapport-sans-posters-2026-11-25.md`, notamment sa comparaison RSS/Torznab et ses diagnostics de matching/Retro Fetch, ainsi que les rapports locaux P0, P1 et P3 ont été réutilisés. Aucun nouvel audit exhaustif ni appel aux flux ou providers réels n'a été effectué.

Les neuf rapprochements prioritaires sont des **titres bruts complets identiques**, et non de simples titres clean :

| Programme | Donneur Torznab → cible RSS | Identité de l'audit |
| --- | --- | --- |
| Affaires sensibles S06E03 | 29827 → 31722 | TMDB tv:134594 |
| Affaires sensibles S06E02 | 29828 → 31721 | TMDB tv:134594 |
| Affaires sensibles S06E01 | 29829 → 31720 | TMDB tv:134594 |
| 66 minutes | 29831 → 31708 | TMDB tv:117970 |
| 66 minutes grand format | 29832 → 31707 | TMDB tv:117970 |
| C'est la famille | 29880 → 31793 | TMDB tv:207042 |
| Mauvaise Influence 720p | 30793 → 32014 | TMDB tv:334848 |
| Mauvaise Influence 1080p | 30794 → 32013 | TMDB tv:334848 |
| Esprit Japon Nolife | 30870 → 31856 | TMDB tv:127160 |

Ces neuf empreintes sont figées dans les tests P4 et rejouées avec des snapshots vérifiés synthétiques. Les tests démontrent l'éligibilité de ces empreintes et la compatibilité Serie/Emission, **pas** l'état actuel des neuf donneurs de production. Un ancien donneur dépourvu de provenance vérifiable sera refusé au prochain traitement.

## Comportement ajouté

Avant les branches de recherche habituelles, chaque release non protégée peut rechercher un donneur parmi les releases persistées. Deux index locaux portent sur le titre original complet et l'infohash. La sélection est bornée à 32 candidats ; une saturation entraîne un refus, sans décision prise sur une liste tronquée. Aucun filtre `sources.enabled` n'est appliqué aux donneurs et aucune synchronisation n'est réactivée.

Une réutilisation exige simultanément :

- Une empreinte complète de release identique, incluant des marqueurs techniques, ou un infohash hexadécimal identique de 40/64 caractères accompagné de preuves de portée concordantes. Aucun rapprochement sur le seul titre clean.
- Un type compatible, établi par le routage existant. Film/Animation/Spectacle ne deviennent pas automatiquement interchangeables ; Serie et Emission peuvent partager une identité TV prouvée. Un type vidéo non démontré reste refusé.
- Pour un film, une année reparsée identique, sans contradiction avec une année persistée. Pour la voie hash avec titres différents : titre reparsé, année, saison, épisode, date TV et édition concordants, avec année film ou structure TV explicite.
- Pour les autres catégories, dont jeux, anime, audio, livres et BD : titre original complet identique. Le hash ne permet pas d'ignorer les différences de DLC, bundle, édition, plateforme ou saison. Le provider doit correspondre à la catégorie de l'identité.
- Un snapshot provider version 1 **sur la release donneuse elle-même** et sur son entité, avec provider/ID concordants. Les identités TMDB doivent également être typées et cohérentes avec les colonnes TMDB de la release. IMDb et TVDB disponibles sont des contraintes de cohérence sur l'entité vérifiée, jamais des assertions librement recopiées ou des IDs traités comme TMDB.
- Aucun conflit entre les identités candidates corroborées, les assertions RSS du donneur/de la cible et les identités persistées. Une assertion externe impossible à corroborer localement entraîne un refus.
- Aucune correction manuelle du donneur, provenance d'entité manuelle, ancien poster manuel ou identité issue de P4. Les corrections de la cible et ses identités établies restent protégées.

Une association manuelle n'est pas utilisée comme donneur, même si ses métadonnées ont été téléchargées et même si son marqueur actif a ensuite été effacé : la provenance d'entité `manual` reste exclue. P4 ne déclenche pas de validation provider supplémentaire pour la rendre transmissible.

Les mots d'édition du titre brut sont conservés dans une signature de sécurité distincte du titre de recherche. Les tests ont notamment détecté que le parseur retire « Extended » ; cette différence est maintenant refusée même avec un hash identique. Aucun seuil de scoring n'a été modifié.

## Entités, posters et concurrence

Le rattachement de la seule release cible à l'entité donneuse se fait dans une transaction SQLite d'écriture, après les protections de snapshot existantes. L'ancienne entité cible et ses autres membres ne sont pas réécrits. La transaction reprend les références au poster existant et au poster store ; elle ne duplique ni le fichier image ni les métadonnées de l'entité.

La migration `0065_local_identity_reuse.sql` ajoute la provenance release/donneur/source/entité/provider/ID/preuve/date et les deux index. Elle n'infère aucune preuve historique. Une release réutilisée est explicitement exclue des donneurs ultérieurs ; de plus, elle n'acquiert pas artificiellement un snapshot provider propre. Cela évite une propagation en cascade dont l'origine aurait été perdue.

Si le poster partagé existe sur disque, aucun appel provider n'est nécessaire. Sinon le mécanisme de rafraîchissement de l'identité désormais établie conserve cette identité et les métadonnées disponibles. Une indisponibilité de l'image ne déclenche pas une nouvelle sélection d'œuvre. Les refus de réutilisation continuent vers le fallback existant, avec ses budgets, seuils et protections habituels. Les protections de publication partagée restent utilisées par les publications ultérieures.

## Radarr et Sonarr : possibilité et limite

Les champs persistés `radarr_item_id`/`sonarr_item_id` sont des IDs internes aux applications Arr, pas des IDs TMDB/TVDB. Les badges `in_radarr`/`in_sonarr` et la méthode de rapprochement ne prouvent pas une identité. Les caches par titre de `ArrLibraryCacheService` peuvent retourner un TMDB/TVDB mais leurs entrées ne conservent pas l'année ni une preuve indépendante de correspondance ; leurs variantes de titre ne permettent pas d'écarter les homonymes/remakes.

Pour **Oppenheimer (32718)** et **Overdose**, P4 n'exploite donc pas un badge ou un cache par titre comme raccourci de matching. Une identité Arr peut bénéficier de P4 lorsqu'elle est déjà matérialisée dans une identité média locale vérifiée et corroborée par une release donneuse ; aucune nouvelle lecture réseau Arr n'est ajoutée. Sans cette preuve, le traitement actuel reste applicable, notamment la priorité titre/année P1 pour les films. Une exploitation plus large nécessiterait de conserver des enregistrements Arr complets, typés, datés et non ambigus ; elle n'est pas simulée à partir des badges existants.

## Diagnostics

Chaque tentative consigne `local_identity_reuse` dans la couverture des diagnostics de matching existants (donc dans les CSV Retro Fetch), avec motif et provenance du donneur examiné/retenu lorsqu'il existe. Le journal structuré comprend release cible, décision, motif, release/source donneuses, provider et ID. Une réussite sélectionne la stratégie `local_identity_reuse`. La table de provenance persiste la décision de réutilisation.

Motifs principaux : `original_release_fingerprint`, `infohash_and_scope`, `target_protected`, `no_candidate`, `candidate_budget`, `scope_or_evidence_insufficient`, `donor_unverified_or_manual`, `poster_identity_unverified`, `contradictory_type`, `contradictory_identities`, `contradictory_claims`, `contradictory_donor_claims`. Un refus avec plusieurs candidats donne le dernier donneur examiné ou celui du conflit immédiat ; la liste complète n'est pas exportée.

## Validation et limites

Les tests sont entièrement locaux, avec SQLite temporaire et providers simulés. Ils couvrent les neuf doublons de l'audit, les sources désactivées, l'absence d'appels providers pour un poster existant, les assertions IMDb/TVDB, les contradictions, les homonymes/remakes, les éditions Extended, les épisodes différents, les plateformes/DLC jeux, les snapshots historiques non vérifiés, les corrections manuelles actives ou effacées, les descendants P4, les badges Arr insuffisants, la référence au poster store et la préservation des autres membres d'une entité cible.

La réutilisation est volontairement conservatrice : sans hash, les variantes d'encodage ou de ponctuation ne sont pas rapprochées ; les films sans année sont refusés ; les sources supprimées et leurs releases disparues ne peuvent pas servir de donneurs ; aucun ID n'est inventé pour compléter une preuve manquante. Les cas hérités non vérifiables restent au fallback plutôt que de provoquer des validations réseau répétées. Les liens vers les donneurs sont des références historiques et non des dépendances à leur activation future.

Les releases déjà persistées passent par ce contrôle au prochain Retro Fetch où elles sont sélectionnées, sans réimport ni modification du planificateur. Aucun Retro Fetch n'a été lancé pendant ce travail.

Résultat final : **128 tests ciblés réussis, 0 échec**, sur P4 et les suites existantes d'identités, corrections manuelles, assertions RSS, publications partagées, routage RSS vidéo et contrats jeux. La compilation est réussie et `git diff --check` ne signale aucune erreur. VSTest a nécessité l'autorisation d'ouvrir son socket de communication local ; aucun service applicatif ni provider réel n'a été démarré pour ces tests.
