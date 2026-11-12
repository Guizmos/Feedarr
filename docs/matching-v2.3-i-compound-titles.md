# V2.3-I — Titres composés, uniquement en Shadow

Base locale : `beta`, `7ef2175`. Aucun appel provider, accès NAS, commit ou push.
Les quatre identités proposées sont des références Beta fournies par l’utilisateur,
pas des identités indépendamment validées. Les réponses provider des fixtures sont
explicitement simulées ; aucun export des dernières observations Beta n’est disponible
localement pour confirmer les textes réellement reçus.

## Diagnostic et règle ciblée

V2 comparait la requête à chaque titre officiel séparément. Une requête contenant
l’intégralité du titre localisé **et** du titre original pouvait donc être rejetée,
bien que les deux noms figurent déjà dans les réponses reçues pour la même identité.

`OfficialCompoundTitle` exige maintenant :

- provider TMDB et identité **provider/type/ID** identiques pour les deux preuves ;
- un titre original provenant de `SearchOriginalTitle` et un titre localisé provenant
  de `SearchTitle`, avec locale explicitement disponible ;
- couverture exacte, normalisée, de toute la requête par ces deux titres complets,
  dans l’un ou l’autre ordre, sans aucun mot ajouté, retiré ou réordonné ;
- noms distincts, sans containment : un nom déjà entièrement inclus dans l’autre
  ne justifie pas un préfixe dupliqué.

Les alias non typés, les titres de deux œuvres différentes et les sources incohérentes
ne permettent pas cette composition. Aucun rapprochement approximatif ni traduction
n’est transformé en preuve officielle. La paire exacte est exposée dans les diagnostics,
avec sa provenance, via `official-composed-title-exact` et les deux titres utilisés.
Elle compte comme une preuve de titre, jamais comme deux votes indépendants.

Les seuils, poids de confiance, contrôles d’année/type/nombres/épisodes, compétition
entre identités, verrous et corrections manuelles restent en vigueur. Le parser,
le générateur de requêtes, les clients HTTP et le moteur historique ne sont pas modifiés.

## Années finales : ambiguïté conservée

Le relevé avant modification montre que les quatre libellés **exacts** fournis sont
parsés avec le dernier nombre encore dans le titre et `Year = null`. Le parser ne
transforme pas automatiquement un nombre terminal nu en année.

Lorsqu’un préfixe est exactement prouvé par les titres officiels et que le nombre
final correspond à la date provider, V2 peut désormais signaler une **hypothèse**,
avec `trailing-year-needs-independent-evidence`. Ce traitement ne sélectionne jamais
une identité : confiance .90, décision ambiguë. La requête et son niveau de preuve
ne sont pas modifiés ; `TrailingYearHypothesis` est explicitement exposé.
Une année identique seule ne suffit pas : la totalité du préfixe doit être prouvée.
Si la date provider ne correspond pas ou si cette preuve manque, pas de découpage.

## Comparaison réellement exécutée

Avant : sorties V2.3-H figées sur les quatre fixtures avant modification.
Après : mêmes libellés, mêmes identités et mêmes textes provider simulés.

| Libellé fourni | Proposition | Avant | Après |
| --- | --- | --- | --- |
| Le.Festin.Nu.Naked.Lunch.1991 | movie:2742 | reject | ambiguous |
| Mutant.Forbidden.World.1982 | movie:42251 | reject | ambiguous |
| Epic.LA.BATAILLE.DU.ROYAUME.2013 | movie:116711 | reject | ambiguous |
| Society.on.the.Moors.Topical.Budget.522.1.1921 | movie:1173160 | reject | ambiguous |

**Aucune des quatre formes nues n’est automatiquement acceptée.** Dans les tests
additionnels où `.BluRay` fournit déjà au parser un contexte lexical suffisant pour
extraire l’année, les quatre fixtures sont acceptées avec les preuves provider
requises. Pour les trois premières, une paire originale/localisée complète peut
justifier la composition ; le quatrième cas utilise un titre officiel unique complet.

Pour Epic, deux scénarios sont testés séparément : si le titre localisé reçu est déjà
`Epic : La Bataille du royaume`, il s’agit d’une égalité avec un seul titre officiel,
pas d’une composition inventée. Si les deux appellations reçues sont `Epic` et
`La Bataille du royaume`, la paire peut fournir la preuve complète. Les données Beta
manquent pour affirmer lequel de ces scénarios était effectivement reçu.

## Numérotation 522.1 / 522-1

La normalisation stricte existante produit déjà les mêmes deux tokens `522`, `1`
pour point, tiret ou espace. Aucun nouveau collage/split de chiffres n’est ajouté.
Les zéros initiaux, l’ordre et chaque chiffre sont conservés : `5221`, `522.01`,
`0522.1`, `522.2`, `1.522` restent contradictoires avec `522-1`.
Un garde-fou V2 distingue en outre les fractions, ratios, virgules et opérateurs
numériques encore disponibles dans les textes, au lieu de les assimiler au tiret.
Le normaliseur global et le parser restent inchangés.

La fixture ne valide cette équivalence que si le **titre officiel complet** contient
bien `Topical Budget 522-1`. Si le provider ne fournit que `Society on the Moors`,
le suffixe de catalogue n’est pas supprimé arbitrairement : rejet conservé. Aucun
nouveau champ provider ni liste de titres alternatifs n’est fabriqué pour le résoudre.

## Masquage des diagnostics

Le filtre existant masquait intégralement tout texte contenant une URL, slash/chemin,
`@`, domaine apparent ou mot comme `secret`, `token`, `bearer`. Il peut donc aussi
masquer un titre légitime tel que `Secret Society` ou `Movie / Alternate`.
Aucun des quatre titres localisés simples des fixtures ne déclenche ce filtre.
Sans les textes effectivement reçus en Beta, leur éventuel masquage précis n’est
pas reproductible et aucune cause spécifique n’est inventée.

La politique de confidentialité reste **identique**. Les diagnostics exposent
maintenant le motif fixe (`credential_keyword`, `path_or_separator`, `url`,
`email_or_at_sign`, `domain_like_text`), jamais le fragment masqué ou une valeur secrète.
Le panneau affiche ce motif, la paire de titres utilisée et l’avertissement d’année
hypothétique. Les faux positifs de masquage restent masqués par prudence.

## Validation et limites

- **231 tests backend ciblés réussis**, dont **49 nouvelles régressions** : quatre
  libellés nus et datés lexicalement, provenance, deux œuvres, suffixes arbitraires,
  mauvais remake/année/type, épisodes, identités proches et protégées, numéros,
  réponses partielles et confidentialité. Tests V2.3-G/H et contrats vidéo conservés.
- **6 tests frontend ciblés réussis**, incluant rendu échappé des preuves et motifs.
- Build solution Release : **0 avertissement, 0 erreur**. Build Vite et lint ciblé verts.
- `git diff --check` propre. Pas de suite backend complète relancée pour ce lot ciblé.
- Copie SQLite privée inchangée : SHA-256
  `23a3d0a355958f2eec8ed562c524282c5b5259a5ff70060991e23bedda7b159d`.

Aucune régression de décision détectée dans les tests existants, aucun gain de précision
réelle annoncé. L’acceptation des formes nues, les titres officiels réellement reçus,
l’éventuel texte de catalogue absent et les masquages Beta restent à vérifier sur
preuves suffisantes. Le code ne réassocie rien, n’écrit pas de données métier et
n’effectue aucune recherche supplémentaire.
