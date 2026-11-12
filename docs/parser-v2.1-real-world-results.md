# Parser V2.1 — résultats sur la sauvegarde SQLite réelle

Analyse locale après `294eacb`, parser actuel de `074986e`. Aucun changement de
règle, migration, écriture SQLite, synchronisation, téléchargement, appel provider,
accès NAS/Docker/API de production, commit ou push dans cette étape.

## 1. Copie, sécurité et reproductibilité

Entrée privée : `tools/parser-benchmark/local/feedarr-parser-benchmark.db`, sauvegarde
cohérente autonome fournie par l’utilisateur, 145 424 384 octets. `local/` est bien
ignoré par Git. Aucun WAL/SHM/journal associé présent. Le SHA-256 du fichier reste
identique avant/après ; ses permissions ne sont pas changées.

Le garde-fou de l’exporteur initial refusait ce dossier/fichier local accessible en
écriture. Seul correctif d’outillage : `--local-snapshot`, réservé à une sauvegarde
autonome sous `local/`, refusant les sidecars, avec `mode=ro&immutable=1`, query_only,
tris en mémoire et authorizer limité aux colonnes utiles. Ce mode ne doit jamais
être utilisé sur une base active. Le mode NAS par défaut reste inchangé.

Le schéma réel contient les champs attendus releases/media_entities ; aucune colonne
facultative de la projection ne manque. Les paramètres/intégrations, credentials,
URLs et textes bruts d’erreurs ne sont pas exportés. Les sources sont pseudonymisées.
Les 32 exclusions concernent 11 entrées contenant un nom de source et 21 déclenchant
le filtre sensible/invalide ; leurs contenus ne figurent pas dans ce rapport.

Graine : `feedarr-parser-v2.1`. Export et import existants réutilisés. Deux exports
produisent le même corpus ; deux exécutions actuelles donnent des résultats JSON
et rapports Markdown identiques octet pour octet. Les durées sont séparées.

Corpus SHA-256 :
`04293b3ed1490b4bb0f62926d7e2570abe9f0cfe9c77a9788e982b61621f7538`.
Sources parser/catégories SHA-256 :
`cbe4fa55015cfa39e48befdd68574f17cce043001be34c30b554502914a0a79c`.

## 2. Population et corpus réellement analysé

La copie contient **4 904 releases** : **344 sans référence de poster** et **4 560
avec référence**. Après filtrage, 4 872 candidates sont admissibles, dont 343 sans
poster. Toutes ces 343 sont retenues ; le contrôle complète à **2 000 releases**.
La cible 70 % sans poster est impossible faute de cas disponibles : le corpus
contient **17,15 % sans poster**, contre 7,01 % dans la population brute.

| Catégorie stockée | Sans poster | Avec poster | Total |
| --- | ---: | ---: | ---: |
| Animation | 28 | 273 | 301 |
| Emission | 31 | 175 | 206 |
| Film | 72 | 417 | 489 |
| JeuWindows | 76 | 257 | 333 |
| Serie | 107 | 435 | 542 |
| Spectacle | 29 | 100 | 129 |
| Anime | 0 | 0 | 0 |
| **Total** | **343** | **1 657** | **2 000** |

Sept sources sont représentées, sans publier leurs noms. Ancienneté de publication :
1 869 récentes et 131 anciennes, selon le maximum de publication moins 180 jours.
Ce n’est pas l’année de sortie de l’œuvre. Les 2 000 lignes correspondent à 1 877
titres bruts distincts : les répétitions inter-sources/versions ne sont pas des
œuvres indépendantes. Les catégories viennent de la base, pas d’une vérité terrain.
Aucune ligne n’est catégorisée Anime dans cette copie ; des titres Animation
portent pourtant des marqueurs d’épisodes, à examiner sans reclassification automatique.

« Avec poster » signifie référence SQL non vide avec COALESCE release/entity,
comme l’outil existant. Les fichiers physiques ne sont pas consultés. Ni leur
existence ni la justesse de leur association ne sont ainsi certifiées.

## 3. Signaux descriptifs : pas un décompte de mauvaises associations

Recherche locale dans TitleClean, sans modifier les règles du parser. Familles
chevauchantes : leurs comptes ne s’additionnent pas. Le groupe de contrôle est
volontairement stratifié, pas un estimateur pondéré de la population entière.

| Signal automatique | Sans poster / 343 | Avec poster / 1 657 | Total | Interprétation |
| --- | ---: | ---: | ---: | --- |
| Langue conservée (MULTI, VOSTFR, FRENCH, VFF…) | 106 | 343 | 449 | Certaines occurrences peuvent appartenir au titre |
| Format/codec/audio conservé (1080p, WEB-DL, BluRay, x264, AAC, DTS…) | 22 | 330 | 352 | Résidu confirmé sur les exemples revus ; autres lignes à valider |
| Suffixe jeu/version/plateforme dans TitleClean | 24 | 49 | 73 | Restreint à JeuWindows ; DLC/OST peuvent faire partie du produit |
| Mot de collection/pack conservé | 8 | 64 | 72 | Pas toujours du bruit : titre légitime ou lot multi-œuvres possible |
| Marqueur Sxx/SxxExx conservé dans TitleClean | 0 | 12 | 12 | Catégorie et contrat d’épisode à vérifier |
| Year=NULL | 286 | 1 151 | 1 437 | Souvent normal pour épisodes/jeux et cas ambigus |
| TitleClean recalculé différent du stockage | 129 | 602 | 731 | Le stockage n’est pas une référence validée |

Le signal format/codec/audio touche 6,41 % des sans-poster et 19,92 % des contrôles.
Il ne suffit donc pas à expliquer l’absence de poster. Il concerne 352 lignes mais
311 titres bruts distincts : 22 sans poster et 289 avec poster.

Sous-familles observées dans TitleClean : SD 44 (35 sans/9 avec), AD 57 (15/42),
HDR10Plus 20 (7/13), PCM 5 (2/3), codes de service AMZN/DSNP/M6P/TF1P/ADN/CR/ATV
27 (3/24), PREACTIVED 10 (6/4). Ces indicateurs ne prouvent pas à eux seuls une erreur.

## 4. Défauts reproduits et propositions à valider

Les signatures de release sont masquées ci-dessous. Les calculs privés utilisent
les chaînes exactes. **Confirmé** signifie comportement technique reproduit ; cela
ne valide ni l’œuvre attendue ni son ID provider. Les attentes restent proposées.

| Cas brut expurgé | Parsing actuel | Proposition / statut |
| --- | --- | --- |
| Miyazaki.Ponyo.Sur.La.Falaise.2008.FRENCH.BRRip.x264.AC3-[signature.composée] | Le titre conserve 2008 FRENCH BRRip x264 AC3 et la signature ; Year=NULL | Titre sans suffixe technique, année candidate 2008. **Résidu confirmé**, référence complète à valider |
| Bodycam.v0.8.12.19346.WIN.X64.MULTI.PORTABLE.PREACTIVED-[signature] | Bodycam v0 8 12 19346 WIN X64 MULTI PORTABLE PREACTIVED-[signature] ; Year=NULL | Bodycam, Year=NULL. **Résidu de version/plateforme confirmé**, identité non validée |
| Casino.1995.VFF.SD.BDRip.AAC.2.0.x264-[signature] | Casino 1995 VFF SD ; Year=NULL | Casino ; année candidate 1995. **SD/VFF résiduels confirmés**, défaut déjà présent au lot 5 |
| NRJ.Music.Awards.2026.FRENCH.TF1P.1080p.WEB.H264-[signature].mkv | NRJ Music Awards 2026 FRENCH TF1P ; Year=NULL | Retirer les tags FRENCH/TF1P ; conserver provisoirement 2026. Année/édition **ambiguë** |
| South.Park.S29E03.FASTSUB.VOSTFR.1080p.WEB.EAC3.5.1.H264.[signature] | Catégorie Animation, MediaType=movie ; S29E03 et tags dans le titre ; Season/Episode=NULL | Titre South Park et S29/E03 proposés. **Suspect de catégorie/contrat**, pas une identité validée |
| L.Amour.Est.Dans.Le.Pre.S21E15.02.11.2026… | Titre propre ; AirDate=2026-11-02, Year=2026 ; Season/Episode=NULL | Conserver aussi S21/E15 ? **Décision métier à valider** : la date est actuellement prioritaire |
| James.Bond.collection.complete.1962.2021.FRENCH.720p.HDTV… | James Bond collection complete 1962 ; Year=2021 | Lot multi-œuvres, pas une identité de film certaine. **Ambiguïté structurelle**, ne pas choisir un TMDB arbitrairement |

### Régressions identifiées par comparaison de code

Les mêmes entrées ont été exécutées avec les sources de `2c77b26`, lues dans un
dossier temporaire sans checkout. 876 sorties structurées changent : TitleClean
487 fois, Codec 436, Year 272, ReleaseGroup 32, Source 1. Ce ne sont **pas** 876 erreurs.
Les références réelles restant non validées, le comparateur ne calcule aucun score
de régression annotée ; cette absence de score ne signifie pas absence de défaut.

Les 352 titres portant le signal format/codec/audio n’avaient pas ce signal avec le
lot 5. Deux autres perdent ce signal avec le correctif actuel. L’examen de Ponyo et
Bodycam confirme des régressions de nettoyage introduites par `074986e`, absentes
du corpus de 174 cas : l’exigence d’une queue intégralement reconnue dans
FindVideoTechnicalStart/FindGameMetadataStart refuse une frontière valide quand un
élément suivant est inconnu ou quand une signature composée ne correspond pas au
format de validation. Une année peut ensuite devenir NULL par effet secondaire.

La validation vidéo/groupe est notamment incohérente pour les signatures composées
à points, et les variantes HDR10Plus, SD, AD ou PREACTIVED exposent des limites de
vocabulaire/contexte. **Aucun retour arrière global n’est proposé** : les protections
des mots légitimes et des nombres doivent rester vérifiées.

### Saisons, groupes et numéros

86 chaînes contiennent SxxExx sans les deux valeurs correspondantes en sortie :
81 Emission ont aussi une AirDate et suivent la priorité de date existante ; les
5 autres sont 4 Animation et 1 Film, catégories non épisodiques pour ce chemin.
Ce décompte n’est donc pas assimilé à 86 bugs de regex ou à 86 mauvaises catégories.
Des groupes composés après codec ne renvoient que leur dernier segment, conformément
au LastIndexOf('-') actuel ; le groupe attendu complet reste à annoter.

Aucune suppression systématique de numéros légitimes ou troncature de titre n’est
confirmée exhaustivement. Directive 8020, par exemple, garde son numéro mais conserve
un suffixe de version. Les titres courts ne sont pas classés erronés automatiquement.

## 5. Parsing et matching persisté : ne pas confondre

Parmi les 343 sans poster, 340 portent une erreur classée no_match et 3 une autre
erreur ; 2 possèdent néanmoins un ID exporté. Aucun message brut n’est publié.
Parmi les contrôles, 45 n’ont pas d’ID numérique exporté ; les IDs opaques de certains
providers sont omis par l’exporteur. Cela ne prouve pas un poster incorrect.
1 376 contrôles portent un ID TMDB sans type exporté : signal historique à préserver,
pas motif de réassociation globale. Dix-neuf contrôles ont une erreur no_match
stockée malgré leur référence de poster : ces erreurs peuvent être anciennes.

1 159 lignes diffèrent du stockage sur au moins un champ : 203 sans poster et 956
contrôles. Les différences comprennent 608 années et 438 codecs. Aucun titre manuel
ou matching manuel n’est signalé par les indicateurs exportés sur ce corpus.
Les champs stockés, versions passées et enrichissements ne sont pas une vérité terrain.
Le pipeline réel utilise les champs stockés : une sortie recalculée mauvaise peut
coexister avec un poster obtenu auparavant à partir d’un autre TitleClean. Le
benchmark ne répare ni ne remplace ces champs.

## 6. Performance locale

.NET 8.0.33, Arm64, mono-thread, 2 passages de warmup et 5 répétitions par entrée,
soit 10 000 appels chronométrés par exécution. Temps hors compilation/warmup/SQLite/
providers. Pas d’extrapolation directe à un Synology ou à une recherche complète.

| Mesure | Actuel A | Actuel B | Lot 5, un passage |
| --- | ---: | ---: | ---: |
| Médiane / appel | 92,7 µs | 96,7 µs | 14,0 µs |
| p95 / appel | 218,5 µs | 222,3 µs | 26,5 µs |
| Somme des durées pour 10 000 appels | 1 055,34 ms | 1 095,42 ms | 155,09 ms |

Le coût actuel est nettement supérieur sur ce corpus ; les validations de suffixes
sont une cible de profilage. Ces passages ne constituent pas une étude statistique
contrôlant toutes les variations JIT/GC/charge. Les sorties actuelles sont identiques
entre les deux passages ; les temps, naturellement, ne le sont pas.

## 7. Références et précision

**Zéro référence réelle validée ; aucune précision annoncée.** Une sélection privée
de 120 cas est prête : 72 sans poster, 48 contrôles ; 100 récents, 20 anciens ; six
catégories et sept sources. Elle inclut des reproductions et des contrôles, avec
propositions séparées des références. Toutes ses annotations restent unreviewed,
avec parserExpected/associationExpected vides. Cette sélection de diagnostic ne
permettra pas sans pondération d’estimer la précision globale de production.

Les fichiers locaux real-review-selection.json et real-review-references.json sont
liés au SHA-256 du corpus et ignorés par Git. La présence d’un poster, un ancien
TitleClean ou un résultat de l’ancien parser ne valide jamais automatiquement une attente.

## 8. Priorités Parser V2.2 et livrables

1. Valider manuellement le sous-ensemble, séparément pour parsing et identité ;
   transformer les reproductions confirmées en contre-exemples permanents après accord.
2. Assouplir de façon contextuelle la validation des queues inconnues/composées et
   aligner traitement du groupe, nettoyage et année, sans supprimer tous les mots/tags.
3. Traiter les variantes rencontrées SD/AD/HDR10Plus/PCM/services et PREACTIVED/paquets
   jeux sans ignorer le texte naturel ni les numéros d’édition.
4. Décider du contrat Emission date+saison/épisode, puis revoir Animation/Anime et
   les paquets multi-œuvres sans reclassification ou rematching massif.
5. Profiler les validations répétées, avec le corpus réel et les 174 cas comme garde-fous.

Artefacts privés sous `tools/parser-benchmark/local/` : real-corpus.json, real-run-a/
et real-run-b/ (JSON/Markdown/performance), real-lot5-vs-current/, real-suspects.json,
sélection et références de revue. Exports et résultats ne sont pas destinés à Git.

Validation : 22 tests ciblés des outils verts ; benchmark Release et comparaison
déterministe réussis ; base inchangée, aucun sidecar créé ; aucun changement dans
src/. Aucun correctif métier, aucune suite backend complète, aucun commit/push.
Parser V2.2 reste en attente de validation.
