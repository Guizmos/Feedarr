# P0 — régressions du parser après le lot 5

Base : `beta` à `b69ba9d`. Régression auditée : `2c77b26`, parent `32bd510`.
L’audit et les 174 entrées conservées sous `/tmp/feedarr-parser-audit/` ont été
réutilisés, sans nouvel audit général ni accès aux données de production.

## Causes et corrections

- Les frontières vidéo omettaient des tags audio/codec pourtant détectés ailleurs.
  Le nettoyage et l’année utilisent maintenant la même reconnaissance des sources,
  codecs et compléments techniques (DTS, canaux audio, bit depth, HDR, etc.). Une
  frontière exige une queue technique valide : un mot au milieu d’un titre naturel
  ne suffit pas à tronquer ce titre. Les variantes Blu-ray et H.264/H.265 sont aussi
  reconnues dans les champs structurés.
- Les marqueurs Update, Hotfix, Repack, Bonus, OST et Early Access des jeux étaient
  reconnus dans la validation d’une queue, mais pas tous comme frontières. Le même
  ensemble sert désormais aux deux opérations. Une queue contenant encore du texte
  naturel n’est pas tronquée arbitrairement. Un numéro devant OST/Bonus/DLC est
  conservé lorsqu’il peut appartenir au titre.
- Un tag de langue ambigu en majuscules peut être retiré en suffixe lorsqu’une
  source ou un codec le confirme. Les mots internes et les noms comme Johnny English
  restent protégés. La résolution seule ne suffit pas à effacer un mot ambigu.
- Le label `[Fansub]` est reconnu en tête d’anime avec un contexte de release ;
  les crochets arbitraires comme `[REC]` ne sont pas supprimés globalement.
- Une année placée après un marqueur de saison/épisode est exploitable lorsque
  son environnement est technique. Les dates complètes et composants de versions
  sont exclus de cette interprétation.

## Exemples structurés avant/après

| Entrée | Avant P0 | Après P0 |
| --- | --- | --- |
| Argo.2012.DTS.1080p.BluRay-GRP | Argo 2012 DTS ; année NULL | Argo ; 2012 ; groupe GRP |
| Argo.2012.AVC-GRP | Argo 2012 AVC-GRP ; année/groupe NULL | Argo ; 2012 ; groupe GRP ; codec x264 |
| Argo.FRENCH.1080p.WEB-DL.x264-GRP | Argo FRENCH | Argo ; année NULL ; WEB-DL/x264/1080p conservés |
| Portal.2.Update.v1.2.3-CODEX | Portal 2 Update | Portal 2 ; année NULL ; groupe CODEX |
| [Fansub].Show.S01E02.1080p | [Fansub] Show | Show ; saison 1 ; épisode 2 ; 1080p |
| The.Office.S01E02.2005.1080p | The Office ; année NULL | The Office ; 2005 ; saison 1 ; épisode 2 |
| Blade.Runner.2049.1080p | Blade Runner ; année 2049 | Blade Runner 2049 ; année NULL |
| Blade.Runner.2049.2017.1080p | Blade Runner 2049 ; 2017 | Identique |

Les catégories restent fournies par les appelants et leur correspondance MediaType
est inchangée. Les tests comparent titre, année, saison, épisode, AirDate, résolution,
source, codec, groupe et MediaType, pas seulement l’apparence du titre.

## Politique d’ambiguïté confirmée par l’utilisateur

Un seul nombre de quatre chiffres suivi uniquement d’une résolution reste dans le
titre, avec Year=NULL. Un groupe ou une extension ne rend pas cette année certaine.
Cela vaut aussi bien pour `Film.2020.1080p` que pour `Blade.Runner.2049.1080p`.
Quatre attentes anciennes (Johnny English, Spanish Prisoner, Final Cut et
Saint-Germain) ont été explicitement adaptées à cette politique, sans modifier les
entrées. Une date délimitée, une seconde année ou des indices de release plus probants
restent exploitables. Les années futures explicitement marquées restent reconnues.

`The.Office.2005.S01E02` conserve 2005 dans le titre et Year=NULL, comme
`Space.1999.S01E01` conserve 1999. Sans indice indépendant, distinguer sûrement une
année d’un numéro de titre nécessiterait une connaissance de l’œuvre. Aucun nom
d’œuvre n’est codé en dur. `Show.COMPLETE.1080p`, les langues isolées et les versions
nues courtes sans contexte suffisant restent conservatrices plutôt que tronquées.

## Pipeline posters : défaut direct démontré

Le repository enregistre les sorties du parser. Les deux chemins de
PosterFetchJobFactory utilisent TitleClean/Year. PosterFetchService lit aussi
TitleClean, puis normalise ponctuation et accents avant les requêtes vidéo.

Une seconde suppression dans QuerySanitizeGame retirait encore tous les nombres
19xx/20xx et des mots tels que Linux ou Update. Elle annulait les protections du
parser avant les requêtes IGDB/RAWG. Cette étape se limite maintenant à normaliser
ponctuation et espaces : `Cyberpunk 2077`, `F1 2026` et les vrais mots sont transmis.
Les tests parcourent ingestion, SQLite temporaire, jobs et handlers HTTP simulés,
et vérifient les requêtes TMDB, IGDB et RAWG effectivement émises. Aucun appel réseau
réel n’est nécessaire. Les politiques de sélection, scores, verrous manuels et
révisions des lots 3/4 ne changent pas.

## Corpus et périmètre

Le corpus de 174 entrées est versionné dans
`src/Feedarr.Api.Tests/Golden/parser-lot5-p0.json`, avec ses sorties attendues revues.
Comparé au lot 5 : 122 sorties identiques, 34 corrections de parsing, 10 mises à jour
de champs techniques et 8 changements conservateurs d’année approuvés. Les doublons
d’entrées de l’audit gardent un index distinct pour être tous exécutés par xUnit.

Aucune migration, reprise historique, synchronisation, purge ou téléchargement global.
Aucun changement Backup V2, wizard, GC ou frontend. Aucun accès NAS. Les données,
corrections manuelles et posters existants ne sont pas modifiés par cette livraison.
Le correctif s’applique aux prochains traitements normaux ; les anciennes valeurs
stockées ne sont pas réparées automatiquement.

## Validation finale

- 455 tests ciblés réussis, dont les 174 entrées du corpus et les requêtes providers.
- Suite backend complète exécutée une seule fois sur le correctif final : 1612
  réussites, zéro échec, zéro ignoré. TRX dans `/tmp/feedarr-parser-audit/p0-validation/`.
- Compilation Release finale : zéro avertissement, zéro erreur.
- `git diff --check` propre ; origin/beta alignée sur la base avant commit.
- Aucun test frontend, puisque le frontend n’est pas modifié.
- Les fichiers étrangers `.artifacts/` et `feedarr-poster-storage-audit.py` sont préservés.

Le premier lancement prévu de la suite complète n’avait pas été exécuté : la revue
automatique d’approbation avait échoué pour limite d’usage. L’exécution après reprise
ci-dessus est donc l’unique passage effectif de cette suite pour ce correctif.
