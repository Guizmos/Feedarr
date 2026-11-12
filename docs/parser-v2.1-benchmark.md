# Parser V2.1 — mesure reproductible, état de livraison

Base : `beta` après `074986e`. Ce lot ajoute uniquement outils, tests et documentation.
Aucune règle du parser, du matching ou des recherches de posters n’est modifiée.
Le corpus permanent de 174 cas reste inchangé.

## Données réelles : mesures manquantes

Aucun accès SSH Synology identifiable dans la configuration disponible ; aucune
connexion de production n’a été ouverte. **Aucun corpus réel n’a été extrait.**
Cela ne signifie pas que Feedarr possède zéro release : sa population n’a pas été
mesurée. Aucun export brut, nom de tracker privé ou credential n’est versionné.

Restent donc non mesurés : taille/composition réelle, répartition réelle avec/sans
poster, sources, ancienneté, familles d’échecs réellement fréquentes, performances
NAS, références métier réelles et précision des associations providers.
Nombre de références réelles validées : **0** ; précision réelle : **non mesurée**,
et non 0 % ou 100 %. Les fixtures ne sont pas utilisées pour inventer cette mesure.

## Outils livrés

Voir [le mode d’emploi](../tools/parser-benchmark/README.md).

- Export SQLite ciblé autonome, stdout uniquement : OS en lecture seule, mode=ro,
  query_only, authorizer SQL. Refus d’un répertoire/base/sidecar accessible en écriture.
  Aucun checkpoint, accès Docker, appel de synchronisation ou parcours des posters.
- Échantillon demandé de 1500, plafond de 2000, cible 70 % sans poster, graine fixe,
  stratification par catégorie/source pseudonymisée/ancienneté/complexité ; volume
  adapté aux candidats disponibles après exclusions. Aucun nom de source exporté.
- Filtrage des valeurs sensibles, schémas sur liste blanche, compteurs d’exclusion,
  sorties locales privées ignorées par Git. Une revue humaine reste indispensable
  avant partage : les titres publics ne sont pas automatiquement anonymes.
- Runner Release .NET compilant les sources réelles du parser/catégories sans charger
  l’hôte API. Aucune dépendance NuGet externe ou requête réseau. Un autre checkout
  peut être mesuré avec `--source-root`, sans changer la branche ni ses fichiers.
- JSON/Markdown déterministes liés aux SHA-256 du corpus, des références et du code.
  Les durées non déterministes sont dans un fichier séparé, avec répétitions/warmups.
- Annotations unreviewed/ambiguous/validated, preuves/reviewer obligatoires pour une
  validation. Les métriques ne portent que sur les champs réellement annotés ; les
  identités providers sont notées séparément des sorties du parser.
- Comparaison de versions, recalcul des écarts, détection des régressions validées,
  refus de comparer des corpus/références différents. `check` refuse une précision
  sans référence validée. Une différence non annotée reste une observation.

## Benchmark réellement exécuté : fixtures existantes

| Catégorie | Entrées | Présence de poster |
| --- | ---: | --- |
| Film | 56 | Inconnue |
| JeuWindows | 56 | Inconnue |
| Serie | 34 | Inconnue |
| Anime | 8 | Inconnue |
| Emission | 8 | Inconnue |
| Animation | 6 | Inconnue |
| Spectacle | 5 | Inconnue |
| Book | 1 | Inconnue |
| Total | 174 | Inconnue pour les 174 |

Les 174 attentes revues au P0 vérifient dix champs ParsedTitle : 1740 comparaisons
de champs conformes. Les catégories sont des entrées du parser, pas une prédiction.
Le résultat **174/174 conforme** mesure la non-régression sur ces attentes approuvées,
pas la précision sur un échantillon indépendant. Aucune identité provider n’est
validée par ces fixtures. Les 109 sorties sans année sont un signal descriptif
compatible avec les attentes, pas 109 erreurs de parsing.

Corpus SHA-256 :
`cdc16e8ff41d5f7b6176a079506369eebc9cd19e1275c607f747351a7c363291`.
Sources parser/catégories actuelles SHA-256 :
`cbe4fa55015cfa39e48befdd68574f17cce043001be34c30b554502914a0a79c`.

Deux exécutions indépendantes donnent des résultats JSON et rapports Markdown
identiques octet pour octet ; comparaison sans changement ni régression.
Environnement mesuré : .NET 8.0.33, Arm64, mono-thread, 2 passages de warmup puis
5 répétitions par entrée, soit 870 appels chronométrés. Mesures station locale :

| Mesure | Exécution A | Exécution B |
| --- | ---: | ---: |
| Médiane par appel | 45,8 µs | 44,5 µs |
| p95 par appel | 158,9 µs | 160,7 µs |
| Somme des durées de parsing | 56,43 ms | 56,77 ms |

Compilation, warmup, SQLite et providers sont exclus de ces durées. Ces mesures
de cache chaud dépendent du JIT/GC et de la charge ; elles ne sont pas des mesures
Synology et n’ont pas de signification de latence de recherche complète.

Les sources de `2c77b26` ont aussi été lues avec Git dans un dossier temporaire,
sans checkout ni réécriture d’historique. Sur les mêmes attentes P0 : 122/174 conformes
pour le lot 5, contre 174/174 actuellement ; 52 entrées différentes, 84 champs devenus
conformes, aucune nouvelle divergence validée. Ces différences incluent les choix
d’ambiguïté approuvés au P0 ; elles ne représentent pas 52 mauvais matchs prouvés.
Le lot 5 mesure environ 10,2 µs de médiane et 44,6 µs de p95 dans cette exécution.
Le coût actuel plus élevé est donc une piste de profilage, pas une modification à
effectuer sans corpus réel et nouveaux contrôles de non-régression.

## Parsing, associations et absence de posters

L’export conserve séparément parsing stocké, parsing recalculé localement et IDs
associés. Il reprend la définition SQL existante des références de poster avec
fallback vers l’entity, sans vérifier ni télécharger les fichiers. Les erreurs sont
classées sans leurs messages/URLs. Les écarts de champs stockés, absence d’année,
IDs sans poster et types TMDB historiques manquants sont des signaux à examiner,
croisés par catégorie, poster, tentative et présence d’identité.

Sans données réelles et références revues, aucune fréquence d’anomalie de production
ni responsabilité parsing/cache/provider n’est affirmée. Une release avec poster
n’est jamais promue automatiquement en vérité terrain.

## Priorités proposées pour V2.2

1. Obtenir l’export filtré via un accès déjà en lecture seule et figer le corpus local.
2. Faire revoir un sous-ensemble représentatif avec et sans poster, en annotant
   séparément parsing et identité provider, et en conservant les cas indécidables.
3. Prioriser les familles réellement fréquentes à partir des écarts validés ; ne pas
   déduire une panne du parser de la seule absence de poster ou d’année.
4. Profiler le coût des validations de suffixes sur les titres réellement rencontrés,
   puis comparer toute optimisation aux références et aux 174 cas existants.

## Validation

- 20 tests ciblés des outils : déterminisme, quotas/strates, exclusions sensibles,
  listes blanches, intégrité des références/résultats et codes de retour.
- SQLite temporaire : écritures/ATTACH/PRAGMA dangereux refusés ; répertoire/base
  accessibles en écriture refusés ; octets SQLite/WAL/SHM inchangés lors d’une lecture
  dans un processus neuf, y compris une entrée engagée non checkpointée.
- Deux benchmarks actuels, contrôle des 174 références et comparaison de versions.
- Build Release du runner : zéro avertissement, zéro erreur.
- `git diff --check` ; aucun changement dans `src/`, aucune suite backend complète
  ni frontend lancée, puisque le parser et le code métier restent inchangés.
- Exports/résultats locaux et bin/obj ignorés ; fichiers étrangers préservés.
