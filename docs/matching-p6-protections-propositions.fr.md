# P6 — Pertinence des protections et simplification proposée

25 novembre 2026. **Audit ciblé et propositions uniquement, soumis à validation.** Aucun changement de moteur, seuil, budget, configuration, BDD ou production ; aucun Retro Fetch, appel API provider, build, commit ou push. Les modifications P0–P5 déjà présentes sont conservées. Seul ce rapport est ajouté au dépôt.

## Conclusion et décision proposée

Il existe des abstentions excessives démontrées. Les premières simplifications à valider sont :

1. Donner la priorité à un **titre officiel exact + année fiable + type confirmé**, sans lui imposer la marge du matching approximatif contre les seules années voisines.
2. Transformer les différences d'édition vidéo et les notes de ressortie en avertissements lorsqu'elles ne désignent pas une autre identité. Oppenheimer fournit un exemple concret.
3. Lever les veto numériques généraux pour les identités exactes et uniques suffisamment documentées. Deux dossiers ont un unique veto résiduel identifiable : `418` et `9/11`.
4. Réduire le nombre de concurrents TV à enrichir obligatoirement ; augmenter les budgets ciblés lorsque le groupe fortement corroboré le justifie. L'expansion systématique des alias P5 fait maintenant passer 14 anciens échecs en dépassement d'enrichissement, sans gain démontré sur ces dossiers.
5. Séparer la fiabilité d'une identité P4 de celle de son fichier poster. Ne pas assimiler absence d'un alias historique et contradiction positive d'identité.

**Recommandation : assouplir la décision exacte et certains budgets, sans baisser globalement la qualité exigée pour le flou.** L'objectif 95–97 % est cohérent avec cette orientation, mais n'est pas démontré par les données disponibles. Les gains d'association, les posters effectivement disponibles et leur langue doivent être mesurés séparément.

## Périmètre et preuves réutilisées

Sources principales :

- Audit initial `.tmp/audit-posters/rapport-sans-posters-2026-11-25.md`, ses sept approfondissements réels et ses diagnostics.
- Rapports P0, P1, P3 dans `.tmp/audit-posters/`, corrections/tests P2 disponibles, [P4](matching-p4-local-identity-reuse.fr.md) et [P5](matching-p5-tv-emissions.fr.md).
- Deux CSV existants : `retro-fetch-20261125-190328-a91dc3.csv` et `retro-fetch-20261125-200501-73850c.csv`, dans `.tmp/local-dev/data/logs/` ; lectures ciblées du `backend.log` existant.
- Code actuel du ranker, priorité d'année P1, composition de titres, enrichissement TV, réutilisation P4 et sélection des images.
- Artefacts historiques de comparaison dans `~/Documents/Projets/Feedarr-benchmarks/`, sans exécuter les moteurs ni lire leurs bases.

Aucune nouvelle interrogation release par release ni recherche provider. Les nombres ci-dessous proviennent de diagnostics déjà produits, pas d'un nouvel audit de bibliothèque.

### Comparabilité des chiffres

Les observations 96 % V1, 92 % V2a et environ 93 % P5 sont des références utilisateur. Elles n'ont pas ici un dénominateur figé commun ni une revue exhaustive des associations et langues d'image. Le rapport initial mesurait 211 manquants sur 2 647 releases, toutes sources de son instantané ; la référence précédente de 167 concerne les sources actives. Le dernier CSV contient **187 échecs distincts**, pas une mesure de 187 manquants sur les seules sources actives. Ces périmètres ne sont pas interchangeables.

Le dernier batch va du **25 novembre, 21:05:01 à 21:06:16, heure de Paris**. Le précédent va de 20:03:29 à 20:04:28. Les 187 IDs du dernier CSV figurent tous dans le précédent, qui en contient 188. La seule absence est `29307 — Les Apprentis Aventuriers`. Cette absence ne prouve pas, à elle seule, une publication réussie.

## Constats quantifiés

### Exactitude forte encore bloquée

Dans le dernier CSV, **16 dossiers movie** exposent au moins un candidat avec titre officiel exact, année exacte et type concordant. Ce ne sont pas 16 vrais positifs certifiés :

- **5 dossiers** montrent déjà plusieurs identités exactes la même année : Overdose, Gentle Monster, Just the Two of Us, Free Fall, New Life. Une priorité d'année ne les départage pas.
- **11 dossiers** montrent une seule identité exacte à l'année dans l'extrait : Oppenheimer, 9/11, 418, Carmen, Sight, Camille, Aline, Bruised, Whistle, A Family, Piece by Piece.
- Plusieurs classements sont tronqués à six candidats. « Un seul visible » ne devient pas « unique dans tout le catalogue », particulièrement si une autre protection est ensuite assouplie. Les pools complets de l'audit initial restent utilisables pour les cas approfondis.

| Dossier réel | Preuve existante | Conclusion P6 |
| --- | --- | --- |
| 32718 Oppenheimer | TMDB 872585, 2023, score 1,00 ; homonyme 2024 à 0,96. Dernier log : année principale et première année 2023, date originale présente ; veto `ambiguousEdition=True`, note US du 03/11/2023, type cinéma. | La note d'édition du même film ne prouve pas une autre identité. Veto global excessif ; candidat prioritaire pour l'assouplissement. Le texte libre de la note n'est pas conservé dans ce diagnostic : aucune interprétation précise de son contenu n'est inventée. |
| 31202 9 11 The Final Minutes Of Flight 93 (2020) | Un candidat, TMDB 742159 ; officiel exact, année/type concordants, score 1,00 ; seul veto `title-numeric-punctuation-mismatch`. | La normalisation accepte le titre, puis la ponctuation numérique le rejette. Remplacer ce veto par un avertissement dans ce contexte précis ; conserver les chiffres et leur ordre. |
| 29026 418 (2026) | Deux candidats dans l'extrait complet ; 1606527 à 1,00, officiel/année/type exacts ; autre résultat rejeté à 0,16. Seul veto du premier : `numeric-title-needs-independent-identity`. | Exigence d'un autre ID externe excessive pour ce niveau de preuve. Acceptation proposée si le pool confirme l'unicité ; pas d'exception codée pour « 418 ». |
| 29007 Overdose (2022) | 1024766 et 896485 tous deux exacts à l'année, à 1,00. | Ambiguïté réelle des preuves disponibles. Le badge Radarr ou le titre seul ne choisit pas le bon. |
| 29072 Bruised (2020) | Exact 1412141 en 2020 ; voisin 718633 en 2021, mais première observée au Canada le 12/09/2020, type première. | Contre-exemple à une priorité aveugle du seul champ `release_date`. L'année du fichier peut désigner la première du voisin. Il faut distinguer ce cas d'une simple édition. L'identité souhaitée n'est pas certifiée par ces logs. |
| 29074 Whistle (2025) | Exact 1526225 en 2025 ; voisin 1193501 en 2026, mais première en Italie le 25/09/2025. | Même réserve : le voisin peut appartenir à la même année originale demandée. Ne pas le traiter comme une simple sortie DVD tardive. |
| 32082 Piece by Piece | Exact 2024 à 1,00, voisin 2023 à 0,96. Refus P1 `neighbor_release_or_external_identity_competes`. | Motif trop agrégé : il mélange sortie voisine dans l'année et même IMDb. Une sortie locale/physique serait un veto excessif ; un conflit externe positif doit rester investigable. Les logs ne disent pas laquelle des deux branches a bloqué. |
| 32103 Sentimental Value / 29344 Short Circuit | Les logs P1 existants enregistrent déjà `outcome=applied`, avec respectivement 2 et 3 concurrents vérifiés. | Preuve qu'une priorité conditionnelle est utile. Ne pas compter à nouveau ces décisions comme gains futurs P6 ni les confondre avec une publication d'image confirmée. |

Les deux veto numériques seuls correspondent à **deux décisions d'identité qui pourraient changer sur les observations enregistrées**. Cela ne mesure ni leur justesse humaine ni la présence d'une affiche FR. Pour Oppenheimer, supprimer un veto d'édition ne prouve pas que tous les autres contrôles P1 auraient passé : il ne faut pas annoncer un gain déterministe en retirant cette seule condition.

### Élargissement excessif de l'enrichissement TV

Le dernier CSV contient 15 `tv_evidence_budget`, contre un dans le précédent : **14 transitions de motif**, toutes sur des dossiers déjà en échec. Elles ne représentent pas 14 posters perdus.

| Groupe | Releases | Concurrents plausibles après P5 | Ancien motif |
| --- | ---: | ---: | --- |
| Traces S01E01/S01E02, trois sources | 6 | 37 | ambigu |
| Lie To Me S02/S03 | 2 | 9 | ambigu |
| Sacrifice S02 et S02E10 | 2 | 26 | ambigu / vide |
| L'Amour est dans le pré | 1 | 8 | ambigu |
| Bel bien, deux sujets | 2 | 11 | vide |
| Sugar Rush | 1 | 10 | ambigu |
| Fort Boyard | 1 | 24 | budget déjà atteint |

Dans `TelevisionEvidence.cs`, le calcul des plausibles ne considère plus les contradictions de similarité de titre et de nombres comme éliminatoires avant enrichissement, pour permettre la découverte d'alias. Sur Traces, **les 37 candidats collectés deviennent 37 candidats à vérifier** ; sur Fort Boyard, 24 sur 24. Le budget de six bloque alors avant les détails. La protection d'exhaustivité des alias est devenue trop générale.

**Proposition :** utiliser d'abord les titres officiels déjà reçus et les preuves fortes disponibles ; réserver les recherches supplémentaires d'alias aux cas proches et à un petit repli optionnel. Ne pas imposer la vérification de tous les alias inconnus d'un résultat lexicallement éloigné avant d'accepter une identité fortement corroborée. Le risque résiduel accepté est de manquer un alias inattendu dans cette traîne ; ce n'est pas une collecte de pages incomplète. Si plusieurs concurrents forts subsistent, ils doivent tous être évalués ou le moteur s'abstient.

Les 14 dossiers retrouvent ainsi une possibilité d'évaluation ; **aucun nombre de nouvelles acceptations n'est démontré** par ce changement seul.

### Donneurs P4 : les refus ne disent pas tous la même chose

Répartition des 187 diagnostics de réutilisation :

| Motif | Nombre | Interprétation ciblée |
| --- | ---: | --- |
| `no_candidate` | 159 | Aucun donneur trouvé par les clés actuelles ; pas 159 protections excessives. |
| `donor_unverified_or_manual` | 18 | Les diagnostics de ces 18 lignes affichent provider/identité vides. Ils ne démontrent pas 18 identités historiques fiables cachées. Exemples : Traces, The FINAL J-Top, Divers Nolife, Blitzkrieg, Stellaris. |
| `poster_identity_unverified` | 3 | Mauvaise Influence 32013 et Affaires sensibles 31720/31721 ; identités de métadonnées TMDB connues dans le diagnostic. |
| `scope_or_evidence_insufficient` | 4 | Zelda / Super Mario, deux sources chacun ; ne pas lever globalement les portées jeux/plateformes. |
| `contradictory_donor_claims` | 3 | Affaires sensibles 31722, 66 minutes 31708 et grand format 31707. Les valeurs en conflit ne sont pas exportées ici. |

Deux simplifications sont justifiées par le code :

- `PosterProvider != ExtProvider` bloque l'identité elle-même. Un poster Fanart d'un film identifié par TMDB est pourtant un cas valide du pipeline. **La provenance de l'image et celle des métadonnées doivent être distinctes.** Le diagnostic ne précise pas si les trois donneurs concernés sont Fanart ou simplement historiques : cette cause individuelle reste à confirmer, sans les déclarer récupérables tous les trois.
- `claim.TvdbId != donor.TvdbId` ou la comparaison IMDb avec une valeur locale nulle produit un refus « contradictoire ». **Absent n'est pas contradictoire.** Deux IDs positifs différents restent un conflit ; un alias secondaire absent devrait être inconnu et ne pas annuler un TMDB déjà corroboré. Cette confusion peut concerner les trois refus, mais leur contenu exact manque dans les logs.

L'exigence simultanée d'un snapshot versionné sur la release et sur l'entité est excessive lorsqu'une racine d'identité indépendamment validée et une empreinte forte prouvent déjà le lien. À l'inverse, `ext_snapshot_version=NULL`, un vieux poster ou un titre clean identique ne prouvent pas cette racine.

**Proposition :** accepter une preuve de racine vérifiée, sans exiger des copies historiques redondantes ; réutiliser ensuite l'identité même si son image doit être recherchée à nouveau. Valider une racine ancienne une seule fois, au premier besoin, seulement lorsqu'elle porte déjà un ID exploitable et des preuves de portée ; mutualiser cette réponse par ID. Ne pas transformer globalement les historiques en « vérifiés », ni propager une correction manuelle sans preuve indépendante, ni autoriser des cascades. Pas de gain chiffré pour les 18 lignes sans identité démontrée.

## Classement des protections

| Protection | Classement | Simplification proposée / limite conservée |
| --- | --- | --- |
| Identités établies, corrections manuelles de la cible, révision de matching et transaction | **Nécessaire** | Priorité absolue ; aucun gain de couverture ne justifie leur écrasement. Les relectures avant publication et dans la transaction protègent des instants différents. |
| Type réellement contradictoire, IDs positifs incompatibles, remake distinct | **Nécessaire** | Refus conservé, même avec un titre exact. Une catégorie fonctionnelle mal choisie n'est toutefois pas une preuve de type provider contradictoire. |
| Même titre et même année pour plusieurs identités | **Nécessaire** | Ne pas résoudre Overdose ou Gentle Monster par popularité, première réponse ou marge abaissée. |
| DLC, jeu de base, plateforme, adaptation, suite effectivement différente | **Nécessaire** | Ne pas assimiler les éditions vidéo d'un même film aux variantes d'identité de jeux. |
| Pages manquantes, pagination incohérente, réponse d'un autre ID | **Nécessaire** | Une limite de ressources peut augmenter ; une collecte partielle ne devient pas complète. |
| Marge 0,08 appliquée à l'exact année contre les seuls voisins ±1 | **Excessive dans cette portée** | Priorité de classe avant comparaison de scores. Variante minimale possible : marge 0,04 uniquement dans cette portée, pas globalement. |
| Note d'édition dans l'historique d'une identité vidéo | **Excessive comme veto général** | Avertissement ; conflit bloquant seulement si une autre œuvre/identité ou une année originale concurrente est effectivement désignée. |
| Toute sortie d'un voisin dans l'année demandée | **Excessive** | Une ressortie, sortie locale, DVD ou diffusion TV ne suffit pas à lui donner priorité sur l'original exact. Une première en festival peut, elle, justifier une compétition. |
| Titre entièrement numérique exigeant toujours un autre ID externe | **Excessive pour l'exact unique** | Autoriser titre officiel + année fiable + type + unicité ; garder l'ambiguïté des homonymes numériques. |
| Ponctuation numérique en second veto après égalité officielle | **Excessive dans certains cas** | Avertissement contextuel pour date/numérotation équivalente ; préserver nombres, ordre, décimales et fractions réellement différentes. |
| Alias officiels encore inconnus de tous les concurrents faibles | **Excessive comme obligation globale** | Enrichir les concurrents forts et un repli borné, plutôt que tout résultat lexicallement éloigné. |
| Exiger le même provider pour identité et fichier poster | **Excessive / confusion de portée** | Découpler identité média et image ; vérifier le rattachement de l'image au même média. |
| Alias secondaire absent assimilé à un ID contradictoire | **Excessive / diagnostic erroné possible** | Trois états : concordant, contradictoire positif, inconnu. L'inconnu peut devenir avertissement selon la force du TMDB vérifié. |
| Même pool recalculé par plusieurs appels du ranker et listes d'alias recomputées | **Redondance de calcul** | Réutiliser classement initial et liste d'enrichissement tant que candidats/contexte n'ont pas changé ; recalculer après enrichissement. Gain CPU non mesuré, pas de gain de couverture revendiqué. |
| Historiques complets de tous les voisins avant une décision exacte | **Souvent redondants pour la décision** | Examiner d'abord les preuves disponibles ; consulter seulement les concurrents réellement susceptibles de changer le choix. Accepter explicitement le petit risque résiduel d'un voisin mal daté non enrichi. |
| Détails répétés au matching puis à la publication | **Partiellement redondants** | Réutiliser la réponse validée si ID, langue et champs nécessaires concordent. Un détail EN ne remplace pas un résumé FR, et les crédits absents ne sont pas inventés. |
| Contrôle d'ID de la réponse image, intégrité de fichiers, références GC | **Nécessaire pour l'image** | Ne pas les supprimer sous prétexte que l'identité est exacte. Un échec d'image ne doit pas rematcher une autre œuvre. |

## Hiérarchie proposée dans l'existant

Il ne s'agit pas de construire un moteur V3 ou cinq nouveaux pipelines. Utiliser les mécanismes existants et simplifier l'exception P1 :

1. **Identité externe vérifiée** : propriétaire, type et portée corroborés ; racine locale fiable ou réponse provider cohérente. Une assertion RSS brute, un badge Arr ou une association manuelle non revérifiée ne constituent pas cette preuve transmissible. La correction manuelle de la cible reste prioritaire et protégée.
2. **Titre officiel exact + année fiable + type confirmé** : égalité originale, localisée, alias officiel rattaché au même ID ou composition officielle complète ; année réellement extraite et non hypothétique. Une unique identité à ce niveau peut gagner sur des voisins plus faibles sans marge de flou. Si une première originale concurrente déjà connue couvre cette année, les deux appartiennent au même niveau et demandent une désambiguïsation.
3. **Correspondance enrichie** : preuves TV, alias, contexte et années compatibles ; conserver une marge entre concurrents comparables, adapter l'enrichissement à ce qui peut changer la décision.
4. **Approximatif** : seuils actuels comme point de départ, pénalités adaptées aux écarts bénins ; contradictions d'identité réelles toujours bloquantes. Les seuils restent calibrables, mais ce panel ne justifie pas une baisse globale immédiate.
5. **Abstention** : contradiction réelle, plusieurs meilleurs candidats équivalents, preuves essentielles absentes ou pool fort encore hors budget.

Le passage prioritaire au niveau 2 doit précéder les protections réservées au flou. Une année de fichier n'est pas infaillible : Bruised et Whistle illustrent le risque assumé si l'on choisit de ne pas examiner tous les historiques voisins. La proposition pragmatique est de bloquer sur les conflits positifs connus, sans exiger de prouver systématiquement l'absence de toute autre histoire de sortie.

### Titres bilingues, numéros et suffixes

`OfficialCompoundTitle` existe déjà : deux noms officiels complets d'une même identité peuvent couvrir toute la requête, sans traduction inventée. Il faut le conserver et l'assimiler à une preuve exacte, plutôt que lui rajouter des exigences de flou.

La règle de composition refuse aussi les noms contenus l'un dans l'autre. Cette protection peut être réexaminée pour deux noms officiels complets, de même ID/type, couvrant exactement toute la concaténation avec année fiable : un titre bilingue peut légitimement répéter une partie du nom. Aucun dossier réel de ce panel ne permet toutefois de chiffrer un gain de ce seul assouplissement ; ne pas annoncer les anciennes fixtures comme preuve de récupération.

Le dossier réel **31753 — Astérix The Mansion Of The Gods / Le Domaine des Dieux** reste rejeté : un candidat 170522 à 0,785714. La requête diagnostique contient encore `2014 MULTi 1080p HDRip`. Cela oriente vers la génération de requête/parsing effectivement exécutée, pas vers une baisse aveugle de similarité. Les fixtures bilingues de l'ancien rapport V2.3-I sont simulées et ne constituent pas des gains réels supplémentaires. Un suffixe qui couvre une partie officielle du titre doit rester ; une traduction d'une autre œuvre ne peut pas être concaténée comme preuve.

Les **sept releases 31971–31977 des Duos impossibles de Jérémy Ferrari** partagent un candidat de tête 1814493. Elles ont toutes aussi `title-similarity-insufficient`, en plus des nombres/parties. Supprimer seulement `title-number-mismatch` ne débloquerait donc **aucune des sept**. Proposition : canonicaliser les ordinaux équivalents et distinguer numéro d'édition, segment de diffusion et suite d'œuvre. Préserver le « 11 » ; ne pas retirer globalement `Part 2`. Un segment d'une même captation peut partager l'identité de l'événement, tandis qu'une véritable deuxième œuvre doit rester distincte. Le plafond de sept releases est un périmètre à examiner, pas une estimation de sept bons matchs.

## Budgets : assouplissements proposés à valider

| Budget / portée | Proposition concrète | Effet mesurable et risque |
| --- | --- | --- |
| Projection TV, actuellement 100 | **150 pour le parcours TV ciblé** | Les dix lignes Survivor avec 105 identités pourraient atteindre l'étape suivante. Cela ne garantit pas le matching : le budget d'enrichissement peut encore bloquer. |
| Concurrents à enrichir, actuellement 6 | **12 pour un groupe fortement corroboré**, après réduction de la traîne faible | Lie To Me, avec neuf concurrents dans le diagnostic actuel, pourrait être évalué si ce groupe reste pertinent. Traces à 37 et Fort Boyard à 24 exigent d'abord une sélection pertinente ; ne pas prendre arbitrairement les douze premiers. |
| Recherche/preuves TV, actuellement 30 requêtes logiques | **40 au maximum dans cette portée**, réponses identiques mutualisées | Autorise un complément raisonnable ; ne garantit pas tous les pires cas de douze concurrents et leurs alias. Si le groupe fort ne tient toujours pas, abstention. |
| Collecte, actuellement 12 pages | **18 sur un plan prometteur typé**, sans modifier les contrôles de complétude | Cinq refus actuels demandent 18 pages : The One, deux 'Oro, deux Enquête. The One s'est arrêté après deux appels ; les autres après quatre. Passer de 12 à 18 de plafond peut donc coûter jusqu'à **16 ou 14 appels logiques supplémentaires** par dossier, et pas seulement six. Les plans identiques doivent partager les réponses. |
| Marge de confiance | **Priorité de preuve pour l'exact**, ou variante locale 0,04 au lieu de 0,08 | Débloque la mécanique 1,00/0,96 sans relâcher les vrais homonymes de même année. Pas de réduction globale proposée. |

Ces valeurs sont des propositions expérimentales, pas des paramètres modifiés ni des limites déclarées intouchables. Aucun coût provider effectif n'a été engagé pour les évaluer. Les caps plus élevés doivent servir les groupes forts et être compensés par la suppression des vérifications faibles/redondantes, sans augmenter les retries, réactiver RAWG ou lancer des validations en masse.

## Priorité FR et réutilisation des images

La sélection TMDB parcourt les langues avant le vote : avec configuration FR, `fr` précède `en`, puis le neutre. Les votes départagent les affiches de même langue. Fanart suit aussi les priorités configurées. Cette politique doit rester indépendante de l'assouplissement des identités.

Il existe néanmoins deux chemins à surveiller dans une simplification : P4 peut reprendre un poster déjà présent puis le rafraîchissement protégé retourne immédiatement si le fichier existe ; le partage de poster utilise une clé par identité sans vérifier la langue dans son filtre de donneur. **Ces chemins ne prouvent pas qu'un poster FR disponible aura priorité sur un ancien EN.** Ce constat est statique, pas un décompte de mauvaises images.

Proposition : reprendre d'abord les fichiers FR compatibles avec la préférence courante ; ne pas bloquer la réutilisation de l'identité si seule l'image est EN/inconnue. Conserver cette image comme fallback et chercher la meilleure affiche de la même identité dans le mécanisme normal, avec cache, sans rafraîchissement massif. Les affiches manuelles ne sont pas remplacées. Ne pas supprimer une lecture FR sous prétexte qu'un détail ou une image EN du même ID a déjà été reçu.

Trois échecs du dernier CSV concernent déjà l'indisponibilité de poster après choix d'identité. Assouplir le matching ne résout pas ce manque d'image. Le taux d'images FR et le taux d'associations correctes doivent accompagner le taux brut de couverture.

## Comparaison V1 accessible : ce qui est établi et ce qui manque

Les observations visuelles utilisateur — V1 plus couvrant, mais davantage d'associations et affiches EN discutables — restent pertinentes ; elles ne constituent pas des labels par release permettant de calculer une précision.

Les résultats archivés de publication TV fournissent neuf IDs communs avant/après migration : **2 retours `Ok` avant, 8 après**. Six releases sans résultat avant reçoivent après des identités de La Diplomate (trois releases), Mauvais Karma, The Queen Who Crowns et Inspecteur Barnaby. Dark conserve son identité ; Star Academy passe d'un retour réussi sans métadonnées d'identité dans l'artefact avant à une identité TMDB ; C politique reste sans publication. Les six nouvelles publications représentent quatre programmes, pas six œuvres différentes.

Ces données vérifient des comportements avant/après sur un petit panel ; `Ok` n'est pas un label de vrai positif. Les anciens rapports de revue mentionnent des identités correctement proposées et des sous-ensembles sans faux positif observé. Les fichiers `comparison-*.json` disponibles comparent toutefois des assemblages de replay V2/pré-TV, pas un export complet identifié du **V1 de production à 96 %**. Leurs taux de justesse ne sont pas transférables à cette comparaison V1/P5.

Le rapport `matching-metadata-lot4.md` documente aussi un ancien chemin où un score fort laissait accepter une année 1990 pour une requête 2012, ainsi qu'un mélange de métadonnées entre providers. Ce sont de bons motifs pour garder les contradictions fortes, pas pour rétablir les court-circuits V1.

**Aucun taux TP/FP V1 contre P5 ni taux FR/EN comparatif n'est calculable ici sur une cohorte commune certifiée.** Les cas sans label restent inconnus, pas des vrais positifs par défaut. Une comparaison future, après validation, doit distinguer bon ID, mauvais ID, abstention justifiée, identité correcte sans image et langue d'affiche sur les mêmes IDs et les mêmes preuves.

## Impact et trajectoire vers 95–97 %

| Action | Population observée | Impact prouvé maintenant | Gain futur défendable |
| --- | --- | --- | --- |
| Lever les seuls veto numériques de l'exact unique | 418 et 9/11 | Deux veto résiduels précisément isolés | Deux décisions potentiellement acceptables ; justesse et image à confirmer. |
| Édition vidéo et priorité d'année simplifiées | Neuf dossiers à voisinage d'année parmi les onze exacts visibles, dont Oppenheimer | Cause Oppenheimer confirmée ; contre-exemples Bruised/Whistle identifiés | Pas neuf gains garantis. Les preuves originales concurrentes peuvent conserver une ambiguïté. |
| Découpler identité/poster P4 et inconnu/conflit | Trois refus poster et trois refus de claims | Six porteurs de diagnostics ciblés, distincts des 18 donneurs sans identité démontrée | Potentiel conditionnel ; pas six récupérations certifiées. |
| Réduire l'expansion TV, puis adapter les budgets | 15 refus d'enrichissement, dont 14 transitions de motif | Retour possible à l'évaluation, coût actuel excessif démontré | Nombre d'acceptations inconnu. |
| Budget de projection / pages | 10 Survivor + 5 refus à 18 pages | Étapes actuellement inaccessibles | Jusqu'à 15 dossiers évaluables en plus à ces étapes, pas 15 bons posters. |
| Ordinal/suffixe et composition de titres | 7 Duos + Astérix | Les seuls veto de nombres ne suffisent pas à les résoudre | Dépend du titre/événement officiel et du parsing, pas d'une baisse de marge seule. |

Ces populations ne s'additionnent pas en prévision de couverture. Un même dossier peut changer de motif, rester ambigu ou manquer d'image.

Le CSV comprend **49 plans vidéo sans candidats**, **9 couvertures IGDB non prouvées** et **3 échecs d'image** : 61 dossiers ne sont pas résolus par une baisse des scores seule. Parmi les 38 refus de fallback RAWG, 20 ont aussi zéro candidat IGDB dans leur diagnostic. Soit **81/187 (43,3 %)** de ces échecs dont les preuves actuelles ne peuvent pas produire un bon poster par simple assouplissement du ranker. Les autres refus RAWG peuvent en revanche masquer des candidats IGDB dont les protections d'édition méritent examen, sans assimiler DLC/base ou mauvaise plateforme.

Passer d'environ 93 à 95–97 % exige **20 à 40 posters supplémentaires par tranche de 1 000 releases**, sur un dénominateur constant. Si 167 correspondait exactement à 7 % sur un même périmètre, cela représenterait environ 48 à 96 récupérations ; c'est une illustration arithmétique, pas une estimation de la bibliothèque actuelle. Les seuls deux veto numériques et Oppenheimer ne suffiront manifestement pas.

### Ordre de mise en œuvre recommandé, après validation

**Premier ensemble : décision exacte et séparation des portées.** Simplifier P1 dans son emplacement actuel ; édition vidéo non identitaire en avertissement ; numérique contextuel ; alias absent distinct du conflit ; identité P4 distincte de l'image ; conservation de la priorité FR. Vérifier notamment les négatifs Overdose, Gentle Monster, Bruised et Whistle avant d'élargir.

**Deuxième ensemble : coût et budgets ciblés.** Réduire la traîne d'alias obligatoire TV, réutiliser les résultats purement redondants, puis tester les caps 150/12/40/18 proposés. Valider l'effet séparément à chaque étape, car débloquer la projection n'est pas débloquer l'identité.

**Troisième ensemble : langage des titres et segments.** Canonicalisation limitée des ordinaux et composition officielle ; pas de suppression générale des nombres, des suites ou des éditions de jeux.

Le risque résiduel proposé est accepté sur les variantes de sortie et les alias inconnus de concurrents faibles, pas sur les identités contradictoires ou les corrections manuelles. Une petite revue ciblée des nouvelles acceptations, des négatifs connus et des affiches FR suffit à décider de l'élargissement ; il n'est pas nécessaire de refaire un audit exhaustif pour chaque ajustement.

Aucune de ces propositions n'a été implémentée dans P6. Aucun gain de couverture, TP/FP ou quota provider consommé n'est attribué à ce rapport.

La regex d'édition de P1 examine également le titre brut avant d'en retirer le titre officiel reconnu. Sa portée doit être limitée aux informations de release : un mot appartenant au nom officiel ne constitue pas à lui seul une preuve d'édition. Ce point est confirmé par l'ordre du code, sans exemple réel supplémentaire revendiqué ici.

## Repères de code pour la validation

- `Services/Posters/MovieExactYearPriority.cs` : lignes 98–109 (historiques/éditions/voisins), 145 (regex d'édition avant retrait du titre), 217–227 (notes de sorties).
- `Services/Matching/SearchCandidateRanker.cs` : marge ligne 101, ponctuation ligne 357, numérique ligne 362.
- `Services/Matching/OfficialCompoundTitle.cs` : composition exacte et ponctuation des nombres.
- `Services/Matching/TelevisionEvidence.cs` : plausibles et limite lignes 56–59, alias TVmaze ligne 90.
- `Data/Repositories/ReleaseRepository.LocalIdentityReuse.cs` : provenance poster lignes 47–49, snapshot ligne 63 et suivantes, comparaisons de claims lignes 77–85.
- `Services/Posters/PosterFetchService.SharedVideoPoster.cs`, `PosterFetchService.EstablishedIdentity.cs`, `Tmdb/TmdbClient.cs` et `Models/Settings/UiLanguageCatalog.cs` : réutilisation d'images et ordre des langues.
