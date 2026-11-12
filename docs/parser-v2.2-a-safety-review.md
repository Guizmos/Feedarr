# Parser V2.2-A — revue de sécurité avant commit

**Verdict : correctifs nécessaires avant intégration.** Revue des résultats existants, sans modification du code, sans nouvel appel parser/provider et sans ouverture SQLite. Les tests verts précédents ne couvrent pas les régressions identifiées ici.

## Périmètre et méthode

Les 524 différences du corpus de 2 000 releases ont été lues, y compris les 433 lignes avec poster. Comparaison de l'entrée brute et des sorties structurées avant/après, sans utiliser les associations existantes comme preuve. Les doublons de releases comptent séparément : les chiffres ne désignent pas 524 œuvres distinctes.

Le classement porte sur **la modification lexicale**, pas sur l'exactitude intégrale du parsing ni l'identité provider. Une amélioration peut laisser des défauts préexistants (AD, VOSTEN, DOC, etc.). Une régression dans un champ prime sur une amélioration dans un autre. Un retrait sémantique non démontrable reste ambigu. Aucun résultat attendu ou statut de référence initial n'a été inventé/modifié.

| Classe | Toutes releases | Avec poster | Sans poster |
| --- | ---: | ---: | ---: |
| Amélioration confirmée de la différence lexicale | 409 | 330 | 79 |
| Régression confirmée | 18 | 16 | 2 |
| Changement neutre | 0 | 0 | 0 |
| Ambiguïté nécessitant validation | 97 | 87 | 10 |
| Total | 524 | 433 | 91 |

Ces comptes ne constituent pas un taux de précision. Les 18 références lexicales partielles de la phase précédente restent distinctes de ce classement et de toute validation provider.

## Régressions confirmées : 18 releases

La numérotation ci-dessous est l'index 000–523 de la liste privée exhaustive. Les signatures d'exemples publics sont abrégées/anonymisées ; les entrées et champs exacts figurent dans la revue privée.

| Index | Entrée ou fragment responsable | Ancien → nouveau | Conclusion |
| --- | --- | --- | --- |
| 000 | Salvation…[FRENCH.SUBS][Ver.1.2.5878][2026] | titre sans version → titre avec `[Ver 1 2 5878]` | Réintroduction d'une version technique |
| 455 | Moonlighter.2…[UPDATED][V1.0.42.3][2026] | titre sans version → titre avec `[V1 0 42 3]` | Même régression ; le numéro 2 reste présent |
| 008 | Gears.Of.War.E-Day.ISO.WINDOWS.MULTi-RUNE | groupe `RUNE` → `Day.ISO.WINDOWS.MULTi-RUNE` | Tiret du titre interprété comme séparateur de groupe |
| 404 | MSFS.2024.AzurPoly.-.Rafale-C.v1.1.0-NoTag | groupe `NoTag` → `C.v1.1.0-NoTag` | Variante du modèle et version absorbées dans le groupe |
| 189, 215, 313 | …x264/x265.E-AC-3.5.1-TEAM | groupe terminal → `AC-3.5.1-TEAM` | Audio absorbé dans le groupe |
| 513 | Stargate.Universe…AV1.AC-3-TEAM | groupe absent → `3-TEAM` | Tiret interne audio sélectionné |
| 211, 467 | …ProdKeys.-v22.5.0.Switch-TEAM | groupe terminal → `v22.5.0.Switch-TEAM` | Version/plateforme absorbées dans le groupe |
| 165 | …THE.JUNGLE-BOOK.FRENCH | groupe absent → `BOOK.FRENCH` | Fragment de titre alternatif inventé comme groupe |
| 216 | …THE-CROODS.FRENCH | groupe absent → `CROODS.FRENCH` | Même défaut |
| 291 | …A.NEW-AGE.FRENCH | groupe absent → `AGE.FRENCH` | Même défaut |
| 481 | …GED-SENKi.FRENCH | groupe absent → `SENKi.FRENCH` | Même défaut |
| 519 | …THE-GUARDiANS.FRENCH | groupe absent → `GUARDiANS.FRENCH` | Même défaut |
| 522 | …LES.12.TRAVAUX-DASTERiX | groupe absent → `DASTERiX` | Même défaut |
| 448 | [scoot] Night on the Galactic Railroad [BD 1080p HEVC Opus]… | titre et bloc complet → `[scoot] Night on the Galactic Railroad [BD` | Bloc technique tronqué, crochet ouvert résiduel |
| 473 | Call.of.Duty.4.Modern.Warfare.Remastered.FRENCH… | `Call of Duty 4 Modern Warfare Remastered FRENCH MULTI5 x86 64-TEAM` → `Call of Duty 4 Modern Warfare` | Désignateur d'édition Remastered supprimé ; numéro 4 préservé |

Gravité : élevée pour les métadonnées d'édition supprimées et la généralisation abusive des groupes ; moyenne pour les versions réintroduites et les blocs mal fermés. Aucun changement d'identité persistée n'est démontré ou effectué par ce benchmark.

### Causes directes

- `ExtractSequenceGroup` demande seulement un token technique quelque part avant le tiret. Il ne rattache pas le séparateur à une fin de séquence technique vérifiée. En jeux, des mots reconnus dans le titre peuvent aussi satisfaire ce contexte. Le premier tiret syntaxiquement admissible capture donc des morceaux de titre, d'audio ou de version.
- Le vocabulaire vidéo ne couvre pas atomiquement les graphies `E-AC-3`/`AC-3` dans ce chemin ; exclure les tirets *à l'intérieur des tokens reconnus* ne suffit pas lorsqu'ils ne sont pas reconnus comme un seul token.
- Les versions entre crochets après un bloc de texte non reconnu échouent à établir la frontière de jeu ; le nettoyage final ne reproduit plus le retrait antérieur de la version.
- La frontière vidéo peut commencer au milieu d'un bloc technique `[BD …]` sans remonter à son ouverture.
- Le marqueur court d'édition de jeu `Remastered` devient une frontière de métadonnées ; sa reconnaissance ne prouve pas qu'il soit dispensable dans le nom de l'édition.

## Les huit années ajoutées aux émissions

Toutes passent de `null` à une valeur présente dans le nom brut. Aucune ne constitue une année provider validée.

| Index | Brut, partie pertinente | Ancien → nouveau | Évaluation |
| --- | --- | --- | --- |
| 223 | Marrakech.Comedy.Festival.2026.FRENCH.AD.1080p.DSNP.WEB… | titre avec 2026 et tags, année null → Marrakech Comedy Festival, année 2026 | Édition annuelle plausible ; sens de Year à valider |
| 362 | NRJ.Music.Awards.2026.FRENCH.TF1P.1080p.WEB… | NRJ Music Awards 2026 FRENCH TF1P, année null → NRJ Music Awards, année 2026 | Le numéro d'édition annuel quitte le titre ; risque d'identification à valider |
| 227 | Les.Apprentis.Aventuriers.S09E34.2026… | même titre, année null → 2026 | Année de diffusion plausible, pas forcément année d'origine de la série |
| 414 | Les.Apprentis.Aventuriers.S09E33.2026… | même titre, année null → 2026 | Même ambiguïté |
| 324 | Les.Apprentis.Aventuriers.S09E44.2026… | même titre, année null → 2026 ; groupe absent → JTBC | Même ambiguïté ; groupe terminal syntaxiquement plausible |
| 399 | Les.Apprentis.Aventuriers.S09E42.2026… | même titre, année null → 2026 ; groupe absent → JTBC | Même ambiguïté |
| 452 | Les.Apprentis.Aventuriers.S09E40.2026… | même titre, année null → 2026 ; groupe absent → JTBC | Même ambiguïté |
| 351 | Monstres.Ordinaires.DOC.S01.VOF.2019.1080p.Web… | même titre, année null → 2019 ; groupe absent → JTBC | Année de saison/documentaire, identité de programme non validée |

Les saisons/épisodes/dates restent strictement identiques. Conclusion : huit ambiguïtés, pas huit améliorations d'identité confirmées. Ne pas modifier les règles métier Émission dans un correctif de suffixes sans décision explicite.

## Les cinq groupes DTS/AC3 retirés

| Index | Brut, fin pertinente | Ancien groupe → nouveau | Conclusion |
| --- | --- | --- | --- |
| 020 | Les.Boxtrolls…X264-DTS.FRENCH | DTS.FRENCH → null | Ambigu : audio ou signature |
| 190 | Les.Aventures.De.Winnie.Lourson…MPEG2-AC3.FRENCH | AC3.FRENCH → null | Audio plausible, mais pas démontré comme seule interprétation |
| 195 | Le.Livre.De.La.Jungle…MPEG2.AC3-DTS.FRENCH | DTS.FRENCH → null | Ambigu |
| 201 | Les.Boxtrolls…FULL.BLURAY.AVC-DTS | DTS → null | Ambigu ; un retrait technique partiel du titre accompagne ce changement |
| 471 | Les.Aventures.De.Tintin.Le.Secret.De.La.Licorne…X264-DTS.FRENCH | DTS.FRENCH → null | Ambigu |

Le placement après tiret peut indiquer un groupe autant que le vocabulaire indique un codec. Le rejet lexical seul n'est pas une preuve suffisante. Conserver ces cinq cas en revue ; ne pas annoncer leur correction comme acquise.

## Liste exacte des 97 ambiguïtés

La liste privée donne pour chaque ligne le titre brut, tous les champs avant/après et sa conclusion. Répartition exclusive :

- 78 retraits de titres alternatifs ou de contexte non technique après la frontière : 013, 027, 030, 038, 045, 050, 052, 061, 062, 069, 072, 079, 086, 098, 105, 107, 110, 130, 135, 137, 141, 155, 159, 163, 207, 217, 230, 234, 245, 250, 253, 254, 262, 265, 278, 283, 286, 293, 296, 299, 301, 309, 317, 318, 322, 333, 336, 346, 348, 357, 358, 363, 364, 366, 378, 385, 387, 390, 411, 412, 415, 418, 420, 424, 425, 431, 440, 442, 443, 457, 460, 474, 478, 487, 506, 510, 517, 521.
- Les cinq groupes audio : 020, 190, 195, 201, 471.
- Les huit années d'émission : 223, 227, 324, 351, 362, 399, 414, 452.
- Six autres cas : 041 (plusieurs années, collection), 153 (six courts métrages), 263 (paquet émulateur contenant un nom de jeu supprimé), 315 (groupe `Pis.mkv` extrait de `Manneken-Pis`), 494 (collection de contes/année d'édition), 514 (groupe devenu une chaîne distributeur/repack/signature).

Exemples de texte libre : `The.Killing…(Forbrydelsen)` devient `The Killing` ; `Schrotten…(Les Ferrailleurs)` devient `Schrotten`. Le préfixe principal reste présent, mais un alias utile à la recherche disparaît : avantage ou perte non démontrés sans validation d'identité. Pour `Léo.Delibes.Lakmé.2011.720p.MKV.flac.2.0.(Sydney.Opéra)` et `Chostakovitch.Lady.Macbeth.of.Mtsensk.2002.480p.h264.MP3.2.0.(0pera.Barcelone)`, le nouveau titre conserve l'œuvre, retire la salle et extrait l'année. Ces détails peuvent identifier une représentation ; aucune autorisation sémantique de les perdre n'est déduite des seuls codecs.

## Vérification des garanties

- **Aucun mot légitime supprimé : garantie non satisfaite.** Remastered est perdu ; alias et salles restent ambigus.
- Aucun titre vide dans les 2 000 sorties. 358 des 524 titres sont réduits d'au moins moitié (ratio de longueur ≤ 0,5) : examinés dans la revue, beaucoup retirent essentiellement les tags. Les titres courts `Qui`, `Rio`, `Soul`, `Flow`, `Luck`, `Badh`, `Goat`, `Pode` conservent le préfixe brut ; la longueur seule ne prouve pas une troncature. Le bloc `[BD` résiduel est en revanche anormal.
- Aucun numéro d'œuvre clairement perdu confirmé dans ces différences ; suites, volumes, 007, 8020 et numéros de jeux examinés. Les années d'éditions, collections et paquets mixtes restent explicitement ambigus. Cela ne constitue pas une garantie universelle hors corpus.
- Aucune année déjà renseignée perdue ou remplacée sur les 2 000 lignes ; 345 ajouts depuis null.
- **Aucun groupe fantaisiste : garantie non satisfaite.** Les exemples ci-dessus sont reproductibles dans les résultats figés.
- Aucun changement de Season, Episode, Codec, Resolution, AirDate ou MediaType sur les 2 000 sorties. Le corpus et les catégories d'entrée sont identiques. Aucun provider n'est appelé ; aucune identité n'est écrite. La future qualité des associations ne peut pas être validée sans références provider.
- Les 174 entrées historiques sont conservées, mais **pas leurs 174 attentes intégralement inchangées** : une cellule ReleaseGroup a déjà été changée de Raws.CR à Tsundere-Raws.CR. Les 173 autres instantanés sont inchangés. Les 432 tests et la Release précédents sont verts ; aucune suite relancée pour masquer ces anomalies de corpus.

## Correctifs minimaux recommandés — non appliqués

1. Rattacher le groupe à la frontière et aux tokens précédents immédiats, pas à un token quelconque du titre. Exclure les suffixes encore traversés par des marqueurs de version/plateforme/audio et les alias libres. Reconnaître atomiquement les graphies audio composées ; conserver les véritables signatures composées.
2. Traiter les blocs version/build et techniques comme unités structurées, avec ouverture/fermeture ; restaurer le retrait des versions après les blocs de langue sans couper les blocs au milieu.
3. Préserver les désignateurs d'édition de jeux dans le préfixe œuvre ; un mot comme Remastered ne doit pas suffire seul à créer une frontière destructive.
4. Garder les alias/salles et les années d'émission sous statut ambigu ; décider explicitement leur représentation plutôt que d'étendre une règle de suppression libre.
5. Convertir les 18 régressions en tests ciblés, puis rejouer exactement le même corpus avant une nouvelle revue. Les règles doivent être portées par la séquence structurée, sans exceptions par œuvre.

## Traçabilité et sécurité

Détails privés, ignorés par Git : `tools/parser-benchmark/local/v22a-safety-review/classification.json`, `revue-exhaustive.md`, `priorites.md`. Ils contiennent les 524 décisions et les 13 vérifications prioritaires avec entrées exactes et sorties complètes. Ne pas les committer.

Le corpus canonique reste `04293b3ed1490b4bb0f62926d7e2570abe9f0cfe9c77a9788e982b61621f7538` ; le candidat analysé reste `b67405857509a71539c2b4815a51e5c09c23fc5e3e3403c16813af6b1620e74d`. SHA-256 de la base inchangé : `23a3d0a355958f2eec8ed562c524282c5b5259a5ff70060991e23bedda7b159d`.

Cette revue ajoute uniquement ses documents et artefacts privés. Aucun correctif, changement de référence, commit, push, accès NAS/Docker/provider ou écriture SQLite.
