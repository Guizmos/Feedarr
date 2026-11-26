# P6A — Décision exacte et séparation des portées

Implémentation du premier ensemble validé du [rapport P6](matching-p6-protections-propositions.fr.md), le 25 novembre 2026. Aucun nouvel audit ni appel réel aux providers. Aucun changement de production, purge, réimport, relance massive, commit ou push. Les modifications antérieures du workspace sont conservées.

## Décision exacte

La priorité existante `MovieExactYearPriority` prend désormais en charge les candidats officiels exacts dont les seuls veto concernent la marge, le titre numérique ou la ponctuation numérique. Le ranker général et ses seuils restent inchangés : l'assouplissement est appliqué dans le parcours movie exact, après une collecte complète, pour une année parsée non hypothétique, un type confirmé et une identité exacte unique.

Les homonymes exacts de même année ne sont pas départagés arbitrairement. Cela inclut un cas limite nouveau : si un ancien veto de ponctuation masquait un deuxième candidat exact et laissait le premier en `Accept`, l'assouplissement rétablit l'ambiguïté au lieu de confirmer ce choix.

Le parcours exige toujours la preuve de l'année originale du candidat retenu. Les vérifications historiques se concentrent sur ce candidat et les concurrents pertinents, réutilisent la réponse combinée/cache existante et restent dans le budget actuel. L'historique manquant, physique seul ou indisponible faute de budget d'un voisin plus faible devient un avertissement, sans obligation de prouver son absence de toute l'année demandée. Le candidat retenu, lui, doit rester corroboré. Une réponse indiquant positivement un autre ID, type, titre original ou une année principale contradictoire n'est pas assimilée à une simple absence d'information.

Pour un rejet numérique contextuellement résoluble, le collecteur conserve le résultat ciblé complet afin de permettre cette décision, sans relancer inutilement la recherche générale. Les autres fallbacks ne changent pas.

## Édition et année originale

La détection d'édition est appliquée après retrait du titre officiel reconnu du titre brut. Des mots comme « Restoration » appartenant au nom officiel ne sont donc pas traités comme des métadonnées d'édition de la release.

Les éditions vidéo et notes de remaster/ressortie sur la même identité deviennent des avertissements. L'historique distingue maintenant l'année originale de la simple présence d'une sortie locale, physique ou télévisée dans une année. Une telle sortie secondaire ne suffit plus à maintenir artificiellement un voisin en compétition.

Deux protections restent bloquantes et ont des diagnostics distincts :

- `neighbor_original_year_competes` : la première originale du voisin appartient à l'année demandée ; le diagnostic donne son ID, l'année principale, la première originale et les preuves de dates contrôlées ;
- `external_identity_collision` : deux IDs TMDB partagent explicitement le même IMDb, dans les preuves reçues ; aucun regroupement arbitraire des entités n'est effectué.

Les codes d'avertissement sont conservés dans les raisons de la décision et dans le journal de priorité. Les notes libres des providers ne sont pas ajoutées aux logs.

## Résultats des cas de référence

| Cas | Comportement vérifié |
| --- | --- |
| Oppenheimer | Le candidat 872585 / 2023 peut gagner sur le voisin 2024 malgré une édition vidéo ou note de remaster non identitaire. |
| 418 | Le titre entièrement numérique peut être accepté avec titre officiel, année, type, unicité et preuve originale concordants ; aucun ID externe supplémentaire n'est imposé par le seul fait que le titre est numérique. |
| 9/11 | Une différence contextuelle de ponctuation numérique ne bloque plus l'identité exacte unique ; la collecte ciblée reste complète, sans fallback général redondant. |
| Overdose / Gentle Monster | Deux identités exactes de même année restent ambiguës, sans appel de preuve optionnelle destiné à choisir arbitrairement l'une d'elles. |
| Bruised / Whistle | Une première originale du voisin dans l'année demandée maintient l'ambiguïté, même si son année principale est différente. |
| Piece by Piece | Une sortie secondaire en 2024 du voisin créé en 2023 devient un avertissement ; un IMDb explicitement commun produit au contraire `external_identity_collision`. |

Les titres et IDs viennent du rapport P6 et des fixtures antérieures. Les histoires de sorties utilisées pour isoler les branches sont synthétiques : aucun catalogue réel n'a été rappelé. Pour Piece by Piece, P6 ne disposait pas de la branche précise derrière l'ancien motif agrégé. P6A permet désormais de l'identifier au prochain traitement, sans prétendre l'avoir mesurée en production.

## Réutilisation P4 et validation historique

La validation ne demande plus systématiquement deux snapshots identiques sur la release et l'entité. Une racine d'entité vérifiée suffit si des métadonnées concordantes apportent une preuve indépendante, en plus des preuves fortes de release déjà exigées par P4. Une copie de snapshot sur la release n'est pas inventée pour satisfaire ce test.

Un alias secondaire absent devient **inconnu**. Deux IDs secondaires positifs différents restent contradictoires. Les claims intrinsèquement contradictoires, les types incompatibles et les IDs principaux incompatibles restent refusés. Ces conflits connus sont examinés avant une éventuelle validation réseau historique.

Pour une racine historique TMDB déjà identifiée et typée, sans snapshot exploitable :

1. Le dépôt retourne un besoin de validation, sans rattachement ni mutation pendant l'appel réseau.
2. Le service consulte les détails localisés par le cache de réponses existant, borné à 15 secondes et mutualisé par identité/langue/credential. Il exige un titre principal concordant et, pour un film, une date valide de la même année. Une proposition de sujet ou de suffixe ne devient pas à elle seule une preuve principale.
3. Le dépôt relit la racine et vérifie le snapshot, les claims et les protections sous transaction. Une correction intervenue entre-temps invalide la réutilisation.
4. Le rattachement conserve le même ID. Le snapshot réellement reçu remplace les anciennes métadonnées de cette identité ; un ancien snapshot n'est jamais simplement marqué « vérifié ».

Les donneurs sans ID connu ne déclenchent pas de validation exploratoire. Les corrections manuelles, la provenance manuelle, les titres manuels et les posters manuels hérités de l'entité sont exclus des racines automatiques. Les descendants P4 restent exclus des donneurs ; la provenance conserve la racine et ne permet pas de cascade. Les sources désactivées restent utilisables sans réactivation de synchronisation.

La validation historique ajoutée couvre TMDB movie/TV. Pour les autres providers, il faut une racine locale vérifiée : aucun nouveau validateur réseau générique n'a été ajouté.

## Identité média et poster

La provenance d'image n'est plus un veto sur l'identité P4. Un poster Fanart correctement rattaché au même TMDB est reconnu comme une image de cette œuvre. Un poster de provenance inconnue ou dont l'ID ne concorde pas est remplacé par le mécanisme normal de cette identité, sans nouvelle sélection d'œuvre.

Les champs de provider d'image restent distincts de ceux des métadonnées. Les chemins hérités ne sont pas utilisés comme fichiers locaux si leur nom n'est pas canonique ; un hash existant est contrôlé avant réutilisation rapide ou fallback P4. Les contrôles de publication partagée — namespace, hash, fichier original, miniatures, références GC et transaction — restent actifs.

## Priorité FR et partage

La sélection TMDB fournit maintenant à la fois le chemin choisi et sa langue réelle. Elle continue à respecter l'ordre configuré : FR d'abord avec la préférence FR. Cette sélection passe par le cache de réponses existant ; un refresh explicite la contourne.

Le partage rapide ne reprend un poster TMDB que s'il correspond à la langue préférée. Une image EN/neutre/inconnue peut ensuite être partagée si la sélection localisée, déjà mutualisée, confirme ce même chemin comme meilleur fallback. Un poster EN hérité ne peut donc pas court-circuiter un FR disponible. La langue effectivement choisie est enregistrée, sans déclarer artificiellement FR une image Fanart ou inconnue.

Pour P4, une image EN fiable reste utilisable si la recherche d'une meilleure image échoue ; l'identité n'est pas rematchée. Une image dont l'intégrité ou le rattachement n'est pas établi n'est pas rendue fiable par ce fallback. Les affiches manuelles et les identités déjà établies conservent leur parcours protégé : aucune campagne de remplacement d'images n'est lancée.

## Validation et portée

Les passages locaux pertinents sont verts :

- **450 tests** dans la sélection élargie Films/TV, P1/P4, identités, publication partagée, corrections et caches ;
- **147 tests** lors du dernier passage de décision exacte et non-régression movie, incluant les avertissements historiques et les contradictions positives ;
- **76 tests** d'intégrité, métadonnées, cache, matérialisation et GC après les ajustements correspondants ;
- **46 tests** dans le dernier passage ciblé sur les racines P4, incluant le poster manuel porté seulement par l'entité.

Compilation réussie, sans avertissement dans les derniers passages, et `git diff --check` sans erreur.

Ces sélections se recouvrent et leurs nombres ne s'additionnent pas. Les attentes P1 d'abstention pour éditions vidéo/ressorties et titres numériques ont été remplacées par les comportements P6A validés. Une ancienne attente de matérialisation pré-P1 a aussi été corrigée : le plan fiable couvre déjà trois années FR/EN, soit six recherches et non deux ; P6A n'augmente pas ce budget.

Les tests vérifient notamment le refus d'une racine modifiée manuellement après validation, la non-promotion d'une mauvaise identité historique, la réutilisation de la validation sur un second doublon, le maintien du provider Fanart, le choix FR malgré un ancien EN, et le partage EN sans nouveau téléchargement lorsque la sélection ne trouve pas de FR.

Aucune augmentation de seuil/budget TV, aucun changement de langage des titres ou de moteur complet : P6B et P6C restent séparés. Aucun taux de couverture réel, gain de poster ou récupération sur les 167 releases n'est annoncé sans prochain traitement autorisé et mesure sur son périmètre.

Fichiers principaux : `MovieExactYearPriority.cs`, `MovieYearSearchCollector.cs`, `ReleaseRepository.LocalIdentityReuse.cs`, `LocalIdentityReuse.cs`, `PosterFetchService.RssIdentity.cs`, les parcours de publication/rafraîchissement/partage des posters et `TmdbClient.cs`. Tests dédiés : `MatchingP6AExactTests.cs` et `MatchingP6AReuseTests.cs`. Pas de migration supplémentaire.
