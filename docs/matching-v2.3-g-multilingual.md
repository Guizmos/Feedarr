# V2.3-G — Titres TMDB multilingues en Shadow

## Diagnostic ciblé

L’historique appelle Search sans locale explicite, puis avec la locale des métadonnées
UI. Le scorer compare le titre principal et le titre original, avec sa normalisation
historique. `DedupCandidates` conserve ensuite la première ligne par type/ID : la
ligne localisée suivante n’était donc pas transmise au Shadow. Le ranker V2 ne voyait
que le titre de cette première ligne et son titre original.

Distinctions conservées :

- `title` / `name` : titre principal de **cette réponse** Search.
- `original_title` / `original_name` : titre original, avec `original_language`.
- Locale demandée pour la réponse : absente pour la première requête, puis celle
  réellement configurée dans Feedarr. Ce n’est pas `original_language`.
- Un titre de réponse à une requête `fr-FR` peut être un fallback : son texte n’est
  jamais traduit ou déclaré linguistiquement français par le moteur.
- Les listes complètes de titres alternatifs officiels ne sont pas disponibles dans
  les objets Search consommés ici. Aucun appel `alternative_titles` n’est ajouté.

## Correctif

Le scope Shadow conserve désormais les lignes Search déjà reçues **avant** la
déduplication historique, avec leur type d’endpoint et locale demandée. Le résultat
renvoyé au moteur historique et sa déduplication restent inchangés.

L’adaptateur regroupe les titres par `tmdb + movie/tv + ID`. Il transporte au ranker
des preuves explicites : texte, identité typée, nature (réponse/localisé/original/
alternatif), source et locale. Les années divergentes restent des lignes distinctes
pour que le ranker conserve sa détection de conflits. Un titre de série ne peut pas
être emprunté au film ayant le même ID numérique.

Le ranker reconnaît les preuves de titre officiel uniquement pour la même identité
et une source cohérente. Une égalité normalisée fournit une raison positive explicite
`official-*-title-exact`. Une ressemblance approximative reste une simple similarité,
jamais une traduction officielle ni une nouvelle preuve d’identité. Les titres
alternatifs disposent d’un contrat explicite `AlternativeTitlesEndpoint`, testé
avec des données simulées ; l’adaptateur Search n’en fabrique aucun. Le champ historique
`AlternativeTitles` du ranker reste compatible avec ses appelants existants ; aucune
traduction devinée n’y est injectée par le Shadow.

Aucun seuil global modifié. Année, type, nombres, épisodes, hypothèses et identités
concurrentes restent contrôlés. Les preuves répétées pour une identité ne deviennent
pas des votes indépendants. La capture est bornée à 100 lignes ; un dépassement
invalide la couverture au lieu de masquer des concurrents.

Le panneau Beta affiche les titres officiels utilisés, leur nature, source et locale,
avec le filtrage de confidentialité et le rendu texte React déjà présents.

## Comparaison des fixtures Beta

Les identités ci-dessous sont les **propositions utilisateur**, pas des références
indépendamment validées. Les réponses localisées des tests sont explicitement
**simulées** ; ce ne sont pas des captures TMDB réelles. La colonne finale est
conditionnelle à la présence de ce titre dans une réponse officielle pour cette
identité, avec année et type cohérents.

| Titre de release | Proposition TMDB | V2-F, ligne anglaise seule | V2-G, anglais seul | V2-G, titre localisé présent |
| --- | --- | --- | --- | --- |
| Le Robot sauvage (2024) | movie:1184918 | reject | reject | accept |
| Le Magicien d'Oz (1939) | movie:630 | reject | reject | accept |
| Massacre à la tronçonneuse (1974) | movie:30497 | reject | reject | accept |
| Ghost Rider : L'Esprit de vengeance (2011) | movie:71676 | reject | reject | accept |
| L'Emprise des ténèbres (1988) | movie:11503 | reject | reject | accept |
| Coup de foudre pour le fils du Père Noël (2023) | movie:1180796 | reject | reject | accept |
| Paradis express (1997) | movie:158 | reject | reject | accept |

Les sept rejets sont corrigés **dans ces scénarios de preuve simulée** ; sept restent
rejetés lorsque cette preuve manque. Les trois autres divergences parmi les dix
annoncées ne sont pas identifiées dans la demande et aucun export Shadow les
contenant n’a été trouvé dans les artefacts locaux examinés. Elles ne sont ni
inventées ni déclarées rejouées. Aucun taux de précision ou gain réel Beta n’est calculé.

Autres résultats : original anglais et plusieurs titres officiels reconnus ; faux
alias, source incohérente, mauvaise année/type et numéros contradictoires rejetés ;
remake écarté par l’année ; concurrents comparables et années provider incohérentes
ambiguës. Couverture partielle et identité manuelle restent `not_evaluable`.
Aucun nouveau cas devenu non évaluable sur les sept fixtures complètes ; aucun
changement de décision involontaire détecté dans les tests ciblés existants.

## Validation et coût

- **146 tests ciblés réussis** : multilingue, génération/ranking, protections Shadow,
  contrats vidéo historiques et requêtes issues du parser.
- **3 tests frontend réussis**, dont rendu React des preuves et échappement HTML.
- Build API Release : 0 avertissement, 0 erreur. Build Vite et lint du composant modifié verts.
- `git diff --check` propre. Aucune suite backend complète relancée dans ce lot ciblé.
- Micro-mesure isolée : 2 000 classements sur la même requête et un seul candidat,
  après échauffement : champs historiques **9,13 µs / 8 536 octets** par appel,
  ajout d’un titre officiel **11,69 µs / 8 832 octets** (delta **2,56 µs / 296 octets**).
  Comparaison marginale dans le nouveau binaire, pas chronométrage de l’ancien binaire
  ni benchmark du pipeline complet. Les exécutions concurrentes aux builds variaient
  davantage ; aucun engagement de latence en production n’en est déduit.

## Garanties et limites

Shadow uniquement, OFF au démarrage. Pas de nouvelle recherche, traduction,
téléchargement ou écriture d’association. Parser et décisions historiques inchangés.
Protections persistantes et corrections manuelles conservées. Aucune migration.
La copie SQLite privée conserve son SHA-256 du lot F ; les tests SQLite utilisent
seulement des bases temporaires. Aucun NAS, production ou provider externe contacté.

La véritable validation multilingue exige encore les réponses effectivement reçues
et des identités indépendamment validées. Ce lot corrige leur transport et leur
évaluation, sans prétendre valider les propositions Beta à distance.
Aucun commit/push.
