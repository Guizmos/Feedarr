# P7 — Profil Équilibré contextualisé et préparation beta

Implémentation du 26 novembre 2026 après validation du rapport de calibration P7. Les résultats de calibration restent historiques ; aucune nouvelle couverture réelle n'est annoncée.

## Décision finale

Le profil 0,85 / 0,90 / 0,05 s'applique uniquement à la décision vidéo finale : titre provider rattaché à l'identité, type concordant, année de l'œuvre extraite et exacte ou preuve TV structurelle vérifiée pour le contexte demandé. Une saison/épisode/date demandée empêche de substituer une simple année à sa preuve TV. Les jeux et les candidats insuffisamment corroborés conservent la politique existante. Les contradictions fortes restent des refus.

L'exigence d'égalité lexicale est levée dans ce même périmètre. Le pool entier continue de participer à la décision ; aucun concurrent fort n'est tronqué. La sélection des candidats à enrichir, les plafonds, budgets et plans P6B conservent leur politique. L'activation est interne au décideur, sans nouveau réglage utilisateur.

Un titre de programme exact, officiellement rattaché et corroboré par une identité intercatalogues vérifiée peut recevoir le poster du programme malgré un sujet d'épisode inconnu. Le diagnostic devient `programme-subject-programme-identity-warning`. Cette règle ne s'applique pas à une saison/épisode/date explicitement demandée sans preuve et ne fabrique aucune identité d'épisode.

La marge 0,05 ne départage pas les identités exactes à 1 et 0,96 : Bruised/Whistle restent soumis à P6A et à leurs contradictions de première originale. Les homonymes exacts restent ambigus. Les identités établies, corrections manuelles, éditions/DLC de jeux, portée des entités et priorité des posters FR sont préservées.

Le corpus Replay transporte facultativement la politique utilisée. Un ancien export sans cette propriété conserve sa politique historique ; un export contextualisé se rejoue avec les mêmes seuils.

## Corrections découvertes pendant la validation complète

- P6A : zéro candidat à l'année exacte est distingué de plusieurs identités à cette année. Le premier cas conserve un matching historique admissible à une année voisine ; le second conserve l'ambiguïté. Le test de matérialisation The Matrix couvre cette non-régression.
- Deux assertions P0 étaient antérieures aux corrections P2/P5 : une animation dont l'année est désormais correctement extraite n'est plus un cas de routage sans preuve ; une saison forte peut router en TV tant qu'aucune identité movie établie ne la contredit. Les tests ont été actualisés avec de véritables absences de preuve/contradictions, sans modifier les protections du moteur.
- Le contexte Docker exclut explicitement les diagnostics locaux, répertoires de credentials, bases annexes, fichiers `.env`, clés et lanceur `dev.sh`.

## Validation avant push

- Suite backend complète Release : **2 716 réussis, zéro échec**.
- Suite frontend complète : **346 réussis**, zéro échec.
- Intégration HTTP/TLS/administrateur initial : **7 réussis**, zéro échec.
- Lint sans avertissement, garde du domaine catégories et build web : réussis.
- Publication API Release et construction locale du Dockerfile : réussies ; démarrage du conteneur jetable et `/health` vérifiés.
- Audits NuGet transitif et npm : aucune vulnérabilité signalée.
- Migrations 0064/0065 : suite de migration complète, migration ascendante des sources existantes, SQL additionnel sur base mémoire, conservation des anciennes lignes, intégrité et clés étrangères vérifiées. Les archives de configuration historiques conservent leurs champs ; RSS est une extension facultative.
- Workflow beta limité à `beta`, plateformes `linux/amd64,linux/arm64`, tags Docker `beta` et `beta-<sha>`. Le workflow stable exige un tag `v*` et une provenance `main` ; aucun tag n'est créé ici.
- Contrôle des fichiers candidats et des commits locaux précédents : aucun secret reconnu, base, log, cache ou artefact de build à publier. `dev.sh` reste local et hors commit. `git diff --check` vérifié.

Les modifications RSS direct présentes dans le lot sont incluses avec leurs protections SSRF, tests, migrations additives et compatibilité Torznab. Aucune instance de production n'est contactée. La publication distante et son digest multi-architecture sont confirmés séparément après le push, pas déduits de ces validations locales.
