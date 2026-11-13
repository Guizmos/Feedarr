# Lot 1 — Compte obligatoire et politique de transport

Travail limité à `beta`, dans `/private/tmp/feedarr-wizard-beta`, sans commit, push, déploiement ni données réelles.

## Architecture finale

Toute instance exige un administrateur et toute API privée exige son authentification, sans exemption d’IP ou de réseau. Les anciens modes Open / Smart / Strict ne sont plus proposés ni acceptés par l’API de modification des paramètres.

Deux politiques dans **Paramètres → Sécurité** : **HTTP et HTTPS autorisés** (défaut des nouvelles instances), ou **HTTPS obligatoire**. Le premier compte, la connexion automatique, la déconnexion/reconnexion et le redémarrage fonctionnent en HTTP direct par IP. Un bandeau rouge sous le header indique que HTTP n’est pas chiffré.

Le cookie reste HttpOnly et SameSite=Lax ; Secure dépend du transport réel reconnu par ASP.NET. Les sessions créées en HTTPS et celles des anciennes instances restent inutilisables en HTTP. Les protections CSRF, origines, limitation des tentatives et confiance explicite des proxies sont conservées. La politique HTTPS refuse les accès HTTP privés, y compris avec des identifiants Basic valides.

### Première création sur NAS

Ouvrir Feedarr par son IP en HTTP ou HTTPS, choisir un identifiant, un mot de passe et sa confirmation. Aucun bootstrap, secret, jeton ou code Docker n’est demandé. Une requête unique crée atomiquement le compte et la session ; toute création ultérieure est refusée, y compris après redémarrage.

La première personne qui accède à une instance vierge peut créer son administrateur : c’est la politique retenue. En HTTP, les identifiants et le cookie circulent sans chiffrement ; le bandeau l’indique.

### Activation HTTPS et récupération

Activer « HTTPS obligatoire » depuis une session ouverte via HTTPS fonctionnel, directement sur Kestrel ou derrière un proxy explicitement reconnu. Une requête HTTP, même munie d’un faux `X-Forwarded-Proto`, ne suffit pas. La confirmation d’une restauration qui activerait HTTPS applique le même contrôle. Les anciennes options `RequireHttpsForBasicAuth`, `TrustedReverseProxyTls` et `App:Security:EnforceHttps` ne pilotent plus cette politique ; conserver les proxies/réseaux connus pour la reconnaissance du transport.

Si TLS devient indisponible : définir temporairement `Security__AllowHttpRecovery=true` sur le serveur, redémarrer, se connecter en HTTP avec le compte existant, puis corriger HTTPS ou choisir « HTTP et HTTPS autorisés ». Retirer ensuite cette variable et redémarrer. Cette récupération ne contourne jamais l’authentification ; le bandeau HTTP signale aussi son activation.

## Migration et compatibilité

La migration `0061` est conservatrice : **Smart / Strict → HTTPS obligatoire**, **Open → HTTP et HTTPS autorisés avec compte obligatoire**. Une ancienne instance protégée qui utilisait HTTP doit donc disposer de HTTPS ou utiliser explicitement la récupération serveur ; aucun affaiblissement silencieux. Une instance Open sans compte propose directement la première création.

Les comptes, données et anciennes sessions sont conservés. `0061` ajoute une contrainte de transport aux sessions et une trace SQL du mapping ; elle ne réécrit pas le JSON de sécurité ni sa génération de révocation. L’empreinte d’une restauration déjà confirmée reste ainsi identique. Les anciennes archives sont normalisées à la lecture ; une restauration sans identifiants utilisables conserve l’administrateur actuel. Les changements d’identifiants ou de politique restent soumis aux confirmations de restauration existantes.

La suppression du wizard est conservée : création → application, transition CSS avec `prefers-reduced-motion`, bibliothèque vide avec accès aux sources et à la restauration, configuration libre, restauration toujours accessible dans les Paramètres. `onboardingDone` ne gouverne plus l’accès ; ses données de compatibilité restent disponibles pour les sauvegardes.

## Simplifications

Suppression du service de blocage TLS au démarrage, des exemptions réseau/Open et des autorisations bootstrap des API privées, des projections de modes et de leur interface, des effets/modales de sécurité associés, et du helper de traduction devenu inutilisé. `AuthenticationPolicy` remplace `SmartAuthPolicy`. Les champs historiques ne subsistent que pour la lecture des archives ; aucun assistant de remplacement ni bibliothèque d’animation.

Le lot précédent avait supprimé SetupWizard, SetupShell, SetupAccess, les étapes, la progression locale, les événements et styles associés. Les mécanismes de restauration nécessaires sont conservés.

## Validations réellement exécutées

- Compilation backend Release : zéro erreur et avertissement ; build frontend et lint réussis.
- Suite frontend : **379 tests réussis**.
- Backend ciblé Auth/Sessions/Sécurité/Sauvegardes/restauration : **190 tests réussis**, dont la régression de restauration confirmée avant migration, rejoués après suppression du bootstrap initial.
- **4 scénarios d’intégration réussis**, avec ASP.NET en Production, SQLite temporaire, HTTP direct sur `127.0.0.1` et véritable TLS Kestrel avec certificat éphémère : création/connexion automatique et pages React ; logout/login/redémarrage ; activation HTTPS depuis HTTPS et refus HTTP privé ; perte de TLS et récupération authentifiée ; migrations Smart/Strict/Open avec ancienne session conservée ; deuxième création et créations concurrentes limitées à un seul succès.
- Contrôles supplémentaires : CSRF, cookies selon transport, faux header HTTPS, restauration depuis une bibliothèque vide, maintien des champs au retour d’onglet, absence des secrets de test dans les logs.
- `git diff --check` réussi.

Les pages React sont exécutées sous **jsdom** : aucune validation visuelle, animation ou BFCache dans un navigateur réel n’est revendiquée. Le certificat de recette est auto-signé ; le client de test accepte explicitement ce certificat. La configuration d’un reverse proxy de production n’a pas été exécutée.

Bilan de la précédente finalisation des politiques de transport, par rapport à l’état du worktree au début de la demande, hors rapports : **673 lignes ajoutées / 4036 supprimées**, tests inclus.

## Dernière simplification : aucune autorisation bootstrap initiale

Suppression du contrôle de secret du endpoint de création, du drapeau `initialAuthorizationRequired`, du champ et des messages correspondants, et de l’argument/header bootstrap du transport frontend. Les utilitaires bootstrap encore référencés par les anciennes restaurations sont conservés ; aucun autre moteur de Feedarr n’est modifié.

Le backend de recette démarre sans variable de secret bootstrap. Le scénario HTTP couvre le formulaire sans secret, la connexion automatique, l’accès à la bibliothèque et à la restauration, logout/login, redémarrage avec session conservée et refus d’un deuxième compte. Le scénario TLS crée aussi directement le premier compte en HTTPS. La concurrence reste limitée à un succès et un conflit. Les mots de passe hashés, CSRF, sessions et limites de tentatives sont conservés.
