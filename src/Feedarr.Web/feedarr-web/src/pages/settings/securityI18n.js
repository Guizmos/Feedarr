import { getActiveUiLanguage } from "../../app/locale.js";
import { tr } from "../../app/uiText.js";

const SECURITY_TEXT = {
  "settings.security.title": { fr: "Authentification", en: "Authentication" },
  "settings.security.authMode": { fr: "Mode d'authentification", en: "Auth mode" },
  "settings.security.authMode.none": { fr: "Aucune", en: "Open" },
  "settings.security.authMode.smart": { fr: "Smart", en: "Smart" },
  "settings.security.authMode.strict": { fr: "Stricte", en: "Strict" },
  "settings.security.authMode.open": { fr: "Aucune", en: "Open" },
  "settings.security.help.smartExposure": {
    fr: "Le mode Smart protège automatiquement quand l'instance est exposée.",
    en: "Smart mode automatically protects when the instance is exposed.",
  },
  "settings.security.publicBaseUrl": { fr: "URL publique de base", en: "Public base URL" },
  "settings.security.status": { fr: "Statut", en: "Status" },
  "settings.security.status.configured": { fr: "Configuré", en: "Configured" },
  "settings.security.status.notConfigured": { fr: "Non configuré", en: "Not configured" },
  "settings.security.status.authRequired": { fr: "Auth requise", en: "Auth required" },
  "settings.security.status.authNotRequired": { fr: "Auth non requise", en: "Auth not required" },
  "settings.security.username": { fr: "Nom d'utilisateur", en: "Username" },
  "settings.security.username.placeholder": { fr: "Saisir un nom d'utilisateur", en: "Enter username" },
  "settings.security.password": { fr: "Mot de passe", en: "Password" },
  "settings.security.password.placeholderKeepCurrent": {
    fr: "Laisser vide pour conserver le mot de passe actuel",
    en: "Leave blank to keep current password",
  },
  "settings.security.password.placeholderEnter": { fr: "Saisir un mot de passe", en: "Enter password" },
  "settings.security.confirm": { fr: "Confirmation du mot de passe", en: "Password confirmation" },
  "settings.security.confirm.placeholder": { fr: "Confirmer le mot de passe", en: "Confirm password" },
  "settings.security.notice.credsRequired": {
    fr: "Identifiants requis : en mode Smart/Strict quand l'authentification est requise (URL publique ou proxy), renseigne un nom d'utilisateur et un mot de passe.",
    en: "Credentials required: in Smart/Strict mode when authentication is required (public URL or proxy), set username and password.",
  },
  "settings.security.notice.credsExisting": {
    fr: "Identifiants déjà configurés. Laisse vide pour conserver le mot de passe actuel.",
    en: "Credentials already configured. Leave blank to keep the current password.",
  },
  "settings.security.warning.downgradeOpen": {
    fr: "Passer en mode Open désactive l'authentification. Confirme la désactivation pour enregistrer.",
    en: "Switching to Open mode disables authentication. Confirm the downgrade to save.",
  },
  "settings.security.error.passwordComplexityFallback": {
    fr: "Mot de passe trop simple : minimum 12 caractères avec au moins une majuscule, une minuscule, un chiffre et un caractère spécial.",
    en: "Password is too weak: minimum 12 characters with at least one uppercase, one lowercase, one digit, and one special character.",
  },
  "settings.security.error.passwordAndConfirmationRequired": {
    fr: "Mot de passe et confirmation requis.",
    en: "Password and confirmation are required.",
  },
  "settings.security.error.passwordConfirmationMismatch": {
    fr: "La confirmation du mot de passe ne correspond pas.",
    en: "Password confirmation does not match.",
  },
  "settings.security.error.passwordComplexityPrefix": {
    fr: "Mot de passe trop simple : minimum",
    en: "Password is too weak: minimum",
  },
  "settings.security.error.passwordComplexityWithAtLeast": {
    fr: "avec au moins",
    en: "with at least",
  },
  "settings.security.error.passwordClause.upper": { fr: "une majuscule", en: "one uppercase letter" },
  "settings.security.error.passwordClause.lower": { fr: "une minuscule", en: "one lowercase letter" },
  "settings.security.error.passwordClause.digit": { fr: "un chiffre", en: "one digit" },
  "settings.security.error.passwordClause.symbol": { fr: "un caractère spécial", en: "one special character" },
  "settings.security.modal.disableAuth.title": { fr: "Désactiver l'authentification ?", en: "Disable authentication?" },
  "settings.security.modal.disableAuth.message": {
    fr: "Passer en mode Open désactive l'authentification et supprime les identifiants.",
    en: "Switching to Open mode disables authentication and removes credentials.",
  },
  "settings.security.modal.cancel": { fr: "Annuler", en: "Cancel" },
  "settings.security.modal.confirm": { fr: "Confirmer", en: "Confirm" },
  "settings.security.subbar.info": { fr: "Infos", en: "Info" },
  "settings.security.infoModal.title": { fr: "Modes d'authentification", en: "Authentication modes" },
  "settings.security.infoModal.none.title": { fr: "Aucune", en: "Open" },
  "settings.security.infoModal.none.description": {
    fr: "Aucune authentification. Tout le monde peut accéder à l'interface et à l'API.",
    en: "No authentication. Anyone can access the UI and the API.",
  },
  "settings.security.infoModal.smart.title": { fr: "Smart", en: "Smart" },
  "settings.security.infoModal.smart.description": {
    fr: "Authentification activée seulement si l'instance est exposée (URL publique / proxy). En local, peut rester sans authentification.",
    en: "Authentication enabled only when the instance is exposed (public URL / proxy). Locally, it may stay without authentication.",
  },
  "settings.security.infoModal.strict.title": { fr: "Stricte", en: "Strict" },
  "settings.security.infoModal.strict.description": {
    fr: "Authentification toujours activée, même en local. Recommandé en production.",
    en: "Authentication always enabled, even locally. Recommended for production.",
  },
  "settings.security.infoModal.note": {
    fr: "Les identifiants existants peuvent être conservés : laisse les champs mot de passe vides pour garder le mot de passe actuel.",
    en: "Existing credentials can be kept: leave password fields empty to keep the current password.",
  },
  "settings.security.infoModal.close": { fr: "Fermer", en: "Close" },
};

export function tSecurity(key, uiLanguage = getActiveUiLanguage()) {
  const entry = SECURITY_TEXT[key];
  if (!entry) return key;
  return tr(entry.fr, entry.en, uiLanguage);
}

export function getSecurityText(uiLanguage = getActiveUiLanguage()) {
  return (key) => tSecurity(key, uiLanguage);
}
