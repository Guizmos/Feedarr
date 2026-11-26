# P6C — Reconnaissance des titres et segments

Implémentation locale du 26 novembre 2026 à partir du rapport P6 et de l'audit existant. Aucun nouvel audit, appel réel aux providers ou traitement de données de production.

## Corrections

Les règles restent dans `OfficialCompoundTitle`, consommées par le ranker existant et la priorité exacte P6A. Aucun nouveau moteur, endpoint provider ou budget. Les seuils et extensions conditionnelles P6B restent inchangés.

Deux noms officiels distincts et complets de la même identité TMDB typée peuvent désormais composer exactement une requête même lorsque l'un est contenu dans l'autre, si l'année extraite concorde et le type est confirmé. Une répétition de deux noms identiques ne constitue pas une preuve. Sans année fiable, la protection antérieure contre les noms contenus reste applicable. La couverture doit toujours être complète et ordonnée : aucun suffixe libre, traduction inventée, morceau réordonné ou nom d'une autre identité.

La reconnaissance vidéo compare aussi les formes numériques équivalentes aux titres officiels rattachés au candidat : ordinaux chiffrés français/anglais (`11e`, `11ème`, `11th`), chiffres romains I à XX à une frontière de numérotation et marqueurs explicites comme `t02` ou `Part 02`. Elle exige une année extraite égale à l'année du candidat et le même type vidéo. Elle ne canonicalise pas les titres de jeux. Les zéros d'un numéro libre restent significatifs : `522.01` ne devient pas `522.1`. Fractions, rapports et ponctuations numériques significatives restent contrôlés.

Un suffixe « 1 » peut désigner le premier film dont le titre officiel est non numéroté, avec année et type concordants. Les autres numéros restent conservés : `Gladiator t02` peut correspondre à `Gladiator II`, jamais à `Gladiator`. Un véritable `Part 2` reste distinct d'un `Part 1` et d'une œuvre non numérotée.

Pour un spectacle, un titre officiel portant explicitement une **édition numérotée** permet de reconnaître le numéro équivalent, puis un segment terminal `Part N`, `Partie N` ou `Ne soirée`. L'édition reste une preuve d'identité : 11 n'est pas 12. La catégorie Spectacle seule ne suffit pas à supprimer une partie. Sans preuve officielle d'édition, la nouvelle règle refuse cette simplification. Un titre officiel qui distingue déjà une partie reste distinct.

Un libellé vidéo terminal de restauration ou version longue peut être reconnu après couverture complète du titre officiel, sans changer l'identité de l'œuvre. Le parsing accepte désormais `année + Version Longue/Restaurée + métadonnées connues + suffixe technique riche`, dans les catégories Film, Animation et Spectacle. Les mêmes mots appartenant au titre officiel peuvent toujours être reconnus comme titre complet. Aucun retrait générique de `Part 2`, DLC ou édition de jeu.

Les raisons existantes de décision portent la provenance du titre officiel et, selon le cas, `official-numeric-variant-exact`, `first-installment-label-warning`, `video-editorial-suffix-warning` ou `event-broadcast-segment-warning`. Les contrôles de conflits, homonymes, année, type, identités établies et corrections manuelles restent actifs. Les règles d'images et la priorité FR ne changent pas.

## Cas de référence et portée des preuves

| Cas | Vérification locale | Limite |
|---|---|---|
| Release 31753, Astérix | Requête exécutée sans `2014 MULTi 1080p HDRip`, année 2014 ; composition des deux noms officiels testée | La génération actuelle issue des correctifs précédents nettoie déjà ce titre ; les résidus historiques ne sont pas reproduits. Pas d'ajout inutile au vocabulaire technique. |
| Releases 31971–31977, Duos impossibles | Sept titres bruts repris de l'audit ; numéro 11 préservé, ordinal/édition/segment reconnus | Titre officiel « 11e édition » simulé pour le candidat historique 1814493 ; sa présence réelle dans le catalogue n'est pas affirmée. |
| Mission Impossible 1 | Correspondance au titre officiel non numéroté avec année 1996 | Ne dispense pas d'une année fiable ou de la résolution des homonymes. |
| Harry Potter and the Deathly Hallows Part 2 | Équivalence avec `Part II` | Refus de `Part 1` et du retrait global de partie. |
| Gladiator t02 | Équivalence avec `Gladiator II`, année 2024 | Refus du premier Gladiator, y compris si son année est donnée. |
| Les Enfants du Paradis, version restaurée | Titre couvert puis suffixe éditorial ; parsing avant/après année | Année incompatible toujours refusée. |
| Macbeth, version longue | Même reconnaissance et parsing avant/après année | Aucun choix automatique entre plusieurs identités équivalentes. |

31753 désigne l'ID local de release ; 170522 est le candidat TMDB cité par les diagnostics. Les tests d'intégration Astérix utilisent une base temporaire et des réponses HTTP simulées, en RSS et Torznab. Ils commencent avec `title_clean` obsolète et année absente : le service régénère réellement la requête depuis le titre brut, publie via les mécanismes existants et conserve le titre clean persisté. Aucun réimport ni changement de catégorie n'est nécessaire. Le prochain Retro Fetch normalement déclenché utilise ce même chemin ; aucun Retro Fetch n'a été lancé ici.

Les titres provider des fixtures sont des preuves **simulées**, pas de nouvelles validations de catalogue. Aucun gain réel de couverture ni récupération réelle des sept Duos ne sont annoncés. Si les noms officiels réellement disponibles ne couvrent pas les titres, les règles ne fabriquent pas les noms manquants.

## Validation ciblée

- 281 tests réussis : P6C, composition, P6A, P6B, parsing P2/corpus, alias Films et priorité exacte par année. Dont 24 tests P6C, incluant les sept releases Duos et les deux chemins d'intégration Astérix.
- 282 tests complémentaires réussis : génération de requêtes, sécurité/contextes de parsing, TV, réutilisation P4, multilingue, projection Films et routage RSS/Torznab.
- Non-régressions explicites : mauvais numéro d'édition, parties incompatibles, absence de preuve d'événement, ponctuation numérique, homonymes, année inconnue/incompatible, jeu hors portée et numéros d'archives avec zéros significatifs.
- `git diff --check` sans erreur. Aucun appel provider réel, changement de production, purge, relance massive, commit ou push.

Les limites assumées sont les noms officiels absents ou incomplets, les variantes numériques hors du vocabulaire borné, les spectacles sans preuve officielle d'édition et les identités concurrentes non départagées. Les budgets P6B et les protections P6A ne sont pas assouplis davantage.
