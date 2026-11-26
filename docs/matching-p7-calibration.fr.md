# P7 — Calibration finale du matching V2

Ce rapport conserve les mesures avant validation. Le profil a ensuite été validé et implémenté : voir [la livraison P7](matching-p7-implementation.fr.md).

Calibration locale du 26 novembre 2026. Référence fournie : **177 releases sans poster, environ 93,41 %**. Aucun seuil, veto, budget ou chemin de production modifié. Aucun provider réel appelé. Radarr/Sonarr est exclu de l'analyse et des propositions.

## Recommandation

**Proposer le profil Équilibré, 0,85 / 0,90 / 0,05, uniquement pour les reconnaissances vidéo suffisamment corroborées ; conserver le comportement actuel ailleurs.** Desserrer dans ce même périmètre l'exigence d'égalité lexicale absolue. Pour les programmes TV déjà reconnus exactement dans deux catalogues reliés, supprimer l'exigence d'identification de l'épisode lorsqu'on associe seulement l'identité du programme et son poster.

Ce choix est une préférence de calibration contextualisée, **pas un optimum de couverture démontré**. Les quatre profils bruts ne démontrent aucun nouveau vrai positif dans les données exploitables. Le profil Léger est neutre sur ce panel. Équilibré global introduit une régression de preuve TV ; Permissif global introduit aussi deux régressions des protections P6A. Il n'est donc pas recommandé de remplacer globalement les trois valeurs du constructeur.

Le potentiel réel quantifiable ici est limité : **quatre dossiers TV** sont bloqués uniquement par la preuve de sujet/épisode malgré une identité de programme exacte et corroborée. Ce sont quatre acceptations conditionnelles à proposer, pas quatre bons posters certifiés. Les autres extensions de reconnaissance n'ont pas de gain réel démontré sur les pools complets disponibles. Avec le dénominateur approximatif implicite de la référence (~2 686 releases), 95–97 % demanderait environ **43 à 97 posters supplémentaires**. Ces travaux ne permettent pas d'annoncer ce résultat ni d'extrapoler un taux de faux positifs durable.

## Portée réelle des seuils

`CandidateRankingPolicy` définit bien similarité 0,90, acceptation 0,92 et marge 0,08. La similarité n'est ni une distance orthographique ni une probabilité : moyenne du recouvrement de tokens et d'une sous-séquence ordonnée, après normalisation stricte et comparaison des noms alternatifs/officiels disponibles. Abaisser son seuil ne corrige pas une traduction absente ou une segmentation sans tokens communs.

Le score est principalement **additif** : `0,80 × similarité + 0,10 type`, puis un seul bonus de support indépendant de 0,10 et un bonus d'année de 0,10 si exacte, 0,06 si voisine. Il est arrondi et plafonné à 1. Le support RSS concordant peut ajouter 0,10 à `EvidenceScore`, conservé pour la marge même si `Confidence` est saturée. Plusieurs preuves TV/identité ne cumulent pas plusieurs bonus de support. `category-agrees` n'ajoute pas de points.

Il n'existe pas de chaîne générale de pénalités à additionner puis supprimer. Les contradictions fortes rejettent ; plusieurs motifs faibles mettent `sufficient=false`, même à score 1. `RequireExactTitleForAcceptance=true` maintient notamment l'abstention sur les titres non exacts sans identité de confiance, quels que soient les trois seuils. Un seuil numérique abaissé ne rétablit pas `sufficient`.

La marge compare les deux meilleures **identités éligibles**, sur `EvidenceScore` si présent. Deux titres exacts à l'année et à ±1 an valent couramment 1 et 0,96. La marge 0,04 les départage ; 0,05 et 0,06 les laissent à P6A. Une égalité 1/1 reste ambiguë dans les quatre profils.

Les paramètres sont partagés par le ranker vidéo, jeux et autres catégories qui l'utilisent, mais également par des classifications avant enrichissement et des contrôles de collecte/P6A. Modifier leurs valeurs par défaut pourrait donc modifier la sélection des concurrents TV, les appels utiles et l'applicabilité des protections. La proposition porte sur la décision finale dans l'existant, **sans modifier la classification P6B, les plans, seuils providers propres, plafonds ou budgets**. Elle n'est pas un simple remplacement aveugle des valeurs par défaut.

Avant le veto d’exactitude, pour un film soutenu uniquement par titre/type/année exacte, les similarités minimales effectives imposées par le score sont respectivement 0,90, 0,8875, 0,875 et 0,85, combinées au seuil lexical de chaque profil. Avec une année voisine, elles deviennent 0,95, 0,9375, 0,925 et 0,90. Avec une preuve indépendante supplémentaire, le seuil lexical pèse davantage. Une année d'épisode ou de saison ne peut pas être utilisée comme année originale pour obtenir ces bonus.

## Données réutilisées et limites de rejeu

Trois ensembles ont été gardés distincts :

1. **75 cas de fixtures existantes** : 34 positifs simulés, 35 abstentions attendues et six sous-ensembles de recherche issus de l'audit sans vérité terrain ni garantie de couverture transport. Représentatifs du ranker, typographie, localisations, compositions, P6C, TV, préservation et négatifs P6A. Aucun pool réel manquant n'a été complété.
2. **Captures et labels revus du benchmark local `reference-locale`** : sélection bornée de 35 représentants, en regroupant les labels par œuvre et en incluant explicitement le contrôle négatif 761. Le runner offline existant, dans une copie temporaire avec export `RankingInput`, ouvre le snapshot en lecture seule. **34 entrées complètes** sont rejouables ; 761 manque d'une capture et reste exclu. **Zéro transport réseau provider**. Les labels vérifient l'identité catalogue, pas le fichier média ni le poster. Les profils portent sur les mêmes contextes et candidats enrichis exportés, pas sur une réinvention des réponses. Le Replay n'est pas une preuve d'équivalence de toutes les étapes de publication active.
3. **Dernier CSV existant `retro-fetch-20261126-113147-66544d.csv`** : 197 lignes distinctes, 136 diagnostics non tronqués (80 pools vides, 56 non vides), 32 tronqués, 29 sans ranking. Les 136 comprennent 60 films, 44 séries/émissions et 32 jeux. Ce périmètre n'est pas identifié comme les 177 releases actives de la référence : aucun nouveau rapprochement exhaustif avec la BDD n'a été entrepris.

Les CSV exposent scores et raisons, mais pas les DTO complets de titres, contexte et provenance. Leur analyse est une **projection conditionnelle des diagnostics historiques**, pas un rejeu intégral du moteur. Les 32 pools tronqués et les 29 absences de ranking ne participent à aucune estimation d'acceptation. Aucune première place dans un extrait n'est considérée comme une identité unique.

## Comparaison des quatre profils

### Fixtures : séparation entre qualité simulée et cas inconnus

| Profil | Positifs simulés acceptés /34 | Abstentions attendues violées /35 | Sous-ensembles d'audit acceptés /6 |
|---|---:|---:|---:|
| Actuel 0,90 / 0,92 / 0,08 | 34 | 0 | 0 |
| Léger 0,88 / 0,91 / 0,06 | 34 | 0 | 0 |
| Équilibré 0,85 / 0,90 / 0,05 | 34 | 1 | 0 |
| Permissif 0,82 / 0,88 / 0,04 | 34 | 3 | 4 |

Aucun positif du panel n'est perdu ni remplacé par une autre identité. L'ablation supplémentaire `RequireExactTitleForAcceptance=false` ne change pas ces comptes dans ce corpus précis ; elle n'est pas une validation d'une suppression générale de ce verrou.

La régression TV est **FBI sans preuve de structure** : titre exact, champs saison/épisode concordants, mais pas d'objet de preuve TV rattaché à l'identité. Le score est 0,90 ; Équilibré et Permissif l'acceptent. La baisse du seuil seule contourne ici la raison pour laquelle le profil actuel s'abstient. Ce cas simulé vérifie une exigence ; il ne prouve pas que l'identité réelle FBI serait fausse.

Les deux autres régressions sont **Bruised et Whistle**. Les historiques synthétiques des tests existants corroborent une première originale concurrente dans l'année demandée. La marge Permissif accepte déjà le candidat à l'année ; `MovieExactYearPriority` conserve cette décision (`legacy_decision_preserved`), sans examiner les historiques. Le test compare aussi cette étape P6A, pas seulement le score. **Overdose et Gentle Monster restent ambigus dans les quatre profils.**

Les quatre sous-ensembles d'audit acceptés par Permissif sont Oppenheimer, Sentimental Value, Short Circuit et Piece by Piece. Leurs IDs proposés ne sont pas des labels de justesse ; ils ne comptent pas comme nouveaux vrais positifs. Plusieurs sont déjà couverts par la priorité P6A au niveau du pipeline complet.

### Captures réelles avec labels vérifiés

| Profil | Identités correctes /34 | Faux positifs vérifiables | Nouvelles bonnes acceptations |
|---|---:|---:|---:|
| Actuel | 34 | 0 | 0 |
| Léger | 34 | 0 | 0 |
| Équilibré | 34 | 0 | 0 |
| Permissif | 34 | 0 | 0 |

L'ablation du veto exact donne également 34/0/0 pour chaque profil. Répartition : **29 séries, quatre émissions et un film**, tous déjà reconnus. Le contrôle négatif Star Academy 761 est non évaluable ; son absence ne vaut pas réussite. Ce corpus positif vérifié et très TV confirme une conservation des associations ; il ne mesure pas la précision future des nouvelles acceptations sur les échecs actuels. Aucun gain ni effet propre aux jeux ne peut en être déduit.

### Échecs réels : projection des diagnostics complets

| Profil | Acceptations déjà présentes dans le ranking | Nouvelles acceptations conditionnelles | Nouveaux vrais positifs prouvés | Identités nouvelles non validées |
|---|---:|---:|---:|---:|
| Actuel | 3 | 0 | 0 | 0 |
| Léger | 3 | 0 | 0 | 0 |
| Équilibré | 3 | 0 | 0 | 0 |
| Permissif | 3 | 3 | 0 | 3 |

Les trois acceptations actuelles n'ont pas de poster : Les Aiguilleurs 35366, La Tragédie du sang contaminé 31943 et Horns of Deliverance 31895 échouent à la disponibilité/sélection d'image. Aucun profil de scoring ne corrige cela.

Les trois nouvelles propositions de Permissif sont **Captives 35191 → 712022**, **Aline 29071 → 799815**, **Bruised 29072 → 1412141**, toutes côté film, par marge 1−0,96. Aucun gain conditionnel côté séries/émissions ou jeux. Bruised contredit la protection d'ambiguïté originale connue dans P6 ; l'identité souhaitée n'est pas certifiée par ces logs. On ne le présente donc ni comme un bon match ni comme un faux positif d'identité définitivement vérifié. **Zéro nouveau vrai positif confirmé ; zéro nouvelle identité fausse certifiée ; trois inconnues, dont une déjà incompatible avec une protection impérative.**

Retirer seulement le veto exact ne débloque pas davantage ces diagnostics. Deux Duos, 31975/31976, ont un score 0,911111 ; le profil Léger pourrait lever leur minimum lexical, mais le veto numérique reste présent. Un jeu à score élevé, Doom The Dark Ages édition Premium 31842, reste bloqué par l'édition liée non résolue. Ce ne sont pas des pénalités à gommer pour faire monter le score.

## Modifications concrètes proposées, non implémentées

1. **Équilibré contextualisé dans le décideur actuel.** Autoriser la comparaison non exacte d'un titre vidéo provider vérifié quand le type concorde et qu'une année originale fiable exacte, ou une structure TV effectivement vérifiée pour le contexte demandé, apporte le support indépendant. Appliquer 0,85 / 0,90 / 0,05 dans cette portée. Garder les valeurs et exigences actuelles hors de cette portée, notamment jeux, année manquante et structure TV non vérifiée. Réutiliser les booléens de preuve et `OfficialCompoundTitle` existants ; aucun nouveau pipeline ou paramètre utilisateur. Un gagnant doit toujours se détacher dans le pool complet, sans ignorer ses concurrents forts. Gain réel prouvé ici : **0** ; c'est une possibilité de reconnaissance supplémentaire à valider, pas une promesse de couverture.
2. **Portée programme distincte du sujet d'épisode.** Transformer `programme-subject-needs-episode-evidence` en avertissement quand le programme correspond exactement à un titre officiel, son identité intercatalogues est vérifiée, le pool complet permet un gagnant unique et aucune contradiction de saison/épisode/date n'existe. Cela associe l'entité programme, pas une identité d'épisode inventée. Les quatre diagnostics à un seul candidat sont 31796, 31795, 31794 → TMDB 117404 et Affaires sensibles 31719 → TMDB 134594. Score 1 et corroboration intercatalogues déjà présents. Potentiel : **4 acceptations conditionnelles**, justesse et disponibilité du poster encore inconnues. Les deux autres occurrences de ce veto sont dans des pools tronqués : aucun gain compté.
3. **Garder la séparation P6A des contradictions originales.** Aucune marge 0,04 globale, aucun contournement des historiques contradictoires déjà connus par un nouvel `Accept`. Les homonymes exacts et les premières originales concurrentes restent au niveau exact P6A ; un voisin plus faible ne doit pas imposer inutilement les règles du flou. Aucun nouvel historique provider demandé pour cette calibration ou cette proposition.
4. **Ne pas changer les autres veto avec ce panel.** Conserver nombres réellement identitaires, éditions/DLC/plateformes de jeux, liens d'identités contradictoires, dates contradictoires et identité parent d'épisode. Les insuffisances de saison ou de chaîne sans preuve ne deviennent pas des avertissements généraux. Priorité FR, fallback EN, entités partagées, corrections manuelles et identités établies restent indépendants de cette calibration.

La première proposition donne davantage de place au moteur de reconnaissance ; la deuxième retire un refus redondant à la portée programme. Leurs gains ne s'additionnent pas à ceux d'une baisse globale : le panel ne contient pas de nouveaux vrais positifs permettant cette extrapolation. Le profil définitif et ces changements attendent validation.

## Reproductibilité et contrôle de portée

Les deux tests de calibration dans `MatchingP7CalibrationTests.cs` injectent uniquement des politiques dans le ranker pur. Le second requiert explicitement les chemins d'un export complet et de ses labels via `FEEDARR_P7_CAPTURED_REPLAY` et `FEEDARR_P7_CAPTURED_SELECTION`. Ils ont réussi ; **75 × 4 × 2 = 600 évaluations de fixtures** et **34 × 4 × 2 = 272 évaluations sur captures**. Ce sont 872 évaluations hors réseau, pas 872 tests indépendants ni des appels providers. Les contrôles de préservation manuelle/verrouillage/identité existante font partie des fixtures, dans quatre catégories.

`tools/matching-benchmark/calibrate-p7-diagnostics.py` reproduit la projection du CSV sans accès réseau ou BDD :

```sh
python3 tools/matching-benchmark/calibrate-p7-diagnostics.py \
  .tmp/local-dev/data/logs/retro-fetch-20261126-113147-66544d.csv \
  --output /tmp/feedarr-p7-diagnostics.json
```

Les métriques agrégées et l'empreinte du CSV figurent dans `matching-p7-calibration-mesures.json`. Les exports détaillés et le runner temporaire restent sous `/tmp/feedarr-p7`. Les captures, labels et snapshot originaux n'ont pas été enrichis ; les captures manquantes n'ont déclenché aucun fallback réseau. La calibration n'effectue aucune écriture de BDD et ne valide aucun poster visuellement. Aucun audit exhaustif, changement moteur/production, purge, Retro Fetch, commit ou push.
