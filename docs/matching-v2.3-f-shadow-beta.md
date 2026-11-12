# V2.3-F — Shadow observable en Beta

## Utilisation

Ouvrir **Paramètres → Maintenance → Matching V2 Shadow — Beta**.

- **Activer Matching V2 Shadow** : effet immédiat, sans redémarrage.
- **Arrêter immédiatement** : bloque les prochaines évaluations et toute publication
  tardive d’une évaluation déjà commencée. Le traitement historique en cours continue normalement.
- **Remettre les statistiques à zéro** : efface la fenêtre et invalide les publications
  en vol ; conserve l’état ON/OFF.
- **Actualiser** : consultation manuelle ; le panneau actualise aussi toutes les 10 secondes.

Le réglage n’est écrit ni dans SQLite ni dans la configuration. Il revient toujours
à **OFF après redémarrage**, même si une ancienne configuration comportait
`App:MatchingShadowEnabled=true`. Cette ancienne option du lot E est supprimée.
Aucune migration et aucune activation automatique.

## Fenêtre et inspection

Réutilisation de la file bornée à **64 observations**. Les chiffres concernent cette
fenêtre, pas toute la durée de fonctionnement : releases distinctes observées et
évaluées, évaluations exploitables, accept/ambiguous/reject/not_evaluable, divergences
et durée moyenne. Plusieurs observations d’une même release comptent séparément
pour les décisions ; les compteurs de releases sont dédupliqués dans la fenêtre.

La durée mesure l’évaluation locale, relecture des protections persistantes et
classement inclus ; elle exclut HTTP, téléchargement, formatage des diagnostics et
rendu UI. La moyenne inclut aussi les observations `not_evaluable`. Aucun taux de
précision ni gain de matching n’est déduit de ces chiffres.

L’inspection affiche le titre brut filtré, la décision historique au point de sélection
TMDB, ses score et identité, la décision V2, ses requêtes candidates avec le niveau
de preuve de l’année, les candidats réellement classés, les identités typées
`tmdb:movie:ID` / `tmdb:tv:ID`, scores, raisons et contradictions.
Le score historique et la confiance V2 utilisent leurs conventions existantes :
ils ne constituent pas des probabilités comparables.

Une divergence signifie une différence accept/reject/ambiguous sur une évaluation
exploitable, ou une autre identité acceptée. Les caches, protections manuelles et
réponses incomplètes ne deviennent pas artificiellement des divergences. Un résultat
`not_evaluable` peut donc ne comporter aucun candidat classé. Le filtre des divergences
est actif initialement et peut être désactivé pour voir toutes les observations.

## Sécurité et concurrence

API :

- `GET /api/diagnostics/matching-shadow` : état, génération et fenêtre.
- `PUT /api/diagnostics/matching-shadow` avec `{ "enabled": true, "generation": G }`
  où G est la génération actuellement lue. Une activation périmée reste sans effet.
- Même PUT avec `{ "enabled": false }` : arrêt inconditionnel.
- `POST /api/diagnostics/matching-shadow/reset` : réinitialisation en mémoire.

Les routes passent par l’authentification existante et la protection anti-CSRF globale.
Le contrôleur exige le marqueur serveur d’authentification et refuse les jetons de
bootstrap d’installation. Il respecte la politique d’accès configurée par Feedarr
(y compris les modes sans authentification imposée) ; il ne crée aucune exemption.
Les réponses sont `Cache-Control: no-store` et `Pragma: no-cache`.

La projection ne contient aucun objet source/indexeur, URL de requête, configuration,
poster URL, token, exception ou réponse provider brute. Seuls les titres et champs
explicitement sélectionnés sont exposés. Un titre ressemblant à une URL, domaine,
chemin, email ou credential est entièrement masqué ; les autres textes sont limités
à 512 caractères et affichés comme texte React, jamais comme HTML. Les noms/titres
qui ressemblent à ces motifs peuvent donc être masqués par prudence.

Les générations invalident les publications en cours et les activations périmées,
y compris après une remise à zéro ou un arrêt déjà OFF. La projection textuelle
s’effectue hors du verrou de la file : l’arrêt n’attend pas son formatage.
La relecture SQLite reste strictement ReadOnly et query_only. Le shadow n’a aucune
capacité d’écriture d’association ni d’appel HTTP. L’arrêt ne tente pas d’annuler
les requêtes du moteur historique.

## Couverture et limites

Couverture du lot E conservée : sélection TMDB fraîche dans `FetchPosterCoreAsync`,
retours de cache et refresh manuel. Pas d’instrumentation nouvelle des autres
providers ou des autres méthodes de refresh. Aucune recherche additionnelle pour
une variante, une page ou un provider. Une requête non couverte ou une couverture
TMDB non prouvée produit `not_evaluable`.

Les identités établies et corrections manuelles restent protégées ; leur présence
n’est pas utilisée comme vérité terrain. Le choix historique observé est celui des
IDs au point d’intégration, pas une garantie du provider final du poster après fallback.
Aucune modification du parser ou du ranker. Aucun rematching ni reparse global.

## Validation

- Tests ciblés : 45 tests shadow réussis après les dernières vérifications de
  projection/concurrence ; génération/classement et requêtes historiques également verts.
- 20 tests du nouveau diagnostic : OFF/ON, générations, arrêt/reset, concurrence,
  fenêtre bornée, compteurs, durée, confidentialité, authentification et anti-CSRF.
- 3 tests frontend ciblés : identités typées, compteurs et rendu réel React avec
  injection HTML échappée et conservation du niveau `Hypothesis`.
- Build Vite et lint des trois nouveaux modules UI réussis.
- Build API Release réussi, sans avertissement ni erreur.
- Suite backend complète : **1 816 tests réussis**, une seule exécution (environ 61 s).
- Charge concurrente : 8 workers × 300 itérations (publication/lecture/reset/ON-OFF),
  puis 100 publications séquentielles ; file toujours bornée et arrêt respecté.
  Il s’agit d’un contrôle local de concurrence et de coût borné, pas d’un benchmark
  de latence en production ni d’une mesure de précision.
- `git diff --check` propre. Copie SQLite privée inchangée (SHA-256 identique au lot E).

Aucune donnée privée ajoutée au suivi Git. Aucun accès NAS, production ou provider.
Aucun commit/push automatique.
