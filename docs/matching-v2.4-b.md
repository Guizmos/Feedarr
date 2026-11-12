# V2.4-B — Publication et activation sûres des films

## Correctifs

Le chemin après Accept est désormais `PublishMovieV2Async`, séparé de la publication historique. Avant toute association : récupération des détails/crédits, contrôle des IDs TMDB de réponse, contrôle des images de la même identité, téléchargement et décodage de l’image. Un ID absent ou contradictoire provoque une abstention. Les champs provider absents restent absents.

Le poster est écrit atomiquement sous un nom propre à la publication (`tmdb-movie-ID-v2-UUID.jpg`) : aucune troncature/remplacement d’un fichier partagé. Une transaction SQLite ciblée appelle les corps des writers existants (`SavePosterCore`, `UpdateExternalDetailsCore`) et enregistre ensemble identité, fichier et métadonnées, sous les contrôles de révision existants. Une erreur SQLite annule l’ensemble, y compris la mise à jour de l’entity et des releases liées. Une modification manuelle concurrente gagne.

Après commit, le store et ses miniatures sont des optimisations dérivées : une panne conserve le fichier complet directement servi par le chemin legacy et les métadonnées cohérentes ; `storePending=true` indique le défaut. La promotion vers le store utilise exclusivement l’ID accepté et les octets téléchargés, sans reprendre silencieusement une ancienne image partagée. Aucun fallback décisionnel historique, cache d’identité ou autre œuvre n’est utilisé.

Le pilote actif exige une image TMDB disponible ; il ne tente pas Fanart en cas de panne/image absente. L’historique OFF conserve ses fallbacks existants. Cette restriction est volontairement conservatrice pour ce premier périmètre.

## Cohorte persistante

OFF reste la valeur par défaut. `App__MatchingV2MoviesMinReleaseId` reste obligatoire et positif lorsque ON.

À la première activation, avant le démarrage des workers et après les opérations de restauration de configuration existantes, la borne effective est `max(minimum configuré, MAX(releases.id)+1)`, calculée et mémorisée atomiquement dans la clé interne `app_settings.matching_v2_movies_cohort`. Pas de nouvelle table ni migration. Il s’agit d’un marqueur de configuration, sans titre ni credential.

Les releases déjà présentes sans identité restent hors de la cohorte, même si le minimum configuré est trop bas. Un redémarrage réutilise la même borne : une abstention V2 ne repasse pas dans l’historique lorsque V2 reste ON. Une modification du minimum configuré ou un marqueur invalide bloque le démarrage plutôt que de recréer silencieusement une cohorte. Ne pas éditer/supprimer ce marqueur pour élargir le pilote.

OFF n’initialise ni ne supprime le marqueur. Le retour OFF après redémarrage rétablit le chemin historique ; les associations déjà publiées ne sont ni effacées ni rematchées automatiquement. Le marqueur est conservé pour une réactivation de la même cohorte. Aucun traitement global n’est lancé.

## Limites résiduelles

- SQLite et le système de fichiers n’ont pas de transaction commune. Un arrêt brutal avant commit peut laisser un fichier non référencé ; il ne laisse pas de référence vers un fichier partiellement écrit. Aucun nettoyage automatique supplémentaire n’est ajouté. Une issue de commit incertaine privilégie la conservation du fichier.
- Le store et les miniatures peuvent rester indisponibles après une panne ; le poster propre à la publication reste utilisable. Leur réparation pourra utiliser les mécanismes de maintenance existants, sans réassociation. Aucun retry/refresh global au démarrage.
- Une indisponibilité avant publication (détails, crédits, images, téléchargement, fichier ou SQL) fait échouer le traitement sans nouvelle association. Les limites de couverture et budgets V2.4-A restent inchangées.
- Les données officielles TMDB restent une preuve provider, pas une validation humaine de l’identité ni des pixels de l’image. Aucun gain de précision n’est annoncé.

## Activation Beta minimale

1. Instance autorisée uniquement : garder OFF jusqu’au déploiement validé. Relever le prochain ID souhaité en lecture seule et renseigner `App__MatchingV2MoviesMinReleaseId`.
2. Passer `App__MatchingV2MoviesEnabled=true`, puis redémarrer. La borne est fixée avant ingestion ; les releases préexistantes sont exclues automatiquement.
3. Laisser arriver quelques nouveaux films par le flux normal. Vérifier un Accept complet, une abstention et une identité protégée avec les logs/diagnostics existants. Ne pas lancer de rematching ou de Replay pour provoquer des écritures actives.
4. Vérifier qu’un redémarrage ON conserve les associations et les abstentions de la cohorte. En cas de doute, remettre OFF et redémarrer : retour immédiat au routage historique pour les traitements suivants.

Aucune activation ni intervention NAS/production n’est effectuée par ce lot. Les tests utilisent SQLite, dossiers et transports simulés temporaires.
