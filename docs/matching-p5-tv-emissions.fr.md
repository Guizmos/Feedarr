# P5 — Séries TV et Émissions — 25 novembre 2026

Implémentation locale, sans modification de production, purge, réimport, synchronisation, relance Retro Fetch, commit ou push. Les modifications préexistantes, dont P0 à P4, sont conservées. La référence communiquée est **167 releases sans poster sur sources actives** ; elle n'a pas été remesurée. Aucun gain réel sur ces 167 releases n'est annoncé.

## Sources et portée de l'investigation

Reprise du rapport initial `.tmp/audit-posters/rapport-sans-posters-2026-11-25.md`, de ses diagnostics Retro Fetch, des rapports locaux P0 (quotas/diagnostics), P1 (films titre/année), P3 (IGDB) et du [rapport P4](matching-p4-local-identity-reuse.fr.md). Pour P2, les corrections et contrats disponibles sont `TitleParser.SearchMetadata.cs`, `TitleParserP2Tests.cs` et le corpus des posters manquants ; aucun rapport P2 séparé n'a été retrouvé parmi ces artefacts. L'audit annuel antérieur est également repris dans `matching-annual-season-audit.md`.

L'investigation s'est limitée aux cas demandés et aux mécanismes concernés. Aucun nouvel audit exhaustif, lecture d'une base de production ou appel réel aux API de matching n'a été réalisé. Les tests utilisent des bases temporaires et des réponses simulées. Une consultation de la documentation officielle TVmaze a confirmé les endpoints d'alias et d'épisodes par date ; aucun endpoint d'exemple n'a été interrogé.

Les diagnostics historiques de Traces et Lie To Me confirment des ambiguïtés après collecte complète, mais ne conservent pas les rangs des concurrents. Leur cause précise ne peut donc pas être déclarée résolue sur la seule base de ces anciens logs. Les réponses synthétiques des tests ne remplacent pas des captures de ces programmes.

## Causes confirmées et corrections

### Interprétation commune des preuves TV

`TelevisionSearchInput` construit les propositions et le contexte de preuve depuis le titre brut persisté, sans réécrire le titre affiché, l'année, la saison ou la catégorie enregistrés. Le parcours textuel Serie/Emission utilise la même interprétation pour RSS direct et Torznab. Les contenus Film/Animation routés vers TV rejoignent ce parcours.

- Une date complète valide devient une contrainte d'épisode. Son année n'est jamais comparée à l'année originale d'une série. Auparavant cette exploitation dépendait de la catégorie Emission ; une release Serie identique pouvait perdre sa date.
- Une année lexicale de saison/diffusion reste une hypothèse, sans bonus d'année originale ni contradiction forte contre une série plus ancienne. Les années originales explicitement parenthésées restent des années parsées lorsque le contexte n'est pas celui d'une diffusion HDTV/TVRip. Elles restent lexicales, jamais confirmées par le seul parseur.
- Les marqueurs annuels S19xxE/S20xxE étaient déjà reconnus correctement avant P5. P5 n'attribue pas à nouveau cette correction au présent lot. Lorsque leur année correspond à une date complète du titre, la preuve recherchée devient l'épisode à cette date : aucune conversion de S2026 en saison ordinale 20, 26 ou 36 n'est inventée et le numéro local n'est pas prétendu équivalent à celui d'un catalogue.
- Sans date concordante, les coordonnées annuelles restent des contraintes explicites ; elles ne sont pas silencieusement remplacées par une saison ordinaire. Une année seule ne permet pas cette conversion.

La recherche TV reste générale sur les années originales : aucun `primary_release_year` ni `first_air_date_year` n'est ajouté au plan TV. La stratégie P1 des films reste indépendante.

### Programme, sujet et suffixes

Une séparation explicite « programme - sujet », accompagnée de structure TV, de date ou de source HDTV/TVRip, peut produire deux propositions bornées : le titre complet et le programme seul. Les sujets déjà séparés par P2 sont réintégrés dans la première proposition pour conserver l'autre interprétation possible. Le programme seul ne peut accepter sans preuve indépendante de saison/épisode ou de date. Une diffusion ponctuelle sans cette preuve reste ambiguë ; un tiret ne devient pas une preuve d'appartenance à une série.

Pour les intégrales, une seconde proposition peut isoler un suffixe final d'un mot. L'acceptation exige un titre officiel exact et la présence de ce suffixe parmi les chaînes officielles du candidat, obtenues depuis ses détails vérifiés (`networks`, `network` ou `webChannel`). Cette preuve utilise la classe de support indépendante existante, sans changer les seuils. Aucun nom de programme ou de chaîne n'est codé en dur. Une édition ou un mot de titre n'est pas supprimé parce qu'il ressemble à un suffixe. Un suffixe non corroboré reste insuffisant et les concurrents restent présents.

Les mentions distinctives comme « grand format » ne sont pas supprimées pour forcer une correspondance avec le programme principal. Les saisons/épisodes et les titres d'édition conservés par le parseur continuent à contraindre les preuves.

### Titres officiels et articulation TMDB/TVmaze

Les détails TMDB vérifiés apportent désormais aussi `name` et `original_name`, en complément des titres localisés de recherche et d'`alternative_titles`. Un rejet lexical provisoire ne suffit plus à empêcher la découverte d'un titre officiel susceptible de le corriger ; tous les concurrents pouvant gagner cette preuve doivent tenir dans le budget d'enrichissement.

Pour TVmaze, les alias sont demandés uniquement si les titres déjà disponibles ne correspondent à aucune proposition. L'endpoint est rattaché à un show dont l'ID a été vérifié. Une réponse d'alias absente ou invalide donne `tvmaze_alias_coverage_unavailable` : elle ne permet pas d'écarter silencieusement un concurrent. Les alias et la recherche par date correspondent aux mécanismes décrits par la [documentation officielle TVmaze](https://www.tvmaze.com/api).

Lorsqu'un ID externe vérifié permet de réunir TMDB et TVmaze, les alias officiels enrichis sont maintenant conservés sur l'identité canonique. Auparavant la fusion reprenait seulement le nom principal TVmaze et pouvait perdre précisément l'alias qui correspondait à la release. Une année originale manquante en recherche peut également être complétée depuis les détails de la même identité ; une contradiction d'année conserve son refus.

Les ID externes contradictoires restent bloquants. Les dates ne se transmettent que dans le cadre du lien de séries vérifié. Les contrôles existants d'identité d'épisode et de conflit de calendrier sont conservés. Un épisode lié à une autre série ne fournit aucune preuve, même si sa date correspond.

### Collecte et budgets

Les pages prévues doivent toutes être reçues et cohérentes avant projection. Aucun premier résultat, classement de popularité ou sous-ensemble tronqué n'est utilisé pour éviter un dépassement.

Une réduction avant budget est possible uniquement pour une contradiction chronologique indépendante du titre : un épisode régulier explicitement numéroté (saison ordinale 1–99, épisode positif) et daté ne peut appartenir à une série dont la première diffusion se situe dans le futur au-delà de la tolérance d'année existante. Les années absentes, invalides ou contradictoires restent visibles. Les épisodes spéciaux, dates seules et coordonnées annuelles ne sont pas éliminés par cette règle. Les verrous, identités établies, corrections et assertions externes empêchent ce filtrage anticipé. Le ranker expose la même contradiction, `series-premiere-after-episode-date`.

Ce filtre n'est pas une stratégie Films titre + année : une série ayant commencé vingt ans avant l'épisode reste admissible. Il ne permet pas de résoudre les deux dépassements historiques dépourvus de ces preuves.

Les plafonds restent **12 pages TMDB, 100 candidats, 6 concurrents à enrichir, 30 requêtes logiques de recherche/preuve**, avec les délais existants. Aucun seuil de confiance ni marge n'est modifié. Davantage de titres officiels à vérifier peut entraîner une abstention de budget supplémentaire ; aucune couverture partielle n'est déclarée complète pour éviter cela.

## Cas de référence

| Cas | Constat repris / correction P5 | Limite restante |
| --- | --- | --- |
| Traces S01E01/S01E02 | Ambiguïté historique confirmée ; titres officiels, alias et fusion vérifiée améliorés. Tests de preuves S/E et d'homonymes. | Pas de rangs historiques exploitables pour certifier l'identité correcte ou une récupération réelle. Deux homonymes corroborés restent ambigus. |
| Lie To Me S02/S03 | Même traitement des titres officiels et de la preuve de saison. | L'existence d'une saison chez plusieurs homonymes ne les départage pas à elle seule. |
| Sacrifice S02E10 | Le retour immédiat `movie` de la catégorie Film ignorait les preuves S/E. Une release non protégée avec saison+épisode explicites peut maintenant rejoindre TV. | Pas de publication réelle ni d'identité provider certifiée sur ce programme pendant P5. |
| Affaires sensibles | Dates et S/E uniformes entre Serie/Emission ; proposition programme/sujet contrôlée. Les trois doublons restent d'abord éligibles aux vérifications P4. | Une émission avec sujet et seule année 2026 ne prouve ni la création du programme ni l'existence d'un épisode. |
| 66 minutes / grand format | Année de diffusion séparée de l'année originale ; coordonnées annuelles datées vérifiables par date sans inventer une saison ordinale. | Le sous-titre reste distinctif ; sans date/alias/ID vérifiable, aucune assimilation automatique des variantes. |
| Mauvaise Influence / C'est la famille | S/E, titres officiels et identités interproviders vérifiés ; P4 reste prioritaire pour leurs doublons. | Les fixtures démontrent les mécanismes, pas l'état réel des donneurs ou des catalogues actuels. |
| Esprit Japon Nolife | Proposition d'intégrale sans suffixe, conditionnée à une chaîne officielle du candidat. | Aucun appel réel pour affirmer que les métadonnées actuellement retournées permettent cette résolution. Suffixes de plusieurs mots non généralisés. |
| Survivor | Rejeu synthétique : 105 identités après 12 pages complètes / 210 observations FR-EN ; recherches identiques mutualisables. | **Dépassement de 100 conservé.** Les titres et noms d'édition/saison du torrent ne suffisent pas à éliminer des identités avant vérification. |
| Fort Boyard S36 | Nombre de concurrents et limite d'enrichissement maintenant explicites. | **Plus de six concurrents : abstention conservée.** L'absence/planning d'une saison ne prouve pas qu'elle n'existe pas. Pas de validation de tous les concurrents hors budget. |
| Albator84 S01 | Une saison Animation avec métadonnées vidéo suffisantes peut désormais prouver TV malgré la famille Movies/Anime. | Cela corrige le routage, sans renommer Albator84 ni inventer une correspondance avec un titre japonais/anglais. |

Aucune règle ne reconnaît un programme de cette table par son nom.

## P4, diagnostics et réutilisation

P4 demeure le premier contrôle local et garde ses protections contre les corrections manuelles, contradictions et cascades. Un refus `donor_unverified_or_manual`, `scope_or_evidence_insufficient` ou `no_candidate` est un constat de réutilisation locale, pas un échec de recherche TV. Un succès P4 peut éviter complètement le matching textuel.

Les diagnostics de couverture existants reçoivent maintenant :

- `tv_input` : saison/épisode effectivement interprétés, date, année de diffusion connue, rôle des années des propositions et nombre de propositions ;
- `tv_projection` : observations, candidats projetés et limite ;
- `tv_evidence` : candidats collectés/conservés, réduction chronologique et nombre exact de concurrents plausibles face à la limite de six.

Ces éléments complètent l'entrée `local_identity_reuse` de P4 et le classement/résultat déjà conservé par P0. Ils permettent de distinguer un refus local suivi d'une ambiguïté TV, un problème de couverture, un budget ou une publication. Les nouveaux compteurs ne journalisent ni sujet libre, titre, URL authentifiée, synopsis ou secret.

Les artefacts repris ne démontrent pas de récupération réelle post-P4. Aucun Retro Fetch n'a été lancé pour fabriquer cette mesure. Les assertions ci-dessus portent sur le code et les tests, sans double comptage de gains P4/P5.

## Appels, compatibilité et déploiement

Les réponses de recherche/preuve continuent à utiliser les caches existants : mutualisation des requêtes identiques simultanées, TTL de deux minutes et capacité de 256 réponses par cache. Les alias TVmaze bénéficient de ce mécanisme avec validation de leur structure. Les erreurs/réponses incomplètes ne deviennent pas des décisions mises en cache. Les budgets logiques restent inchangés même en cas de cache hit.

Un test exécute quatre recherches TV identiques par client et vérifie un seul transport TMDB et un seul transport TVmaze. Les détails et alias supplémentaires sont bornés par les budgets actuels ; aucun parcours manuel, nouveau provider, scan Arr ou validation massive n'est ajouté. Les identités et posters déjà établis utilisent toujours leur rafraîchissement protégé.

Le contrôle travaille sur les titres bruts existants. Les releases déjà persistées en bénéficieront au prochain Retro Fetch où elles sont sélectionnées, sans réimport, modification de catégorie fonctionnelle ou réécriture massive des champs parsés. P5 ne nécessite pas de migration SQLite supplémentaire.

## Validation

- Suite élargie : **679 tests réussis, 0 échec**, couvrant TV, Films, collecte/pagination, RSS direct, identités, corrections manuelles, publication partagée, P4, replay, multilingue et caches.
- Dernier passage P5 : **28 tests réussis, 0 échec**, comprenant deux cas ajoutés ensuite pour le rapprochement annuel par date et le refus d'un épisode d'une autre série. Ces suites se recouvrent ; leurs effectifs ne s'additionnent pas.
- Compilation réussie et `git diff --check` sans erreur.

Les attentes historiques modifiées correspondent aux comportements intentionnels de P5 : routage TV d'Animation S01 avec métadonnées vidéo, conservation de deux propositions programme/sujet, et abstention d'un cas de replay où seule une année lexicale non corroborée autorisait auparavant l'acceptation. Les contrats de rafraîchissement d'identités établies et de partage de posters restent verts, notamment pour un titre avec année originale explicitement parenthésée.

Les tests réutilisent les titres de l'audit, avec des réponses provider synthétiques pour isoler les causes. Aucun résultat n'est présenté comme une validation exhaustive des 167 releases actives ou une promesse de poster au prochain passage. Survivor, Fort Boyard et les véritables homonymes sans preuve discriminante restent des limites explicites.
