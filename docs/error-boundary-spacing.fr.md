# Espacement des états d’erreur — beta

Intervention dans `/private/tmp/feedarr-wizard-beta`, en conservant les modifications du wizard déjà présentes. Aucun commit, push ou déploiement.

## Cause

`Shell.jsx` rend ses pages dans `.content`, qui possède volontairement `padding: 0`. Les pages normales apportent leur propre conteneur `.page` et donc leurs espacements : 16 px sur desktop, 12/10 px sous 768 px, puis 10/8 px sous 480 px, avec conservation du padding inférieur lié à la zone sûre mobile.

Lorsque `ErrorBoundary` intercepte une erreur du composant de page ou d’un import dynamique, il remplaçait toute la page par un `.errorbox` avec seulement une marge verticale inline. Le conteneur `.page` disparaissait et aucun espacement horizontal ne le remplaçait.

`RouteErrorBoundary` avait déjà son conteneur `.page` : il conserve ses espacements. La navigation et `Shell.jsx` ne nécessitent aucun changement.

## Correction centralisée

- `src/Feedarr.Web/feedarr-web/src/ui/ErrorBoundary.jsx` : le fallback par défaut est enveloppé dans `.error-boundary`. Message, titre et bouton Réessayer sont conservés, ainsi que le comportement des fallbacks personnalisés.
- `src/Feedarr.Web/feedarr-web/src/styles/styles.css` : les fallbacks directement sous `.content` ou `#root` partagent exactement les mêmes déclarations de padding que `.page`, y compris aux deux breakpoints existants. La carte n’ajoute pas de marge supérieure dans ce cas. Une erreur imbriquée dans une page conserve sa marge verticale habituelle et ne reçoit aucun padding supplémentaire. Les messages longs peuvent revenir à la ligne.
- `src/Feedarr.Web/feedarr-web/src/ui/RouteErrorBoundary.jsx` : ajout du marqueur `.page--error` pour appliquer le même retour à la ligne aux détails, sans ajouter de padding. Recharger et Retour à la bibliothèque sont conservés.

Aucune modification de la structure de navigation, de `Shell.jsx`, du routeur, des espacements des pages normales ou de l’apparence des encadrés.

## Vérifications

Nouveaux tests dans `src/Feedarr.Web/feedarr-web/src/ui/__tests__/ErrorBoundary.layout.test.mjs` :

- Erreur de rendu React, navigation conservée, message affiché et bouton Réessayer fonctionnel.
- Import dynamique rejeté, fallback commun et action de réessai présents.
- Erreur de route, message et bouton Recharger présents ; retour à la bibliothèque fonctionnel.
- Comparaison des paddings des pages normales, des erreurs de page, des erreurs racine et des erreurs de route à 1440, 900, 768, 375 et 320 px.
- Absence de padding ajouté aux erreurs imbriquées, absence de marge supérieure cumulée sur la carte de fallback pleine page et retour à la ligne des messages longs.

Résultats :

- Tests ciblés : **8/8 réussis**.
- `npm test` : **440 réussis, 0 échec**, incluant les régressions du wizard.
- `npm run lint -- --max-warnings=0` : réussi.
- `npm run build` : réussi.
- `git diff --check` : réussi.

Les comparaisons responsive utilisent les règles CSS réelles, avec résolution explicite des media queries de largeur et une zone sûre mobile nulle dans jsdom. Elles vérifient les espacements calculés, pas une disposition pixel. Le contrôle navigateur étant indisponible dans cette session, aucune capture visuelle desktop/tablette/mobile n’est attestée ; une vérification visuelle finale reste à effectuer.
