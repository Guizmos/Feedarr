# P6B — Simplification TV, coûts et budgets ciblés

Implémentation locale du 25 novembre 2026, fondée sur le rapport P6, P5 et les diagnostics Retro Fetch existants. Aucun nouvel audit ni appel réel aux providers. P6C reste hors périmètre.

## Décisions appliquées

La collecte reste complète : toutes les pages annoncées sont contrôlées, y compris la dernière. Une pagination incohérente ou incomplète ne fournit aucun ensemble partiel au décideur. Une extension reprend le plan en cours sans refaire ses premières pages.

| Ressource | Base | Extension conditionnelle |
|---|---:|---:|
| Projection TV | 100 candidats | 150 |
| Enrichissement TV | 6 concurrents | 12 |
| Requêtes logiques TV, recherche comprise | 30 | 40 |
| Collecte TMDB | 12 pages | 18 |

Les extensions TV demandent un contexte série, une preuve structurelle saison/épisode/date et un titre officiel exact déjà observé. Les dossiers verrouillés, corrigés manuellement ou dotés d'une identité établie ne reçoivent pas cette extension. L'extension de collecte demande un plan typé prometteur : preuve TV structurelle et titre officiel exact, ou film avec année fiable et résultat officiel exact de cette année. La projection Films reste à 100. Les plafonds ne sont pas des objectifs de consommation.

Une seule classification lexicale préalable porte sur le pool complet. Tous les concurrents pertinents restent à enrichir ; ils ne sont jamais limités aux douze premiers. Au-delà du plafond applicable, abstention avant enrichissement. Les résultats lexicalement éloignés ne deviennent plus tous des concurrents obligatoires au seul motif qu'un alias inconnu pourrait les sauver. Les contradictions de première diffusion restent examinables par les preuves TV ; les contradictions réelles d'identité restent bloquantes.

Les alias supplémentaires concernent les concurrents retenus. En l'absence de concurrent pertinent, le fallback examine au plus deux candidats faibles, classés par preuve puis identité stable. Il ne s'agit pas d'une nouvelle passe globale après échec des concurrents pertinents. Le risque accepté est de manquer un alias officiel inconnu d'un candidat éloigné, notamment au-delà de ces deux candidats ; les tests matérialisent cette limite.

## Réponses réutilisées et diagnostics

Les réponses complètes sont mutualisées par provider et chemin pendant une tentative. Le cache provider existant conserve ses limites de taille, sa durée et sa mutualisation des requêtes simultanées. Les décisions ne sont pas mises en cache.

Un objet TVmaze complet déjà contenu dans la recherche peut fournir les détails de la même identité à l'enrichissement et à la publication. Un objet partiel impose le chargement normal ; une contradiction positive d'année avec les détails déjà présents reste un refus.

Les détails TMDB TV enrichis peuvent alimenter la publication si l'identité, la langue configurée et tous les champs de publication nécessaires concordent. Les crédits restent récupérés séparément. Un snapshot EN ne remplace pas les détails FR. Une réponse incomplète déclenche le chemin normal ; un rafraîchissement explicite contourne cette réutilisation. La sélection des posters FR et son fallback existant restent inchangés.

Les diagnostics exposent les limites réellement appliquées, concurrents pertinents, fallback d'alias, candidats faibles évités, détails validés, candidats enrichis, réponses réutilisées, requêtes logiques et transports HTTP réels. La limite logique est contrôlée avant l'appel : le 41e appel n'est pas transporté. Les diagnostics conservent aussi la progression partielle en cas d'arrêt pendant l'enrichissement.

## Mesures ciblées reproductibles

Tests sans réseau, avec les vrais collecteurs, clients et enrichissement, et un transport HTTP simulé. Les volumes de recherche viennent des diagnostics existants ; les contenus des réponses, leur distribution lexicale et les preuves sont synthétiques. Ces fixtures isolent les causes démontrées, **ne rejouent pas les réponses historiques complètes et ne prouvent aucune récupération réelle de poster**.

La référence avant reproduit les anciens verrous projection/enrichissement et, pour le petit pool, la séquence d'appels antérieure. Elle ne constitue pas une seconde copie intégrale de l'ancien moteur. L = requêtes logiques, T = transports HTTP simulés, E = candidats enrichis. Les chiffres portent sur le matching ; la publication est mesurée séparément.

| Fixture | Avant L/T/E | Après à froid L/T/E | Issue après, sur fixture |
|---|---:|---:|---|
| Survivor, 105 candidats | 12/12/0 | 13/13/1 | Acceptation |
| Fort Boyard, 24 | 4/4/0 | 5/5/1 | Acceptation |
| Traces, 37 | 4/4/0 | 6/6/1 | Acceptation |
| Sacrifice, 26 | 4/4/0 | 6/6/1 | Acceptation |
| Lie To Me, 9 | 2/2/0 | 3/3/1 | Acceptation |
| Bel bien, 11 | 4/4/0 | 5/5/1 | Ambiguïté maintenue |
| Sugar Rush, 10 | 2/2/0 | 3/3/1 | Acceptation |
| L'Amour est dans le pré, 8 | 2/2/0 | 4/4/1 | Acceptation |
| The One, plan de 18 pages | 2/2/0 | 18/18/0 | Refus projection Films >100 |
| 'Oro, plan prometteur de 18 pages | 4/4/0 | 20/20/1 | Acceptation |
| Enquête, plan prometteur de 18 pages | 4/4/0 | 20/20/1 | Acceptation |
| Petit pool de six candidats | 14/14/6 | 4/4/1 | Traîne faible évitée |

Pour les huit premières fixtures, une répétition à cache chaud conserve les requêtes logiques et tombe à **zéro transport**, sans réutiliser une décision. Une abstention ancienne coûtait parfois moins cher : les appels supplémentaires servent désormais à évaluer le candidat corroboré plutôt qu'à s'arrêter avant son examen.

TVmaze complet : 3/3/1 devient 2/2/1, puis publication sans transport supplémentaire. TVmaze partiel : 3/3/1 reste nécessaire. Un rafraîchissement explicite effectue un transport. TMDB complet et FR : la publication évite les détails et effectue seulement les crédits ; incomplet ou mauvaise langue : détails localisés et crédits, soit deux transports.

Douze concurrents forts sont tous enrichis puis restent ambigus ; treize entraînent une abstention avant tout détail. Une fixture atteint exactement 40 requêtes et enrichit ses douze candidats ; une autre nécessitant davantage s'arrête à 40, après dix candidats enrichis, sans 41e transport. Un concurrent fort en dernière page reste visible et empêche une acceptation abusive.

## Validation et limites

- 26 tests P6B réussis : mesures, cache froid/chaud, langues et publication, rafraîchissement, pagination, ambiguïtés, fallback borné et bornes de budget.
- 249 tests ciblés de non-régression réussis : P6A exact/réutilisation, P4, TV/P5, diagnostics d'enrichissement, collecte/pagination, priorité année, cache, multilingue et contrat vidéo.
- Les tests utilisent une base temporaire et des transports simulés. Aucun appel RAWG, changement de production, purge, Retro Fetch massif, commit ou push.

P6A, corrections manuelles, identités établies, contrôle des identités externes et fusion interproviders restent préservés. Les limites de couverture restantes comprennent les alias inconnus éloignés, les pools trop ambigus, les catalogues incomplets, les contradictions positives et la projection Films inchangée. Aucun nouveau taux de couverture réel n'est annoncé ; il faudra les diagnostics du prochain traitement normalement déclenché pour le mesurer.
