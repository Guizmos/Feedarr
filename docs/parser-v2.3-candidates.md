# V2.3 — premier générateur de candidats, isolé

V2.2-B livré sur `beta` : `30a260b5856010ac71ee5d039a64395c561084dd`.

Le composant interne `SearchCandidateGenerator` n'est ni enregistré dans la DI ni appelé par le matching. Aucun parser, endpoint, contrat API, schéma ou comportement de production n'est modifié.

## Contrat

- Entrée immuable : titre brut, catégorie et états fiables d'identité établie/correction manuelle fournis par le futur appelant.
- Identité établie ou correction manuelle : aucun candidat ; les IDs eux-mêmes ne sont jamais des matériaux de requête.
- Au maximum deux candidats, collection en lecture seule, résultat principal du parser toujours premier.
- Année `Parsed` : information lexicale du parser, **pas** confirmation provider. Année `Hypothesis` : proposition alternative exclusivement ; `None` : année inconnue.
- Première variante limitée aux films : nombre final 1900–2099 conservé par le parser, année inconnue et résolution reconnue sans source/codec. Le titre alternatif doit conserver du texte alphabétique. Les jeux, séries et autres catégories conservent uniquement leur candidat principal.
- Aucun dictionnaire par œuvre, normalisation agressive ou duplication du vocabulaire technique ; le nettoyage réutilise le parser existant.

Exemples : `Blade.Runner.2049.1080p` donne `Blade Runner 2049` sans année, puis `Blade Runner` avec l'hypothèse 2049. Avec une seconde année 2017, le principal garde 2049 dans son titre et 2017 comme année parsée. `Cyberpunk.2077` conserve son nombre ; les éditions et suffixes techniques suivent le parser V2.2-B.

## Validation

26 tests ciblés réussis, compilation incluse : exemples demandés, nettoyage technique, éditions, nombres, catégories, identités établies/corrections manuelles, entrées vides, borne, ordre, déterminisme/culture et collection non modifiable.

Commande :

```sh
dotnet test src/Feedarr.Api.Tests/Feedarr.Api.Tests.csproj --no-restore --filter 'FullyQualifiedName~SearchCandidateGeneratorTests'
```

Aucune suite backend complète relancée pour V2.3 ; aucun provider, NAS, production, accès SQLite ou reparse. Les fichiers étrangers et artefacts privés restent préservés.

## À poursuivre

Avant toute intégration au pipeline : adapter les états persistés d'identité/révision/verrou manuel, revoir les hypothèses sur les références réelles, puis définir classement multi-candidats et critères de rejet sans faire passer une hypothèse pour une preuve. Les variantes pour autres catégories restent hors de ce premier composant. Aucun commit/push V2.3 automatique.
